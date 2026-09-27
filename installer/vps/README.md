# DeskLink VPS 部署

VPS 上跑两个进程，职责严格分离：

| 进程 | 监听 | 作用 | 是否暴露公网 |
|---|---|---|---|
| `registryd` | `127.0.0.1:7860` | SQLite：设备公钥、加密昵称、配对关系、撤销状态、最近在线时间 | **绝不暴露**（仅 loopback，由 relayd 代理访问） |
| `relayd` | `:9443`（TCP + UDP 同号） | QUIC + TCP/TLS 双监听；设备挑战认证；内存接线表 + 30s 宽限窗；**只转发密文** | 是（唯一对外入口） |

VPS **不存储**桌面图像、控制事件、文件内容、Windows 凭据或设备私钥。

---

## 一键部署

```bash
sudo ./deploy.sh              # 构建 + 安装到 /opt/desklink + 装 systemd 单元 + 启动
sudo ./deploy.sh --skip-build # 只用已有产物（installer/vps/_out/）
sudo ./deploy.sh --no-start   # 只安装不启动（先检查配置）
```

脚本做的事：

1. `CGO_ENABLED=0 go build` 出 `registryd` / `relayd`（静态二进制，不依赖 glibc 版本）。
2. 建专用系统用户 `desklink`（nologin），目录：
   - `/opt/desklink` — 二进制
   - `/var/lib/desklink` — 数据（registry SQLite、regnotify unix socket）
   - `/etc/desklink` — 配置（bearer token、证书）
3. 生成 registry bearer token（`0600`，root:desklink）。**registry 只监听 loopback，
   token 是第二道防线**：即使同机其它用户能连 `127.0.0.1:7860`，没有 token 也读不到数据。
4. 安装并启动两个 systemd 单元（带 `Restart=on-failure` 与沙箱收紧）。
5. 打印需要自行放行的中继端口命令（**不自动改防火墙**，避免踩掉你已有的规则）。

### 证书

`relayd` 生产环境必须用 `--cert` / `--key`（自签即可）：

```bash
sudo ./gen-cert.sh /etc/desklink/certs
```

自签是**设计内**方案：中继是用户自有 VPS，没有公信 CA 证书；客户端用 **TOFU**
（首次记录证书指纹并人工确认，之后必须一致）防中间人。脚本不会自动换证——
换证会让所有客户端的 pin 失效，必须是显式运维动作。

### 放行端口

```bash
sudo ufw allow 9443/tcp && sudo ufw allow 9443/udp          # ufw
sudo firewall-cmd --add-port=9443/tcp --add-port=9443/udp --permanent && sudo firewall-cmd --reload
```

**不要**放行 `7860`。registry 只接受 loopback 访问。

---

## 客户端连接

中继地址形如 `https://<域名或IP>:9443`。scheme 决定传输：

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
```

- **撤销即时生效**：`registryd` 在撤销配对/公钥变更时，经
  `/var/lib/desklink/regnotify.sock` 推事件给 `relayd`，`relayd` 立刻踢掉相关会话并清接线。
  若该 socket 不通，撤销只会在下一次接线查询时生效（不即时）。
- **relayd 重启 = 所有会话断开**：接线表在内存里（刻意不持久化）。
  两端客户端会用指数退避重连，并**完整重握手**；文件流从已确认分块继续。
- **registryd 重启**不影响已建立的会话，但重启期间新的接线请求会被拒绝。

---

## 本机联调（不用 VPS）

不需要任何 VPS 配置，直接用仓库的冒烟脚本起本地 registry + relay：

```powershell
pwsh -NoProfile -File tests\e2e-smoke.ps1 -Mode relay -Transport both
```

它会自行构建 Go 组件、注册两台设备、建立配对、拉起两个 `--console` 实例，
并断言加密控制帧往返、真实 RTT 与一次完整的文件传输。
