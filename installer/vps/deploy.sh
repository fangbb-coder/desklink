#!/usr/bin/env bash
# DeskLink VPS 部署脚本：构建 Linux 二进制 → 安装到 /opt/desklink → 装 systemd 单元 → 防火墙。
#
# 设计约束（来自 DESIGN.md「部署」章节）：
#   - **registry 绝不暴露公网**：只监听 127.0.0.1，仅由 relay 经 loopback 访问。
#   - 只对外开放 relay 的 HTTPS/QUIC 入口（默认 443，TCP+UDP 同号）。
#   - registry 与 relay 之间用 unix socket 推送撤销/公钥变更事件（即时踢线）。
#   - 防火墙规则用 installer/vps/firewall.sh 集中管理（nft/iptables 兼容 + 幂等）。
#
# 用法：
#   sudo ./deploy.sh                          # 构建 + 安装 + 启动 + 防火墙
#   sudo ./deploy.sh --skip-build             # 只用已有产物
#   sudo ./deploy.sh --no-start               # 只安装不启动（不跑防火墙 + 不起服务）
#   sudo ./deploy.sh --no-firewall            # 不跑防火墙（运维手动管）
#   sudo ./deploy.sh --no-cert                # 缺证书时也继续（运维手动补）
#   sudo ./deploy.sh --gen-cert               # 缺证书时自动调 gen-cert.sh 生成（默认 false，避免隐式换证导致 TOFU pin 失效）
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

INSTALL_DIR=/opt/desklink
DATA_DIR=/var/lib/desklink
CONF_DIR=/etc/desklink
CERT_DIR="$CONF_DIR/certs"
RELAY_PORT="${RELAY_PORT:-443}"
REG_PORT="${REG_PORT:-7860}"

SKIP_BUILD=0
NO_START=0
NO_FIREWALL=0
NO_CERT=0
GEN_CERT=0
for arg in "$@"; do
  case "$arg" in
    --skip-build)  SKIP_BUILD=1 ;;
    --no-start)    NO_START=1 ;;
    --no-firewall) NO_FIREWALL=1 ;;
    --no-cert)     NO_CERT=1 ;;
    --gen-cert)    GEN_CERT=1 ;;
    -h|--help)
      sed -n '8,16p' "$0"
      exit 0
      ;;
    *) echo "deploy.sh: unknown arg: $arg" >&2; exit 2 ;;
  esac
done

if [[ "$(id -u)" -ne 0 ]]; then
  echo "请用 root（sudo）运行：需要写 /opt、/etc、/var/lib 并操作 systemd。" >&2
  exit 1
fi

echo "[+] 仓库根：$REPO_ROOT"

# ── 1) 构建 ──────────────────────────────────────────────────────────────────
if [[ "$SKIP_BUILD" -eq 0 ]]; then
  if ! command -v go >/dev/null 2>&1; then
    echo "找不到 go。请先安装 Go 1.22+（例如 apt install golang-go 或从 go.dev 下载）。" >&2
    exit 1
  fi
  echo "[+] 构建 registryd / relayd（GOOS=linux GOARCH=$(go env GOARCH)）"
  mkdir -p "$SCRIPT_DIR/_out"
  ( cd "$REPO_ROOT/src/vps" && \
      CGO_ENABLED=0 go build -trimpath -ldflags "-s -w" -o "$SCRIPT_DIR/_out/registryd" ./cmd/registryd && \
      CGO_ENABLED=0 go build -trimpath -ldflags "-s -w" -o "$SCRIPT_DIR/_out/relayd"    ./cmd/relayd )
fi

BUILD_OUT="$SCRIPT_DIR/_out"
[[ -x "$BUILD_OUT/registryd" && -x "$BUILD_OUT/relayd" ]] || {
  echo "缺少构建产物（$BUILD_OUT）。去掉 --skip-build 重试。" >&2; exit 1; }

# ── 2) 目录与用户 ────────────────────────────────────────────────────────────
echo "[+] 创建目录与专用用户"
id -u desklink >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin desklink

install -d -m 0755 "$INSTALL_DIR"
install -d -m 0750 -o desklink -g desklink "$DATA_DIR" "$DATA_DIR/registryd"
install -d -m 0750 -o root     -g desklink "$CONF_DIR" "$CERT_DIR"

install -m 0755 "$BUILD_OUT/registryd" "$INSTALL_DIR/registryd"
install -m 0755 "$BUILD_OUT/relayd"    "$INSTALL_DIR/relayd"

# ── 3) bearer token（relay → registry 的 loopback 鉴权）─────────────────────
TOKEN_FILE="$CONF_DIR/registry.token"
if [[ ! -f "$TOKEN_FILE" ]]; then
  echo "[+] 生成 registry bearer token（0600）"
  # 32 字节十六进制。registry 只监听 loopback，token 是第二道防线：
  # 即使同机其它用户能连 127.0.0.1:7860，没有 token 也无法读写设备/配对数据。
  head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n' > "$TOKEN_FILE"
  chmod 600 "$TOKEN_FILE"
  chown root:desklink "$TOKEN_FILE"
else
  echo "[i] 复用已有 token：$TOKEN_FILE"
fi

