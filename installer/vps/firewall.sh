#!/usr/bin/env bash
# firewall.sh — desklink VPS 防火墙策略（iptables/nftables 兼容）
#
# 用法（需要 root 或 CAP_NET_ADMIN）：
#   firewall.sh apply                 # 应用规则（幂等）
#   firewall.sh status                # 列出 desklink 链/规则
#   firewall.sh reset                 # 删除 desklink 链
#   firewall.sh persist               # 持久化到系统配置（apt-installed iptables-persistent 或 nftables.conf）
#
# 规则目标：
#   - relay 入站：443/TCP + 443/UDP（QUIC）放行
#   - SSH：22/TCP 放行（如未自定义，详见注释）
#   - registry 入站：7860/TCP **仅 loopback 接受**（来自 127.0.0.1）
#   - registry 来自公网：REJECT（让 traceroute / nmap 看到显式拒绝）
#   - 其余按系统默认策略
#
# 幂等：每次跑不会重复添加（用 chain 隔离 + comment 标记 "desklink-managed"）。
# 持久化：apply 后必须再跑 persist 才能在重启后保留（apt-get install iptables-persistent / nftables-persistent）。

set -euo pipefail

# 端口与 chain 名（集中常量）
RELAY_PORT="${DESKLINK_RELAY_PORT:-443}"      # QUIC + TCP/TLS 复用
REG_PORT="${DESKLINK_REG_PORT:-7860}"          # registryd loopback
SSH_PORT="${DESKLINK_SSH_PORT:-22}"
CHAIN="DESKLINK"                                 # iptables 专用 chain 名
TABLE_FAMILY_INET="inet"

usage() {
  cat <<EOF
usage: $0 {apply|status|reset|persist} [--relay-port N] [--reg-port N] [--ssh-port N]

env vars (override defaults):
  DESKLINK_RELAY_PORT  default 443   (QUIC + TCP/TLS)
  DESKLINK_REG_PORT    default 7860  (registryd loopback)
  DESKLINK_SSH_PORT    default 22

EOF
  exit "${1:-0}"
}

# 解析参数
CMD="${1:-}"
shift || true
while [[ $# -gt 0 ]]; do
  case "$1" in
    --relay-port) RELAY_PORT="$2"; shift 2 ;;
    --reg-port)   REG_PORT="$2"; shift 2 ;;
    --ssh-port)   SSH_PORT="$2"; shift 2 ;;
    -h|--help)    usage 0 ;;
    *)            echo "firewall.sh: unknown arg: $1" >&2; usage 2 ;;
  esac
done
[[ -z "$CMD" ]] && usage 2

# 检测可用后端（nft 优先，iptables 兜底）
detect_backend() {
  if command -v nft >/dev/null 2>&1; then
    echo "nft"
  elif command -v iptables >/dev/null 2>&1; then
    echo "iptables"
  else
    echo "firewall.sh: neither nft nor iptables found in PATH" >&2
    exit 1
  fi
}

BACKEND="$(detect_backend)"

# ----- iptables 实现 -----
iptables_apply() {
  # 创建隔离 chain（如不存在）
  iptables -N "$CHAIN" 2>/dev/null || true
  # 清掉旧 rules（保留 chain），下次重灌
  iptables -F "$CHAIN"
  # 标记规则
  iptables -A "$CHAIN" -m comment --comment "desklink-managed" -j RETURN
  # relay 入站
  iptables -A "$CHAIN" -p tcp --dport "$RELAY_PORT" -m comment --comment "desklink-relay-tcp" -j ACCEPT
  iptables -A "$CHAIN" -p udp --dport "$RELAY_PORT" -m comment --comment "desklink-relay-quic" -j ACCEPT
  # SSH（保留系统连接）
  iptables -A "$CHAIN" -p tcp --dport "$SSH_PORT" -m comment --comment "desklink-ssh" -j ACCEPT
  # registry loopback-only
  iptables -A "$CHAIN" -p tcp --dport "$REG_PORT" -s 127.0.0.0/8 -m comment --comment "desklink-registry-loopback" -j ACCEPT
  iptables -A "$CHAIN" -p tcp --dport "$REG_PORT" -m comment --comment "desklink-registry-deny-public" -j REJECT --reject-with tcp-reset
  # 挂到 INPUT（仅当未挂）
  if ! iptables -C INPUT -j "$CHAIN" 2>/dev/null; then
    iptables -I INPUT 1 -j "$CHAIN"
  fi
  echo "firewall.sh: iptables apply OK"
}

