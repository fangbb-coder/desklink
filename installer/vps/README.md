# DeskLink VPS 部署

VPS 上跑两个进程，职责严格分离：

| 进程 | 监听 | 作用 | 是否暴露公网 |
|---|---|---|---|
| `registryd` | `127.0.0.1:7860` | SQLite：设备公钥、加密昵称、配对关系、撤销状态、最近在线时间 | **绝不暴露**（仅 loopback，由 relayd 代理访问） |
| `relayd` | `:443`（TCP + UDP 同号） | QUIC + TCP/TLS 双监听；设备挑战认证；内存接线表 + 30s 宽限窗；**只转发密文** | 是（唯一对外入口） |

VPS **不存储**桌面图像、控制事件、文件内容、Windows 凭据或设备私钥。

---

## 一键部署

```bash
sudo ./deploy.sh              # 构建 + 安装 + 启动 + 应用防火墙
sudo ./deploy.sh --skip-build # 只用已有产物（installer/vps/_out/）
sudo ./deploy.sh --no-start   # 只安装不启动（不跑防火墙、不起服务）
sudo ./deploy.sh --no-firewall  # 不跑防火墙（运维手动管）
sudo ./deploy.sh --no-cert     # 缺证书时跳过（运维手动补；relayd 启动会因缺证书失败）
sudo ./deploy.sh --gen-cert    # 缺证书时自动调 gen-cert.sh 生成（注意：换证会让所有客户端 TOFU pin 失效）
```

脚本做的事：

1. `CGO_ENABLED=0 go build` 出 `registryd` / `relayd`（静态二进制，不依赖 glibc 版本）。
2. 建专用系统用户 `desklink`（nologin），目录：
   - `/opt/desklink` — 二进制
   - `/var/lib/desklink` — 数据（registry SQLite、regnotify unix socket）
   - `/etc/desklink` — 配置（bearer token、证书）
3. 生成 registry bearer token（`0600`，root:desklink）。**registry 只监听 loopback，
   token 是第二道防线**：即使同机其它用户能连 `127.0.0.1:7860`，没有 token 也读不到数据。
4. 安装并启动两个 systemd 单元（带 `Restart=on-failure` 与沙箱收紧），并跑 `systemd-analyze verify` 自检（VPS 上必跑）。
5. **自动应用防火墙规则**（幂等）：调 `firewall.sh apply`，规则：
   - relay 端口 `443/tcp+udp` 入站 ACCEPT
   - registry 端口 `7860/tcp` 仅 `127.0.0.1` ACCEPT；其他源 REJECT（让 nmap 看到显式拒绝）
   - SSH `22/tcp` 保留
   - 自动检测 `nft`（nftables 原生）或 `iptables`（nft/legacy 兼容）

幂等性：重复执行 `sudo ./deploy.sh` 不会破坏现有部署：
- 已有 token 不重新生成（保留原值）
- 已有 systemd unit 用 `sed` 重写（`daemon-reload` + restart）
- 防火墙规则用 `iptables -C` / `nft flush table` 幂等检查后再添加
- 证书若已存在则 deploy.sh 跳过自动生成（必须显式删除并 `gen-cert.sh --force` 重跑才能换证）

---

## 验收（干净 VPS）

### 1. 服务 active

```bash
$ systemctl status desklink-registryd desklink-relayd --no-pager --lines=0
● desklink-registryd.service - DeskLink registryd（设备/配对/撤销状态，仅 loopback）
   Loaded: loaded (/etc/systemd/system/desklink-registryd.service; enabled)
   Active: active (running) since ...
● desklink-relayd.service - DeskLink relayd（QUIC + TCP/TLS 双监听中继）
   Loaded: loaded (/etc/systemd/system/desklink-relayd.service; enabled)
   Active: active (running) since ...
```

### 2. 端口监听

```bash
$ ss -tlnp | grep -E ':(443|7860)\b'
LISTEN  0  128  127.0.0.1:7860  users:(("registryd",pid=...,fd=...))    # 仅 loopback
LISTEN  0  128  0.0.0.0:443    users:(("relayd",pid=...,fd=...))       # 公网入站
LISTEN  0  128  [::]:443       users:(("relayd",pid=...,fd=...))       # IPv6 同样
```

### 3. 证书 openssl s_client 验证

```bash
$ openssl s_client -connect 127.0.0.1:443 -servername <你的域名或IP> -showcerts < /dev/null
CONNECTED(00000003)
depth=0 CN = <CN>, O = DeskLink
verify return:1
---
Certificate chain
 0 s:CN = <CN>, O = DeskLink
   i:CN = <CN>, O = DeskLink  # 自签
   a:PKEY: rsaEncryption, 3072 (bit); sigalg: RSA-SHA256
   v:NotBefore: ...; NotAfter: ...  # 3650 天有效期
```

### 4. 防火墙规则

```bash
# nftables
$ nft list table inet desklink
table inet desklink {
  chain input {
    type filter hook input priority filter; policy accept;
    tcp dport 443 accept comment "desklink-relay"
    udp dport 443 accept comment "desklink-relay"
    tcp dport 7860 ip saddr != 127.0.0.1 drop comment "desklink-registry-loopback-only"
  }
}

# iptables
$ iptables -L INPUT -n -v --line-numbers | grep -E '(443|7860)'
1   ... ACCEPT tcp  --  0.0.0.0/0  0.0.0.0/0  tcp dpt:443 /* desklink-relay */
2   ... ACCEPT udp  --  0.0.0.0/0  0.0.0.0/0  udp dpt:443 /* desklink-relay */
3   ... ACCEPT tcp  --  127.0.0.1  0.0.0.0/0  tcp dpt:7860 /* desklink-registry-loopback-only */
4   ... DROP   tcp  --  0.0.0.0/0  0.0.0.0/0  tcp dpt:7860
```

