# DeskLink 设计规格

## 目标

DeskLink 是一套自用的 Windows 10/11 远程桌面软件。它在不同网络间提供一对一远程控制和双向文件操作，使用用户自有 VPS 中继，界面保持极简。

## 范围

第一版提供：

- Windows 10/11 控制端与被控端。
- 跨 NAT 和防火墙的中继连接；两端只需可访问互联网，无需端口映射。
- **局域网 IP 直连（同子网内点对点，控制端手动输入 `IP:端口`）**；安装时经用户同意后开放入站端口，**不走中继、不依赖互联网**，传输与业务协议复用中继路径。
- 远程画面、鼠标键盘操作、全屏。
- 双向文件上传和下载、暂停、断点续传、完成校验。
- 首次配对、永久设备绑定、设备撤销；撤销后重新接入必须再次输入配对码。
- UAC 提示的远程可见与可操作支持。
- 单机会话内的临时文件访问范围（scope）授权；可写回注册表作为该设备的常用授权目录。
- 首次连接到对端设备时，控制端展示对端设备指纹并需用户确认（TOFU）。**仅适用于经中继的连接**；局域网直连复用已配对设备公钥直接握手，不再弹指纹确认。

第一版不提供：

- 音频、剪贴板同步、聊天、终端、多人组织管理或远程电源管理。
- Windows 登录界面、锁屏界面或默认隔离 Secure Desktop 的捕获/交互。
- 公网上的端对端直连（穿透 NAT / 打洞 / 公共中继服务）；局域网 IP 直连是唯一支持的直连形式。
- mDNS / DNS-SD 自动发现；仅支持手动输入 `IP:端口`。
- 直连到中继的自动回退；用户在设备页明确选择"局域网"或"中继"模式后保持该路径，失败时由用户手动切换。
- 共享链接、设备分组或账号体系。

## 使用流程

1. 在每台电脑安装 DeskLink。安装程序以管理员权限部署 Windows 服务和 UIAccess 桌面助手；安装末尾询问"是否允许局域网直连"，同意后用 `netsh advfirewall` 放行固定端口（默认 `47200/TCP+UDP`），卸载时清理。
2. 被控端显示一次性配对码。控制端输入该代码，被控端确认后两台设备永久绑定。
3. 控制端在设备页选择连接路径：
   - **中继**：点击在线设备进入远程桌面；首次连接展示对端设备指纹并需用户显式确认。
   - **局域网**：点击"局域网直连"按钮，弹窗输入 `IP:端口`，直接握手进入远程桌面；不弹指纹确认（已配对即信任），不走中继。
4. 会话内只显示文件、全屏和断开三个操作。文件操作打开传输页，支持选择上传、选择远端文件下载、暂停和继续；冲突时由用户选择覆盖、重命名或跳过。
5. 被控端以持续但低干扰的状态条表示正在被控制，并可随时断开。被控端可在设备页撤销任意设备；撤销后该设备若要再次接入，必须重新输入新的配对码。

## 架构

### Windows 客户端

`DeskLink.exe` 是控制端 UI 和被控端交互入口。它显示设备、远程画面和文件传输状态，采集键盘鼠标，在被控端负责配对确认与会话状态。

### Windows 服务

`DeskLinkService` 以 LocalSystem 运行，开机启动。它拥有设备密钥，维护到中继的出站 QUIC 连接，验证会话和配对请求，并通过 Windows 会话管理启动或监督相应用户会话中的桌面助手。被控端的 `DeskLinkService` 额外在局域网直连端口（默认 `47200/TCP+UDP`）上监听入站，作为局域网直连的"服务端"。

### UIAccess 桌面助手

`DeskLinkDesktop` 在活跃 Windows 用户会话中工作。它使用 DXGI Desktop Duplication 捕获桌面，用 Media Foundation 的 H.264 硬件编码器编码，并注入已授权的鼠标键盘事件。该二进制文件经过代码签名、安装在 Program Files，并使用 UIAccess。

