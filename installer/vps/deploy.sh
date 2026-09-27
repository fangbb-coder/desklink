#!/usr/bin/env bash
# DeskLink VPS 部署脚本：构建 Linux 二进制 → 安装到 /opt/desklink → 装 systemd 单元。
#
# 设计约束（来自 DESIGN.md「部署」章节）：
#   - **registry 绝不暴露公网**：只监听 127.0.0.1，仅由 relay 经 loopback 访问。
#   - 只对外开放 relay 的 HTTPS/QUIC 入口（默认 9443，TCP+UDP 同号）。
#   - registry 与 relay 之间用 unix socket 推送撤销/公钥变更事件（即时踢线）。
#
# 用法：
#   sudo ./deploy.sh                     # 构建 + 安装 + 启动
#   sudo ./deploy.sh --skip-build        # 只用已有产物
#   sudo ./deploy.sh --no-start          # 只安装不启动
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

INSTALL_DIR=/opt/desklink
DATA_DIR=/var/lib/desklink
CONF_DIR=/etc/desklink
CERT_DIR="$CONF_DIR/certs"
RELAY_PORT="${RELAY_PORT:-9443}"
REG_PORT="${REG_PORT:-7860}"

SKIP_BUILD=0
NO_START=0
for arg in "$@"; do
  case "$arg" in
    --skip-build) SKIP_BUILD=1 ;;
    --no-start)   NO_START=1 ;;
    *) echo "未知参数：$arg" >&2; exit 2 ;;
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
if [[ ! -f "$CERT_DIR/relay.crt" ]]; then
  echo "[!] 未找到 $CERT_DIR/relay.crt —— relayd 生产必须用 --cert/--key。"
  echo "    请先运行： sudo $SCRIPT_DIR/gen-cert.sh $CERT_DIR"
  echo "    （本脚本不会自动生成证书：换证会让所有客户端的 TOFU pin 失效，应当是显式运维动作。）"
  echo "    继续安装，但 relayd 启动时会因缺证书而失败。"
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

systemctl daemon-reload

if [[ "$NO_START" -eq 0 ]]; then
  echo "[+] 启动服务（registryd 先起，relayd 依赖它）"
  systemctl enable --now desklink-registryd.service
  sleep 1
  systemctl enable --now desklink-relayd.service
  systemctl --no-pager --lines=0 status desklink-registryd.service || true
  systemctl --no-pager --lines=0 status desklink-relayd.service    || true
fi

# ── 6) 防火墙提示（不自动改，避免踩掉别人的规则）───────────────────────────
cat <<EOF

[+] 部署完成
    registryd : 127.0.0.1:$REG_PORT （仅 loopback，**不要**对外开放）
    relayd    : :$RELAY_PORT        （TCP + UDP 同号，需要对外放行）

    请自行放行中继端口（示例，按你的发行版选择）：
      ufw   : sudo ufw allow $RELAY_PORT/tcp && sudo ufw allow $RELAY_PORT/udp
      nft   : nft add rule inet filter input tcp dport $RELAY_PORT accept
      nft   : nft add rule inet filter input udp dport $RELAY_PORT accept
      firewalld: sudo firewall-cmd --add-port=$RELAY_PORT/tcp --add-port=$RELAY_PORT/udp --permanent && sudo firewall-cmd --reload

    客户端中继地址形如： https://<你的域名或IP>:$RELAY_PORT
    （https:// 会优先 QUIC、不可用时回落 TCP/TLS；quic:// 强制 QUIC，tls:// 强制 TCP/TLS）

    运维：
      journalctl -u desklink-relayd -f
      journalctl -u desklink-registryd -f
      撤销/公钥变更经 $DATA_DIR/regnotify.sock 由 registryd 推送到 relayd（即时踢线）
EOF