iptables_status() {
  echo "=== iptables -L INPUT -n ==="
  iptables -L INPUT -n --line-numbers
  echo "=== iptables -L $CHAIN -n ==="
  iptables -L "$CHAIN" -n --line-numbers
}

iptables_reset() {
  iptables -D INPUT -j "$CHAIN" 2>/dev/null || true
  iptables -F "$CHAIN" 2>/dev/null || true
  iptables -X "$CHAIN" 2>/dev/null || true
  echo "firewall.sh: iptables reset OK"
}

iptables_persist() {
  if command -v netfilter-persistent >/dev/null 2>&1; then
    netfilter-persistent save
    echo "firewall.sh: iptables persisted via netfilter-persistent"
  elif command -v iptables-save >/dev/null 2>&1; then
    iptables-save > /etc/iptables.rules
    echo "firewall.sh: iptables-save > /etc/iptables.rules (install iptables-persistent to auto-load on boot)"
  else
    echo "firewall.sh: no persistence tool found; install iptables-persistent" >&2
    exit 1
  fi
}

# ----- nftables 实现 -----
nft_apply() {
  # 表不存在则建
  nft list table inet desklink 2>/dev/null || nft add table inet desklink
  # 链不存在则建
  nft list chain inet desklink input 2>/dev/null || \
    nft add chain inet desklink input '{ type filter hook input priority 0 ; policy accept ; }'
  # 清掉旧 rules（保留链）
  nft flush chain inet desklink input
  # relay 入站
  nft add rule inet desklink input tcp dport "$RELAY_PORT" counter accept comment \"desklink-relay-tcp\"
  nft add rule inet desklink input udp dport "$RELAY_PORT" counter accept comment \"desklink-relay-quic\"
  # SSH
  nft add rule inet desklink input tcp dport "$SSH_PORT" counter accept comment \"desklink-ssh\"
  # registry loopback-only + 显式拒绝公网
  nft add rule inet desklink input tcp dport "$REG_PORT" ip saddr 127.0.0.0/8 counter accept comment \"desklink-registry-loopback\"
  nft add rule inet desklink input tcp dport "$REG_PORT" counter reject with tcp reset comment \"desklink-registry-deny-public\"
  echo "firewall.sh: nft apply OK"
}

nft_status() {
  echo "=== nft list table inet desklink ==="
  nft list table inet desklink
}

nft_reset() {
  nft delete table inet desklink 2>/dev/null || true
  echo "firewall.sh: nft reset OK"
}

nft_persist() {
  if command -v nft >/dev/null 2>&1; then
    nft list ruleset > /etc/nftables.conf
    echo "firewall.sh: nft ruleset > /etc/nftables.conf (Debian/Ubuntu systemd nftables.service 会自动加载)"
  else
    echo "firewall.sh: nft not found" >&2
    exit 1
  fi
}

# ----- 派发 -----
case "$BACKEND:$CMD" in
  iptables:apply)  iptables_apply ;;
  iptables:status) iptables_status ;;
  iptables:reset)  iptables_reset ;;
  iptables:persist) iptables_persist ;;
  nft:apply)       nft_apply ;;
  nft:status)      nft_status ;;
  nft:reset)       nft_reset ;;
  nft:persist)     nft_persist ;;
  *) echo "firewall.sh: unknown cmd/backend: $CMD / $BACKEND" >&2; usage 2 ;;
esac