为使远端能够处理 UAC，安装程序在用户明确同意后配置 Windows 的 UIAccess 远程辅助策略。UAC 提示仍需用户点击同意或输入凭据，但它显示在可远控的交互桌面而非默认隔离 Secure Desktop。卸载时恢复该项设置。

### VPS 服务

VPS 部署两个服务：

- `relay`：无状态 QUIC 接入与包转发。它鉴别设备、建立控制端与被控端的转发会话，并只转发密文。
- `registry`：保存设备公钥、加密昵称、配对关系、撤销状态及最近在线时间的小型数据库。

VPS 不存储桌面图像、控制事件、文件内容、Windows 凭据或设备私钥。

## 传输路径

DeskLink 支持两条并存的会话路径，业务协议复用，仅传输入口不同：

| 路径 | 触发 | 传输 | 鉴权 | 中继依赖 | 是否弹指纹确认 |
|---|---|---|---|---|---|
| **中继** | 设备页点击在线设备 | 控制端/被控端 → `relay` → 对端（QUIC 优先，回落 TCP/TLS） | 设备 Ed25519 挑战（经 registry 校验） | 必需 | 首次必弹；公钥变更再弹 |
| **局域网直连** | 设备页"局域网直连" → 输入 `IP:端口` | 控制端 → 被控端（QUIC 优先，回落 TCP/TLS；被控端作为服务端） | 复用已配对设备公钥，直接 E2E 握手 | 无 | 否（已配对即信任） |

两条路径共享同一套 E2E 加密、会话密钥派生、流复用与业务帧格式；中继是"无状态代理"，局域网直连是"对等直连"，本质都是**控制端与被控端之间的一次 E2E 会话**，区别仅在握手起点和传输入口。

## 传输协议

### 中继路径

客户端先各自建立到 `relay` 的 QUIC/TLS 出站连接。建立会话后，控制端与被控端用已配对的设备公钥派生会话密钥；所有业务帧在进入 relay 前以该会话密钥进行额外认证加密。

### 局域网直连路径

控制端向被控端 `IP:端口` 发起 QUIC 或 TCP/TLS 出站连接。被控端 `DeskLinkService` 在局域网直连端口上以"服务端"身份监听。握手不复用中继挑战（不经 relay 也不经 registry），而是直接使用已配对设备公钥做 X25519 + Ed25519 SIGMA 简化式 → HKDF → 双向 AES-256-GCM。**直连不弹指纹确认**：配对是公钥信任的根来源，已配对即放行。

### 业务帧（三类逻辑流）

两条路径共用三类独立逻辑流：

- 桌面流：H.264 视频帧、显示器拓扑和尺寸变更事件。
- 控制流：鼠标移动、按键、滚轮、会话控制和自适应码率反馈。
- 文件流：文件清单、分块数据、恢复点和 BLAKE3 完整性摘要。

中继可以看到连接时间、在线状态和流量大小，但不能解密三类业务帧；局域网直连无中继，业务帧由端到端密钥加密，网络层只看到密文。

### 路径切换

设备页提供"中继"和"局域网"两种入口，**用户在 UI 显式选择，路径之间不自动切换**。一种路径失败时保持该状态，由用户手动重试或切换路径；网络短暂中断时，服务在该路径上做指数退避重连，重连成功后桌面会话重新协商，文件流从已确认分块继续。

## 设备身份和权限

每次安装生成 Ed25519/X25519 设备密钥。私钥使用 Windows DPAPI 保护，只保存在本机。配对码为短时、单次、限速的人工确认凭据；完成配对后，registry 只保存双方公钥和配对关系。

只有已配对设备能发起连接。被控端可在设备页撤销任意设备；撤销立即使相关会话失效，并清除该配对关系；被撤销设备若要再次接入，必须重新输入新的配对码完成配对。撤销同时关闭中继会话与局域网直连监听中的相关会话。首次版本不支持账户体系、共享链接或设备分组。

首次经中继连接到对端设备时，控制端展示对端设备公钥指纹（Ed25519 公钥的 SHA-256 十六进制或可视化指纹），需用户显式确认后才完成握手；后续连接在同一中继 + 同一公钥前提下自动通过，公钥变更时再次确认。**局域网直连不复用此 TOFU**：配对已是公钥信任的根来源，直连握手直接用已配对公钥完成，不弹指纹确认。