**registry 7860 端口**对非 `127.0.0.1` 源 DROP；同机其他用户进程连 `127.0.0.1:7860` 仍需 bearer token。

---

## 证书

`relayd` 生产环境必须用 `--cert` / `--key`（自签即可）：

```bash
sudo ./gen-cert.sh /etc/desklink/certs <你的域名或IP>
# 或参数化
sudo ./gen-cert.sh --out-dir /etc/desklink/certs --cn relay.example.com --days 3650 \
                  --san relay.example.com,1.2.3.4 --algo rsa
```

自签是**设计内**方案：中继是用户自有 VPS，没有公信 CA 证书；客户端用 **TOFU**
（首次记录证书指纹并人工确认，之后必须一致）防中间人。脚本**幂等**：
- 已存在且剩余有效期 > 30 天 → 跳过（避免隐式换证导致 TOFU pin 失效）
- `--force` 强制重新生成
- 换证后所有客户端的 pin 失效，必须显式清除 pin 才能重连（运维动作）

`--algo` 可选 `rsa`（默认 3072 位）或 `ecdsa`（P-256，QUIC 友好更短）。

---

## 独立工具

部署脚本会调下列独立工具；运维也可单独用。

### `firewall.sh` — 防火墙（nftables / iptables 兼容）

```bash
sudo ./firewall.sh apply   [--relay-port 443] [--reg-port 7860] [--ssh-port 22]
sudo ./firewall.sh status                              # 列出 desklink 链/规则
sudo ./firewall.sh reset                               # 删除 desklink 链
sudo ./firewall.sh persist                             # 持久化到 /etc/nftables.conf 或 netfilter-persistent
```

环境变量 `DESKLINK_RELAY_PORT` / `DESKLINK_REG_PORT` / `DESKLINK_SSH_PORT` 可覆盖默认端口。

规则目标：
- relay 端口 `443/tcp+udp`（QUIC + TCP/TLS）放行
- SSH `22/tcp` 保留
- registry 端口 `7860/tcp` **仅 loopback 接受**；公网来源显式 REJECT（让 traceroute / nmap 看到）

后端自动检测：`nft` 优先（Ubuntu 22+ / Debian 12+ 默认），否则 `iptables`。

### `gen-cert.sh` — 自签证书（详细见上文"证书"章节）

`--algo ecdsa` 产出 P-256（QUIC 友好）；`--algo rsa` 产出 3072 位 RSA（默认，兼容性更广）。

### 替换 deploy.sh 的内置逻辑

- 不希望 deploy.sh 启动服务？`--no-start`
- 不想跑防火墙？`--no-firewall`
- 缺证书但想跳过？`--no-cert`
- 想让 deploy.sh 自动生成证书（**换证会导致 TOFU pin 失效，慎用**）？`--gen-cert`

---

## 客户端连接

中继地址形如 `https://<域名或IP>:443`（标准 HTTPS 端口，防火墙/代理友好）。scheme 决定传输：

| scheme | 行为 |
|---|---|
| `https://` | 优先 QUIC，QUIC 不可用时回落 TCP/TLS（推荐） |
| `quic://` | **强制 QUIC**；本机不可用时**报错不静默回落**（便于排查） |
| `tls://` | 强制 TCP/TLS（Win10 或 UDP 被封时用） |

首次连接会弹出中继证书指纹确认（TOFU）。核对方式：

```bash
openssl x509 -in /etc/desklink/certs/relay.crt -noout -fingerprint -sha256
```

指纹变化时客户端会**拒绝连接**而不是自动接受；确认 relay 合法换证后，需显式清除该端点的 pin 再重连。

---

## 运维

```bash
journalctl -u desklink-relayd -f
journalctl -u desklink-registryd -f
systemctl restart desklink-relayd
systemctl restart desklink-registryd
```

- **撤销即时生效**：`registryd` 在撤销配对/公钥变更时，经
  `/var/lib/desklink/regnotify.sock` 推事件给 `relayd`，`relayd` 立刻踢掉相关会话并清接线。
  若该 socket 不通，撤销只会在下一次接线查询时生效（不即时）。
- **relayd 重启 = 所有会话断开**：接线表在内存里（刻意不持久化）。
  两端客户端会用指数退避重连，并**完整重握手**；文件流从已确认分块继续。
- **registryd 重启**不影响已建立的会话，但重启期间新的接线请求会被拒绝。
- **防火墙 reload 安全**：`nft flush table inet desklink; nft -f` 是原子操作；`iptables -C` 检查避免重复添加 `-I` 规则。

---

## 本机联调（不用 VPS）

不需要任何 VPS 配置，直接用仓库的冒烟脚本起本地 registry + relay：

```powershell
pwsh -NoProfile -File tests\e2e-smoke.ps1 -Mode relay -Transport both
```

它会自行构建 Go 组件、注册两台设备、建立配对、拉起两个 `--console` 实例，
并断言加密控制帧往返、真实 RTT 与一次完整的文件传输。
