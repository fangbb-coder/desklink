#!/usr/bin/env bash
# gen-cert.sh — 生成 relayd 的 TCP/TLS 服务端证书（自签，PEM）。
#
# 为什么自签：DeskLink 的中继是**用户自有 VPS**，没有公信 CA 证书可用；
# 客户端用 TOFU（首次记录证书指纹，之后必须一致）来防中间人，
# 因此自签 + 指纹固定是设计内的方案，而不是妥协。
#
# 用法（参数化 + 位置参数回退兼容）：
#   gen-cert.sh [OUT_DIR] [CN]   # 旧位置参数
#   gen-cert.sh [--out-dir DIR] [--cn CN] [--days DAYS] [--san HOST[,HOST...]] [--algo ecdsa|rsa] [--force]
#
# 默认：
#   --out-dir /etc/desklink/certs
#   --cn      本机 hostname
#   --days    3650
#   --san     <CN>
#   --algo    rsa
#
# 幂等：若 cert + key 都已存在且剩余有效期 > 30 天，跳过生成。
# 强制重新生成：--force 或部署前手动删除 cert/key 文件。
#
# 生成后：
#   - relayd 用 --cert / --key 指向这两个文件
#   - 客户端首次连接时会显示证书指纹，需人工确认（TOFU）
#   - 换证书后客户端的 pin 会不匹配 → 必须显式清除 pin（运维动作），
#     这是有意的：避免"证书被换掉后静默接受"。
set -euo pipefail

# 关掉 Git Bash (msys) 的 path conversion：
# openssl -subj /CN=$CN/O=DeskLink 会被误转为 C:/Program Files/Git/CN=...
# Linux/Mac 上 MSYS_NO_PATHCONV=1 无害（变量不存在也无影响）。
export MSYS_NO_PATHCONV=1

# ----- 默认值 -----
OUT_DIR=""
CN=""
DAYS=""
SANS=""
ALGO=""
FORCE=0
POSITIONAL=()

# ----- 解析 flag + 位置参数（旧调用：gen-cert.sh [OUT_DIR] [CN]）-----
while [[ $# -gt 0 ]]; do
  case "$1" in
    --out-dir) OUT_DIR="$2"; shift 2 ;;
    --cn)      CN="$2"; shift 2 ;;
    --days)    DAYS="$2"; shift 2 ;;
    --san)     SANS="$2"; shift 2 ;;
    --algo)    ALGO="$2"; shift 2 ;;
    --force)   FORCE=1; shift ;;
    -h|--help) sed -n '2,40p' "$0"; exit 0 ;;
    -*)        echo "gen-cert.sh: unknown arg: $1" >&2; exit 2 ;;
    *)         POSITIONAL+=("$1"); shift ;;
  esac
done

# ----- 位置参数回退（兼容旧调用：OUT_DIR [CN]） -----
[[ -z "$OUT_DIR" && ${#POSITIONAL[@]} -ge 1 ]] && OUT_DIR="${POSITIONAL[0]}"
[[ -z "$CN"      && ${#POSITIONAL[@]} -ge 2 ]] && CN="${POSITIONAL[1]}"
[[ -z "$OUT_DIR" ]] && OUT_DIR="/etc/desklink/certs"
[[ -z "$CN"      ]] && CN="$(hostname -f 2>/dev/null || hostname)"
[[ -z "$DAYS"    ]] && DAYS=3650
[[ -z "$SANS"    ]] && SANS="$CN"
[[ -z "$ALGO"    ]] && ALGO="rsa"

if ! command -v openssl >/dev/null 2>&1; then
  echo "gen-cert.sh: openssl not found in PATH" >&2
  exit 1
fi

CERT="$OUT_DIR/relay.crt"
KEY="$OUT_DIR/relay.key"
MIN_VALIDITY_DAYS=30

# ----- 幂等检查 -----
if [[ "$FORCE" != "1" && -f "$CERT" && -f "$KEY" ]]; then
  if NOT_AFTER="$(openssl x509 -in "$CERT" -noout -enddate 2>/dev/null | sed 's/^notAfter=//')"; then
    if NOT_AFTER_EPOCH="$(date -d "$NOT_AFTER" +%s 2>/dev/null || true)" && [[ -n "$NOT_AFTER_EPOCH" ]]; then
      REMAINING=$(( (NOT_AFTER_EPOCH - $(date +%s)) / 86400 ))
      if [[ "$REMAINING" -ge "$MIN_VALIDITY_DAYS" ]]; then
        echo "gen-cert.sh: cert valid for $REMAINING more days; skipping (use --force to regenerate)"
        echo "  cert: $CERT"
        echo "  key:  $KEY"
        echo "  fingerprint:"
        openssl x509 -in "$CERT" -noout -fingerprint -sha256 | sed 's/^/    /'
        exit 0
      else
        echo "gen-cert.sh: cert expires in $REMAINING days (< $MIN_VALIDITY_DAYS); regenerating"
      fi
    fi
  fi
fi

# ----- 准备目录 -----
mkdir -p "$OUT_DIR"
chmod 0755 "$OUT_DIR"

# ----- 私钥 -----
case "$ALGO" in
  rsa)
    openssl genrsa -out "$KEY" 3072
    KEY_ARGS=("-key" "$KEY")
    ;;
  ecdsa)
    openssl ecparam -name prime256v1 -genkey -noout -out "$KEY"
    KEY_ARGS=("-key" "$KEY")
    ;;
  *) echo "gen-cert.sh: unknown --algo $ALGO (want rsa|ecdsa)" >&2; exit 2 ;;
esac
chmod 0600 "$KEY"

# ----- SAN 扩展 -----
SAN_ENTRIES="DNS:$CN"
IFS=',' read -ra SAN_ARR <<< "$SANS"
for h in "${SAN_ARR[@]}"; do
  h="$(echo "$h" | xargs)"
  [[ -z "$h" ]] && continue
  if [[ "$h" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    SAN_ENTRIES+=",IP:$h"
  else
    SAN_ENTRIES+=",DNS:$h"
  fi
done

# ----- 自签证书 -----
# RFC 5280 PrintableString 子集 + 防御性转义：CN 含空格/&/= 等会断 openssl -subj 解析。
# 用 tr 把非安全字符替换为 '_'，再以双引号包 -subj 防 shell 词拆分。
SAFE_CN=$(printf '%s' "$CN" | tr -c 'A-Za-z0-9 ._-' '_')
SUBJ="/CN=${SAFE_CN}/O=DeskLink"
openssl req -x509 -new -sha256 -days "$DAYS" \
  "${KEY_ARGS[@]}" \
  -out "$CERT" \
  -subj "$SUBJ" \
  -addext "subjectAltName=$SAN_ENTRIES" \
  -addext "basicConstraints=critical,CA:FALSE" \
  -addext "keyUsage=critical,digitalSignature,keyEncipherment" \
  -addext "extendedKeyUsage=serverAuth"

chmod 0644 "$CERT"

echo "[+] 证书已生成："
echo "    cert: $CERT"
echo "    key : $KEY  (0600)"
echo "    algo: $ALGO"
echo "    days: $DAYS"
echo "    SAN : $SAN_ENTRIES"
echo
echo "证书 SHA-256 指纹（客户端 TOFU 首次确认时会显示这个值，可人工核对）："
openssl x509 -in "$CERT" -noout -fingerprint -sha256 | sed 's/^/    /'
echo
echo "下一步："
echo "    systemctl restart desklink-relayd"
echo "    客户端首次连接该中继时确认上述指纹"