## 文件安全

文件传输必须由会话内显式操作触发。被控端仅在用户选择的文件或目录范围内提供上传与下载，不提供默认全盘浏览。传输写入临时 `.part` 文件；分块确认后支持恢复，最终 BLAKE3 校验通过才原子性改名为目标文件。

文件冲突策略：

- 覆盖：以新文件替换目标，跳过备份。
- 重命名：自动在文件名后追加 `(n)` 后缀，不覆盖已有文件。
- 跳过：保留已存在目标，仅跳过当前文件。

策略由用户在文件传输页选择；不同文件可使用不同策略。scope 之外的访问请求一律拒绝。

## 性能和降级

桌面默认优先使用 H.264 硬件编码。网络变差时先降低帧率和码率，再降低分辨率，以保持输入控制响应。无法获得硬件编码器时改用受限的软件编码，并在会话状态中告知用户。多显示器第一版支持选择单个显示器；显示器切换不创建第二路并行画面。

DXGI 捕获因模式切换、DPI 变化或驱动异常触发 `ACCESS_LOST` 时，整条管线（捕获→处理→编码→发送）将重新初始化，期间会话状态条显示“正在恢复画面”以避免用户误判为掉线。

## 错误处理

- 设备离线：显示离线，提供重试，不暴露网络诊断细节。
- 中继不可达：显示服务不可用，保留本地传输状态并自动退避重连。
- UAC 策略缺失或桌面助手异常：拒绝开始需要管理员操作的会话，并显示修复安装入口。
- 用户锁屏或尚未登录：显示等待本地登录；不尝试控制 Windows 登录界面。
- 磁盘空间不足、文件冲突或摘要校验失败：停止对应文件流，保留可恢复状态和清晰原因。文件冲突场景见上文三选项策略。
- 输入注入被 UIPI 拒绝：会话状态显示“被控端需要重新登录以恢复控制”，并提供修复入口（重新以提升令牌启动代理）。

## 验收与测试

- Windows 10 Pro 和 Windows 11 Pro：安装、卸载、服务开机启动、UIAccess 策略恢复，以及**局域网直连端口的防火墙规则放行与清理**。
- 两个不同 NAT 网络经 VPS：配对、连接、远程键鼠、全屏和设备撤销；撤销后重新配对可工作。
- **同子网局域网直连**：配对过的两台机器手动输入 `IP:端口` 直连成功，远程键鼠、全屏、文件传输与中继路径等价；未配对设备的直连请求被拒绝。
- 桌面捕获：不同分辨率、DPI 和单显示器切换；ACCESS_LOST 重建可见且不丢失控制权。
- UAC：管理员确认与凭据输入在已明确启用的 UIAccess 策略下可见且可操作。
- 网络：短暂断网、网络切换及 relay 重启后的恢复；**局域网直连端口断开后的会话清理**。
- 文件：大文件、暂停恢复、冲突处理（覆盖/重命名/跳过）、网络中断后续传和 BLAKE3 失败。
- 安全：未配对设备、过期配对码、重复配对码、撤销后的连接请求均被拒绝；对端公钥变更时强制 TOFU 确认（仅中继路径）；**局域网直连不接受未配对设备的握手**。

## 部署

VPS 仅开放 relay 所需的 HTTPS/QUIC 入口。registry 不暴露公网。控制端和被控端的中继连接均使用出站；不要求用户配置路由器、防火墙入站规则或固定公网 IP。

**局域网直连**需要在被控端开放入站端口。安装程序末尾询问"是否允许局域网直连"：

- 同意：用 `netsh advfirewall firewall add rule` 放行 `DeskLinkService` 进程对默认端口 `47200/TCP` 和 `47200/UDP` 的入站连接，并记录到注册表作为后续修复入口的依据。
- 不同意：不放行端口，设备页的"局域网直连"入口显示为禁用并提示"未启用局域网直连"。
- 卸载：删除对应的防火墙规则，恢复到装机前状态。
- 用户后续可在 Settings 里重新启用或禁用，启用时按需补放行规则，禁用时清除规则。

