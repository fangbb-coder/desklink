#!/usr/bin/env bash
# 生成 relayd 的 TCP/TLS 服务端证书（自签，PEM）。
#
# 为什么自签：DeskLink 的中继是**用户自有 VPS**，没有公信 CA 证书可用；
# 客户端用 TOFU（首次记录证书指纹，之后必须一致）来防中间人，
# 因此自签 + 指纹固定是设计内的方案，而不是妥协。
#
# 用法：
#   ./gen-cert.sh [输出目录] [主机名或IP]
# 默认输出到 /etc/desklink/certs，主机名取本机 hostname。
#
# 生成后：
#   - relayd 用 --cert / --key 指向这两个文件
#   - 客户端首次连接时会显示证书指纹，需人工确认（TOFU）
#   - 换证书后客户端的 pin 会不匹配 → 必须显式清除 pin（运维动作），
#     这是有意的：避免"证书被换掉后静默接受"。
set -euo pipefail

OUT_DIR="${1:-/etc/desklink/certs}"
CN="${2:-$(hostname -f 2>/dev/null || hostname)}"

CERT="$OUT_DIR/relay.crt"
KEY="$OUT_DIR/relay.key"

mkdir -p "$OUT_DIR"

if [[ -f "$CERT" || -f "$KEY" ]]; then
  echo "!! $CERT 或 $KEY 已存在；为安全起见不覆盖（换证请先备份并删除，然后重跑）。" >&2
  echo "   换证后客户端的 TOFU pin 会不匹配，需要显式清除 pin 才能重连。" >&2
  exit 1
fi

# 3650 天：自用场景，避免频繁换证导致所有客户端都要重新确认指纹。
openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes \
  -keyout "$KEY" -out "$CERT" \
  -subj "/CN=$CN/O=DeskLink" \
  -addext "subjectAltName=DNS:$CN" \
  -addext "basicConstraints=critical,CA:FALSE" \
  -addext "keyUsage=critical,digitalSignature,keyEncipherment" \
  -addext "extendedKeyUsage=serverAuth"

chmod 600 "$KEY"
chmod 644 "$CERT"

echo "[+] 证书已生成："
echo "    cert: $CERT"
echo "    key : $KEY  (0600)"
echo
echo "证书 SHA-256 指纹（客户端 TOFU 首次确认时会显示这个值，可人工核对）："
openssl x509 -in "$CERT" -noout -fingerprint -sha256 | sed 's/^/    /'
echo
echo "下一步："
echo "    systemctl restart desklink-relayd"
echo "    客户端首次连接该中继时确认上述指纹"