# ── 4) TLS 证书 ──────────────────────────────────────────────────────────────
if [[ ! -f "$CERT_DIR/relay.crt" || ! -f "$CERT_DIR/relay.key" ]]; then
  if [[ "$GEN_CERT" -eq 1 ]]; then
    echo "[+] 显式调 gen-cert.sh 生成证书（--gen-cert 已确认换证）"
    "$SCRIPT_DIR/gen-cert.sh" --out-dir "$CERT_DIR" --cn "desklink-relay" --days 3650 \
      --san "127.0.0.1,$(hostname -f 2>/dev/null || hostname)"
  elif [[ "$NO_CERT" -eq 1 ]]; then
    echo "[!] 缺证书且 --no-cert 已设，跳过（relayd 启动时会因缺证书失败）"
  else
    echo "[!] 未找到 $CERT_DIR/relay.{crt,key} —— relayd 生产必须用 --cert/--key。"
    echo "    请显式跑： sudo $SCRIPT_DIR/gen-cert.sh $CERT_DIR <你的域名或IP>"
    echo "    或加 --gen-cert 让本脚本生成（注意换证会让所有客户端的 TOFU pin 失效）"
    echo "    继续安装，但 relayd 启动时会因缺证书而失败。"
  fi
fi

# ── 5) systemd 单元 ──────────────────────────────────────────────────────────
echo "[+] 安装 systemd 单元"
sed -e "s|@INSTALL_DIR@|$INSTALL_DIR|g" \
    -e "s|@DATA_DIR@|$DATA_DIR|g" \
    -e "s|@CONF_DIR@|$CONF_DIR|g" \
    -e "s|@REG_PORT@|$REG_PORT|g" \
    "$SCRIPT_DIR/registry.service" > /etc/systemd/system/desklink-registryd.service

sed -e "s|@INSTALL_DIR@|$INSTALL_DIR|g" \
    -e "s|@DATA_DIR@|$DATA_DIR|g" \
    -e "s|@CONF_DIR@|$CONF_DIR|g" \
    -e "s|@RELAY_PORT@|$RELAY_PORT|g" \
    "$SCRIPT_DIR/relay.service" > /etc/systemd/system/desklink-relayd.service

# 自检 systemd 单元语法（不启动）
if command -v systemd-analyze >/dev/null 2>&1; then
  echo "[+] systemd-analyze verify"
  systemd-analyze verify /etc/systemd/system/desklink-registryd.service || echo "    [!] registry.service 自检失败，请人工检查"
  systemd-analyze verify /etc/systemd/system/desklink-relayd.service    || echo "    [!] relay.service 自检失败，请人工检查"
else
  echo "[i] systemd-analyze 不在 PATH（开发机），跳过 unit 自检；VPS 上必跑"
fi

systemctl daemon-reload

if [[ "$NO_START" -eq 0 ]]; then
  echo "[+] 启动服务（registryd 先起，relayd 依赖它）"
  systemctl enable --now desklink-registryd.service
  sleep 1
  systemctl enable --now desklink-relayd.service
  systemctl --no-pager --lines=0 status desklink-registryd.service || true
  systemctl --no-pager --lines=0 status desklink-relayd.service    || true
fi

# ── 6) 防火墙（用 installer/vps/firewall.sh 集中管理） ────────────────────────
if [[ "$NO_START" -eq 0 && "$NO_FIREWALL" -eq 0 ]]; then
  if [[ -x "$SCRIPT_DIR/firewall.sh" ]]; then
    echo "[+] 应用防火墙（relay 443/TCP+UDP，registry 7860 仅 loopback）"
    RELAY_PORT="$RELAY_PORT" REG_PORT="$REG_PORT" \
      "$SCRIPT_DIR/firewall.sh" apply --relay-port "$RELAY_PORT" --reg-port "$REG_PORT"
    # 持久化（Ubuntu/Debian 默认装 nftables-persistent；CentOS 需手动写脚本）
    RELAY_PORT="$RELAY_PORT" REG_PORT="$REG_PORT" \
      "$SCRIPT_DIR/firewall.sh" persist || echo "    [!] 持久化失败，请人工处理（apt install nftables-persistent）"
  else
    echo "[!] $SCRIPT_DIR/firewall.sh 不可用，跳过防火墙配置" >&2
  fi
fi

# ── 7) 完成报告 ─────────────────────────────────────────────────────────────
cat <<EOF

[+] 部署完成
    registryd : 127.0.0.1:$REG_PORT （仅 loopback，**不要**对外开放）
    relayd    : :$RELAY_PORT        （TCP + UDP 同号，已自动放行）

    客户端中继地址形如： https://<你的域名或IP>:$RELAY_PORT
    （https:// 会优先 QUIC、不可用时回落 TCP/TLS；quic:// 强制 QUIC，tls:// 强制 TCP/TLS）

    验证：
      systemctl status desklink-registryd desklink-relayd
      openssl s_client -connect 127.0.0.1:$RELAY_PORT -servername <你的域名> -showcerts < /dev/null
      ss -tlnp | grep -E ':($RELAY_PORT|$REG_PORT)\b'
      $SCRIPT_DIR/firewall.sh status

    运维：
      journalctl -u desklink-relayd -f
      journalctl -u desklink-registryd -f
      撤销/公钥变更经 $DATA_DIR/regnotify.sock 由 registryd 推送到 relayd（即时踢线）

    换证流程（运维）：
      sudo $SCRIPT_DIR/gen-cert.sh --force  # 强制换证
      sudo systemctl restart desklink-relayd
      # 注意：客户端的 TOFU pin 会失效，必须显式清除 pin 才能重连（这是有意的）

    防火墙重置（如果需要）：
      sudo $SCRIPT_DIR/firewall.sh reset
EOF