默认端口 `47200` 在 Settings 暴露为可配置项；端口变更时同步更新防火墙规则。

---

# DeskLink 实施计划

## Context

在空目录 `D:\程序\desklink`（即原 `c:\Mycode\desklink` 的实际工作区）从零开发一套自用 Windows 10/11 远程桌面软件：跨 NAT 一对一远控 + 双向文件传输，经用户自有 VPS 中继，极简 UI。按规格同时做 Windows 三件套（客户端 UI / 系统服务 / 桌面代理）与 VPS 两件套（relay / registry）。规格允许对不合适的部分做修改完善。

## 已确认决策（用户拍板）

| 决策点 | 结论 |
|---|---|
| 技术栈 | Go（VPS：relay + registry）+ C#/.NET 10（Windows 全部组件，WPF UI） |
| 传输 | QUIC 优先（Win11），自动回落 TCP/TLS（Win10 无 QUIC 加密 API、UDP 被封时）；relay 双监听 |
| UAC | 安装时经用户同意设置 `PromptOnSecureDesktop=0`（UAC 弹窗回到交互桌面即可被远控），卸载恢复；不做 UIAccess 代码签名 |
| 依赖 | C#：BouncyCastle（Ed25519/X25519）、Vortice.Windows（DXGI/D3D11/MF）、Microsoft.Extensions.Hosting.WindowsServices；Go：quic-go、modernc.org/sqlite（纯 Go） |
| 输入环路防护 | Service 启动时若启用 Agent 注入，则要求传入 `--no-inject`（仅开发自控自场景）；未传则 hard-fail，防止 SendInput 自环 |
| DXGI 重建 | ACCESS_LOST 时整管线重建，期间状态条显示"正在恢复画面"，不丢控制权 |
| 撤销重配 | 撤销立即清除配对关系并踢线；该设备再次接入必须重新输入新的配对码 |
| 对端指纹 | 首次经中继连接对端时强制 TOFU 展示并确认指纹；公钥变更需再次确认。**局域网直连不复用此 TOFU**，直接复用已配对公钥握手 |
| 局域网直连 | 同子网内点对点，控制端手动输入 `IP:端口`；被控端在默认端口 `47200/TCP+UDP` 上以服务端身份监听；E2E 加密与业务协议复用中继路径；安装时询问并用 `netsh advfirewall` 放行端口，卸载清理；路径由用户在 UI 显式选择，不自动切换 |

## 架构总览

```
控制端 DeskLink.exe(WPF) ←命名管道→ 控制端 DeskLinkService ──┬── 出站 QUIC 或 TCP/TLS ──→ relayd ──→ 被控端 DeskLinkService
                                                                                                          ↑
被控端 DeskLink.exe(WPF) ←命名管道→ 被控端 DeskLinkService ──┤                                                │
                                       被控端 DeskLinkDesktop(会话内桌面代理) ←管道→ 被控端服务             │
                                                            VPS: relayd(Go, 双监听) ─loopback HTTP→ registryd(Go+SQLite, 仅127.0.0.1)
                                                            relay 内部 unix socket ←推送→ relay（撤销即时生效）

同子网局域网直连（不走 relay，不走 registry）：
控制端 DeskLinkService ── 出站 QUIC/TCP/TLS ──→ 被控端 DeskLinkService（监听 47200/TCP+UDP）
```

- **DeskLinkService（LocalSystem，两端都装）**：设备密钥（DPAPI LocalMachine + 文件 ACL）、中继连接与在线状态、指数退避重连、E2E 会话（安全核心）、文件传输引擎（scope/.part/B3/原子改名/三选项冲突）、把桌面代理以**用户提升令牌**（WTSQueryUserToken→TokenLinkedToken）注入活动会话。被控端额外在默认端口 `47200/TCP+UDP` 上作为服务端监听，接受局域网直连入站。`--console` 开发模式。
- **DeskLinkDesktop（会话内代理）**：DXGI Desktop Duplication 捕获 → D3D11 VideoProcessor 转 NV12 → MF H.264 编码（先试硬件 MFT，失败自动回落软件 MFT，会话状态标注）→ 编码码流过管道；指针形状/位置独立控制帧（客户端合成光标）；SendInput scancode 注入；WTS 锁屏检测；ACCESS_LOST 整管线重建并通知会话。开发模式支持手动 `--pipe` 挂接；`--no-inject` 时禁用真实 SendInput，输入桩返回成功仅用于自控自联调。
- **DeskLink.exe（WPF）**：设备页（配对/在线/撤销/重配入口）、远程页（MF 软解 H.264 → WriteableBitmap、Raw Input 全屏键盘、仅 文件/全屏/断开 三操作）、文件页（浏览授权目录、上传下载、暂停续传进度、覆盖/重命名/跳过冲突策略）、被控状态条、设置（中继地址 + 证书指纹 TOFU 首连确认）。
- **relayd（Go）**：QUIC(quic-go) + TCP/TLS 双监听；设备 Ed25519 挑战认证（经 registry 校验）；内存接线表 + 30s 重连宽限窗；配对后仅泵送密文；代理 registry 请求（registry 不暴露公网，loopback + token）；配对码爆破限速（IP+码）；通过本地 unix socket 接收 registry 推送（撤销/公钥变更）并踢活跃会话。
- **registryd（Go）**：SQLite；devices（公钥、加密昵称、lastSeen）、pairs、revocations、pairing_codes（哈希存储、单事务原子领取、短时效单次）。通过 unix socket 向 relay 推送状态变更事件。

### 端到端安全协议（C# 两侧实现，relay 只见密文）

- 配对：被控端出码 → registry 存哈希；控制端输入码 → 单事务领取 → 双方互存公钥 → 被控端经中继通知持久化。**配对是局域网直连握手的前提**：未配对设备的局域网直连请求一律拒绝。
- 会话握手（中继路径）：控制端/被控端各自出站连 relay → relay 经 registry 校验设备 Ed25519 挑战 → 中继转发握手消息 → 两侧 X25519 临时密钥 + Ed25519 对 transcript 签名（SIGMA 简化式）→ HKDF → 双向 AES-256-GCM 会话密钥（序号派生 nonce）。**每次重连完整重握手**。
- 会话握手（局域网直连路径）：控制端直接出站连被控端 `IP:端口` → **不经 relay、不经 registry**，直接复用已配对设备公钥做 X25519 + Ed25519 SIGMA 简化式 → HKDF → 双向 AES-256-GCM。被控端在握手时校验对端公钥是否在已配对列表中，不在则拒绝。**不弹指纹确认**。
- 对端指纹确认：**仅中继路径**。首次握手成功后控制端展示对端 Ed25519 公钥指纹（SHA-256 十六进制或可视化），用户确认后才进入业务流；协议层预留 `device_id_hint` 字段，避免日后改协议。
- 会话内三条逻辑流：desktop / control / file，统一内层帧目录，QUIC=原生流、TCP=[u32 len][u8 sid] mux（帧上限 256KB，关闭语义固定）。两条路径共用同一帧格式。
- 撤销：relay 接线前查 registry 配对+撤销；registry 撤销/公钥变更事件经 unix socket 推 relay，relay 踢活跃会话并清接线。**被控端在收到撤销事件时也关闭局域网直连路径上的相关会话**，确保两条路径同时失效。
- 文件：分块 + ack 位图（断点续传只认 ack 位图）、BLAKE3（托管 C# 实现，官方测试向量验证）、`.part` 临时文件、校验通过原子改名、scope 之外一律拒绝；冲突按用户在 UI 选择的策略处理（覆盖 / 重命名 / 跳过）。

## 仓库结构

```
desklink/
├─ DeskLink.sln
├─ DESIGN.md                              # 本文件
├─ README.md
├─ src/
│  ├─ Protocol/DeskLink.Protocol/          # 纯 C# 类库：内层帧目录、mux、E2E 握手、BLAKE3、relay 控制帧、device_id_hint、路径协商
│  ├─ vps/                                 # 单一 Go module（module desklink/vps）
│  │  ├─ cmd/{relayd,registryd}/main.go
│  │  ├─ internal/proto/proto.go           # 与 C# 常量对齐
│  │  ├─ internal/{server(quic/tcp/wiring/pump/ratelimit), regclient, regstore(sqlite), regapi, regnotify(unix socket)}
│  ├─ Service/DeskLink.Service/            # KeyStore(DPAPI) / RelayClient(QUIC→TCP探测+退避) / DirectServer(QUIC+TCP/TLS 服务端，监听 47200) / DirectClient(出站握手) / SessionHost / FileManager / PairingManager / PipeServer / ProcessLauncher(WTS) / --no-inject / console 模式 / FirewallHelper(netsh advfirewall)
│  ├─ Agent/DeskLink.DesktopAgent/         # DuplicationCapture / VideoProcessor(NV12) / H264Encoder(MFTEnumEx→IMFTransform+ICodecAPI) / InputInjector / SessionWatcher / AccessLostRecovery / PipeChannel
│  └─ Client/DeskLink.Client/              # WPF：Devices/Remote/Files/StatusBar/Settings 视图、H264Decoder、RawInputHook、PipeClient、FingerprintConfirmDialog、DirectConnectDialog(IP:端口 输入)
├─ installer/{install.ps1, uninstall.ps1, vps/{deploy.sh, gen-cert.sh, relay.service, registry.service}}
└─ tests/{Protocol.Tests(xunit), DirectHandshake.Tests, e2e-smoke.ps1}
```

## 与规格的偏差（修改完善项）

1. QUIC 优先 + TCP/TLS 兜底（微软官方：.NET QUIC 仅 Win11+，Win10 缺加密 API）。
2. UAC 用 `PromptOnSecureDesktop=0` 策略替代需代码签名的 UIAccess 清单；代理用**用户提升令牌**运行解决 UIPI 注入。
3. relay 并非字面"无状态"：内存接线表 + 30s 重连宽限窗（重启即断会话，两端退避）；撤销/公钥变更经 unix socket 即时推送。
4. 安装用 PowerShell 脚本 + Program Files 目录，不做 MSI。
5. registry 仅经 relay 代理访问（严格不暴露公网）。
6. 硬件编码器自动探测、异常自动回落软件编码（规格本身允许降级）。
7. 撤销立即清除配对关系；该设备再次接入必须重新输入新配对码（规格原本仅说"撤销使会话失效"，此处收紧避免默默复活）。
8. 首次经中继连接到对端强制展示指纹并确认（TOFU），后续同公钥自动通过；公钥变更再次确认。**局域网直连不复用此 TOFU**。
9. 文件冲突三选项 UX（覆盖 / 重命名 / 跳过），规格原本仅说"停止并提示原因"，此处放宽以保证可用性。
10. **新增局域网 IP 直连能力**（规格原列在"不提供"清单中）：同子网点对点，控制端手动输入 `IP:端口`；安装时询问并放行端口；不复用 TOFU 指纹确认，不走中继/registry；用户在 UI 显式选择路径，不自动切换；撤销同时关闭两条路径的会话。

## 实施阶段（每阶段有单机验证点）

- **P0 环境**：`winget install GoLang.Go`、`winget install Microsoft.DotNet.SDK.10`（若 .NET 10 仍处 Preview 则回落 `Microsoft.DotNet.SDK.9`，并在 README 注明）；验证 `go version`、`dotnet --version`。NuGet 可用（已验证联网）。
- **P1 协议库**：帧编解码、mux、E2E 握手、BLAKE3（嵌入官方测试向量，开发时从 BLAKE3 官方仓库拉取校验数据）、`device_id_hint` 字段预留 → `dotnet test` 全绿。
- **P2 registryd**：SQLite + 签名验证 + 配对码原子领取/限速 + unix socket 推送通道 → Go 测试 + curl 验证 + `socat` 验证推送。
- **P3 relayd**：双监听、挑战认证、接线泵送、宽限窗、撤销/公钥变更踢线（经 unix socket 订阅）、限速 → Go 测试客户端打通两传输。
- **P4 Service 骨架**：KeyStore / RelayClient / 管道 RPC / `--no-inject` 参数 / console 模式（`--data-dir` 支持双实例）→ **单机双实例经本地 relay 配对成功**。
- **P5 E2E 会话**：握手、对端指纹确认 UI（中继路径）、控制流往返、重连退避 + 重握手 + 文件层仅认 ack 位图 → 双实例加密帧往返验证；首次对端连接走 TOFU 流程（中继）。
- **P5.5 局域网直连骨架**：被控端 `DeskLinkService` 监听 `47200/TCP+UDP` 服务端实现（QUIC 优先 + TCP/TLS 兜底）；控制端 DirectClient 出站握手；未配对设备拒绝；与中继路径共享 E2E 握手、流复用与业务帧；UI 加 `DirectConnectDialog`（`IP:端口` 输入）；不弹指纹确认。`tests/DirectHandshake.Tests` 用本地双实例验证握手成功、帧往返、未配对被拒、撤销即时关闭直连会话。
- **P5.6 防火墙与端口策略**：`FirewallHelper` 封装 `netsh advfirewall` 的规则添加/删除/查询；`install.ps1` 末尾交互式询问并放行端口；`uninstall.ps1` 清理；Settings 暴露端口配置，变更时同步规则。
- **P6 文件流**：scope 浏览、分块/ack、暂停恢复、B3、.part/原子改名、覆盖/重命名/跳过冲突策略、冲突/盘满 → 单机全部用例。
- **P7 桌面代理**：捕获→NV12→软件 H.264→管道；注入；锁屏检测；ACCESS_LOST 重建与状态通知 → 本地回环（测试图源→编码→解码→像素校验）。
- **P8 WPF 客户端**：四视图 + 中继 TOFU + 对端指纹 TOFU + 全屏 Raw Input + 冲突策略 UI → **单机全链路冒烟：本地 relay+registry，agent 捕本机桌面，WPF 自控自**（`e2e-smoke.ps1`，依赖 `--no-inject` 防输入环路）。
- **P9 打磨**：硬件 MFT 路径 + 自适应码率（先码率帧率、后分辨率）+ 多显示器/DPI/旋转（ACCESS_LOST 重建管线）。
- **P9.5 已知问题与限制**：撰写 README/KnownIssues.md，覆盖驱动兼容性、GPU/分辨率上限、DPAPI 重装不可恢复、自用范畴下的功能取舍。
- **P10 部署**：`install.ps1` / `uninstall.ps1`（服务注册 + UAC 策略切换/恢复 + 修复入口 + `--print-first-run-info` 打印首次启动指引）、VPS systemd/证书脚本/防火墙策略。

## 关键风险与对策（评审确认）

- **UIPI**：代理用 `WTSQueryUserToken`→`GetTokenInformation(TokenLinkedToken)` 提升令牌启动，否则点不动高完整性窗口（含 UAC 弹窗）。SendInput 失败时报告 UIPI 拒绝并提示"被控端需重新登录"。
- **硬件 MFT interop 是最痛点**：v1 软件同步 MFT 保底可靠，硬件路径独立开关 + 自动回落。软编下分辨率上限可收紧（如 720p）作为 v1 兜底。
- **DPAPI 作用域**：统一 LocalMachine + 文件 ACL 只留 SYSTEM/Admins（console dev 模式与 SYSTEM 模式一致）；不做跨重装恢复（自用可接受）；README 中明确告知。
- **管道只走已编码码流**（几 Mbps），NV12 90MB/s 绝不过管道；输入线程与渲染线程分离。
- **DXGI 细节**：指针不在帧内（独立控制帧客户端合成）；模式切换/DPI 变化 → `ACCESS_LOST` 整管线重建并通知状态条；SPS/PPS 会话开始先发。
- **System.Net.Quic**：必填 `DefaultStreamErrorCode/DefaultCloseErrorCode`，显式调大 `MaxInboundBidirectionalStreams`；TOFU pin 在 `RemoteCertificateValidationCallback`。
- **relay → registry 推送**：registry 变更经 unix socket 推到 relay，relay 立即踢线并清接线；不依赖定期拉取，保证撤销即时生效。
- **输入环路**：P4 起 Service 启动 Agent 时若未传 `--no-inject` 则 hard-fail；联调脚本必须显式启用。
- **对端指纹确认**：协议层 `device_id_hint` 在 P1 即预留；P5 加 UI 强制确认（仅中继路径）。
- **局域网直连握手**：被控端 DirectServer 必须在握手时校验对端公钥是否在已配对列表中，未配对立即拒绝；不依赖 relay 的挑战，避免误用 registry 路径。
- **防火墙规则生命周期**：`install.ps1` 询问 → `netsh advfirewall` 放行 → 注册表记录启用状态；卸载必须清理；端口变更需同步更新规则（删除旧规则 + 添加新规则）；进程路径移动后规则失效需要重装或修复入口。
- **路径选择**：用户在 UI 显式选择"中继"或"局域网"，**不自动回退**；一种路径失败时保持该状态，由用户手动切换，避免半路切换导致会话状态混乱。

## 验证

- 单测：`dotnet test`（协议/BLAKE3/文件引擎/冲突策略/直连握手）+ `go test ./...`（registry/relay/推送通道）。
- 直连单测 `tests/DirectHandshake.Tests`：本地双实例 → 已配对握手成功、帧往返、未配对设备被拒、撤销后直连会话立即关闭、防火墙规则添加/删除幂等。
- 单机冒烟 `tests/e2e-smoke.ps1`：本地 relay+registry → 两个 console 实例配对 → 会话加密帧往返 + 对端指纹确认（中继路径）→ 文件断点续传/B3/三选项冲突 → agent 真捕获 + ACCESS_LOST 重建 → WPF 全链路自控（`--no-inject`）→ **追加局域网直连冒烟**：同一双实例改用 DirectConnectDialog 输入 `127.0.0.1:47200` 完成直连会话、不弹指纹确认、走本地服务端口。
- 需双机/VPS 才能验证（交付清单形式输出）：真实 NAT、公网 TOFU、Win10 TCP 回退端到端、撤销即时踢线（含 unix socket 推送路径 + 局域网直连会话同步关闭）、1080p30 真实带宽、对端公钥变更触发二次确认、**两台同子网机器 `IP:端口` 直连端到端**。

## 风险回顾（按评审补充）

1. **.NET 10 SDK 时机**：截至 .NET 10 Preview 阶段需回落 .NET 9，并 README 注明。
2. **软件 MFT 性能上限**：1080p30 在中等 CPU 上若不达标，软编版硬上限收紧到 720p，避免模糊降级。
3. **DXGI ACCESS_LOST 黑窗期**：1-3 秒内状态条必须显示"正在恢复画面"，避免用户误判为掉线；重建期间不丢控制权。
4. **`--no-inject` 强制**：P4 起 Service 启动 Agent 注入分支时缺参即 hard-fail；`e2e-smoke.ps1` 必须显式启用 `--no-inject`。
5. **撤销语义**：撤销立即清配对关系并踢线，再次接入必须重新输码；协议无"复活"路径。
6. **首次对端指纹确认**：`device_id_hint` 字段 P1 预留，P5 接 UI，避免后续改协议。
7. **局域网直连握手未配对拒绝**：被控端 DirectServer 在握手时校验对端公钥是否在已配对列表中，不在则立即拒绝；不得通过中继挑战绕过该校验。
8. **防火墙规则残留**：卸载必须清理 `netsh advfirewall` 规则；端口变更需同步（删旧加新）；进程路径变更后规则失效需要修复入口；安装询问时记录注册表作为后续修复依据。
9. **路径选择无自动回退**：用户在 UI 显式选择路径后保持该路径；一种路径失败不自动切换，避免会话状态混乱。
10. **局域网直连无中继挑战**：被控端 DirectServer 不依赖 registry 校验设备，必须严格按已配对公钥列表过滤，否则可被未配对设备接入。
