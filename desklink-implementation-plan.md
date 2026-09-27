基于以下需求完成并开发对应程序，不合适的可以修改完善。：# DeskLink 设计规格 
 
 ## 目标 
 
 DeskLink 是一套自用的 Windows 10/11 远程桌面软件。它在不同网络间提供一对一远程控制和双向文件操作，使用用户自有 VPS 中继，界面保持极简。 
 
 ## 范围 
 
 第一版提供： 
 
 - Windows 10/11 控制端与被控端。 
 - 跨 NAT 和防火墙的中继连接；两端只需可访问互联网，无需端口映射。 
 - 远程画面、鼠标键盘操作、全屏。 
 - 双向文件上传和下载、暂停、断点续传、完成校验。 
 - 首次配对、永久设备绑定、设备撤销。 
 - UAC 提示的远程可见与可操作支持。 
 
 第一版不提供： 
 
 - 音频、剪贴板同步、聊天、终端、多人组织管理或远程电源管理。 
 - Windows 登录界面、锁屏界面或默认隔离 Secure Desktop 的捕获/交互。 
 - 端对端直连或公共中继服务。 
 
 ## 使用流程 
 
 1. 在每台电脑安装 DeskLink。安装程序以管理员权限部署 Windows 服务和 UIAccess 桌面助手。 
 2. 被控端显示一次性配对码。控制端输入该代码，被控端确认后两台设备永久绑定。 
 3. 控制端在设备页点击在线设备，即进入远程桌面。 
 4. 会话内只显示文件、全屏和断开三个操作。文件操作打开传输页，支持选择上传、选择远端文件下载、暂停和继续。 
 5. 被控端以持续但低干扰的状态条表示正在被控制，并可随时断开。 
 
 ## 架构 
 
 ### Windows 客户端 
 
 `DeskLink.exe` 是控制端 UI 和被控端交互入口。它显示设备、远程画面和文件传输状态，采集键盘鼠标，在被控端负责配对确认与会话状态。 
 
 ### Windows 服务 
 
 `DeskLinkService` 以 LocalSystem 运行，开机启动。它拥有设备密钥，维护到中继的出站 QUIC 连接，验证会话和配对请求，并通过 Windows 会话管理启动或监督相应用户会话中的桌面助手。 
 
 ### UIAccess 桌面助手 
 
 `DeskLinkDesktop` 在活跃 Windows 用户会话中工作。它使用 DXGI Desktop Duplication 捕获桌面，用 Media Foundation 的 H.264 硬件编码器编码，并注入已授权的鼠标键盘事件。该二进制文件经过代码签名、安装在 Program Files，并使用 UIAccess。 
 
 为使远端能够处理 UAC，安装程序在用户明确同意后配置 Windows 的 UIAccess 远程辅助策略。UAC 提示仍需用户点击同意或输入凭据，但它显示在可远控的交互桌面而非默认隔离 Secure Desktop。卸载时恢复该项设置。 
 
 ### VPS 服务 
 
 VPS 部署两个服务： 
 
 - `relay`：无状态 QUIC 接入与包转发。它鉴别设备、建立控制端与被控端的转发会话，并只转发密文。 
 - `registry`：保存设备公钥、加密昵称、配对关系、撤销状态及最近在线时间的小型数据库。 
 
 VPS 不存储桌面图像、控制事件、文件内容、Windows 凭据或设备私钥。 
 
 ## 传输协议 
 
 客户端先各自建立到 `relay` 的 QUIC/TLS 出站连接。建立会话后，控制端与被控端用已配对的设备公钥派生会话密钥；所有业务帧在进入 relay 前以该会话密钥进行额外认证加密。 
 
 协议有三类独立逻辑流： 
 
 - 桌面流：H.264 视频帧、显示器拓扑和尺寸变更事件。 
 - 控制流：鼠标移动、按键、滚轮、会话控制和自适应码率反馈。 
 - 文件流：文件清单、分块数据、恢复点和 BLAKE3 完整性摘要。 
 
 relay 可以看到连接时间、在线状态和流量大小，但不能解密三类业务帧。网络短暂中断时，服务采用指数退避重连；重连成功后桌面会话重新协商，文件流从已确认分块继续。 
 
 ## 设备身份和权限 
 
 每次安装生成 Ed25519/X25519 设备密钥。私钥使用 Windows DPAPI 保护，只保存在本机。配对码为短时、单次、限速的人工确认凭据；完成配对后，registry 只保存双方公钥和配对关系。 
 
 只有已配对设备能发起连接。被控端可在设备页撤销任意设备；撤销立即使相关会话失效。首次版本不支持账户体系、共享链接或设备分组。 
 
 ## 文件安全 
 
 文件传输必须由会话内显式操作触发。被控端仅在用户选择的文件或目录范围内提供上传与下载，不提供默认全盘浏览。传输写入临时 `.part` 文件；分块确认后支持恢复，最终 BLAKE3 校验通过才原子性改名为目标文件。 
 
 ## 性能和降级 
 
 桌面默认优先使用 H.264 硬件编码。网络变差时先降低帧率和码率，再降低分辨率，以保持输入控制响应。无法获得硬件编码器时改用受限的软件编码，并在会话状态中告知用户。多显示器第一版支持选择单个显示器；显示器切换不创建第二路并行画面。 
 
 ## 错误处理 
 
 - 设备离线：显示离线，提供重试，不暴露网络诊断细节。 
 - 中继不可达：显示服务不可用，保留本地传输状态并自动退避重连。 
 - UAC 策略缺失或桌面助手异常：拒绝开始需要管理员操作的会话，并显示修复安装入口。 
 - 用户锁屏或尚未登录：显示等待本地登录；不尝试控制 Windows 登录界面。 
 - 磁盘空间不足、文件冲突或摘要校验失败：停止对应文件流，保留可恢复状态和清晰原因。 
 
 ## 验收与测试 
 
 - Windows 10 Pro 和 Windows 11 Pro：安装、卸载、服务开机启动及 UIAccess 策略恢复。 
 - 两个不同 NAT 网络经 VPS：配对、连接、远程键鼠、全屏和设备撤销。 
 - 桌面捕获：不同分辨率、DPI 和单显示器切换。 
 - UAC：管理员确认与凭据输入在已明确启用的 UIAccess 策略下可见且可操作。 
 - 网络：短暂断网、网络切换及 relay 重启后的恢复。 
 - 文件：大文件、暂停恢复、冲突处理、网络中断后续传和 BLAKE3 失败。 
 - 安全：未配对设备、过期配对码、重复配对码和撤销后的连接请求均被拒绝。 
 
 ## 部署 
 
 VPS 仅开放 relay 所需的 HTTPS/QUIC 入口。registry 不暴露公网。控制端和被控端均使用出站连接；不要求用户配置路由器、防火墙入站规则或固定公网 IP。 








# DeskLink 实施计划

## Context

在空目录 `c:\Mycode\desklink` 从零开发一套自用 Windows 10/11 远程桌面软件：跨 NAT 一对一远控 + 双向文件传输，经用户自有 VPS 中继，极简 UI。按规格同时做 Windows 三件套（客户端 UI / 系统服务 / 桌面代理）与 VPS 两件套（relay / registry）。规格允许对不合适的部分做修改完善。

## 已确认决策（用户拍板）

| 决策点 | 结论 |
|---|---|
| 技术栈 | Go（VPS：relay + registry）+ C#/.NET 10（Windows 全部组件，WPF UI） |
| 传输 | QUIC 优先（Win11），自动回落 TCP/TLS（Win10 无 QUIC 加密 API、UDP 被封时）；relay 双监听 |
| UAC | 安装时经用户同意设置 `PromptOnSecureDesktop=0`（UAC 弹窗回到交互桌面即可被远控），卸载恢复；不做 UIAccess 代码签名 |
| 依赖 | C#：BouncyCastle（Ed25519/X25519）、Vortice.Windows（DXGI/D3D11/MF）、Microsoft.Extensions.Hosting.WindowsServices；Go：quic-go、modernc.org/sqlite（纯 Go） |

## 架构总览

```
控制端 DeskLink.exe(WPF) ←命名管道→ 控制端 DeskLinkService ──┐
被控端 DeskLink.exe(WPF) ←命名管道→ 被控端 DeskLinkService ──┤ 出站 QUIC 或 TCP/TLS
                                       被控端 DeskLinkDesktop(会话内桌面代理) ←管道→ 被控端服务
                                                            VPS: relayd(Go, 双监听) ─loopback HTTP→ registryd(Go+SQLite, 仅127.0.0.1)
```

- **DeskLinkService（LocalSystem，两端都装）**：设备密钥（DPAPI LocalMachine + 文件 ACL）、中继连接与在线状态、指数退避重连、E2E 会话（安全核心）、文件传输引擎（scope/.part/B3/原子改名）、把桌面代理以**用户提升令牌**（WTSQueryUserToken→TokenLinkedToken）注入活动会话。`--console` 开发模式。
- **DeskLinkDesktop（会话内代理）**：DXGI Desktop Duplication 捕获 → D3D11 VideoProcessor 转 NV12 → MF H.264 编码（先试硬件 MFT，失败自动回落软件 MFT，会话状态标注）→ 编码码流过管道；指针形状/位置独立控制帧（客户端合成光标）；SendInput scancode 注入；WTS 锁屏检测。开发模式支持手动 `--pipe` 挂接。
- **DeskLink.exe（WPF）**：设备页（配对/在线/撤销）、远程页（MF 软解 H.264 → WriteableBitmap、Raw Input 全屏键盘、仅 文件/全屏/断开 三操作）、文件页（浏览授权目录、上传下载、暂停续传进度）、被控状态条、设置（中继地址 + 证书指纹 TOFU 首连确认）。
- **relayd（Go）**：QUIC(quic-go) + TCP/TLS 双监听；设备 Ed25519 挑战认证（经 registry 校验）；内存接线表 + 30s 重连宽限窗；配对后仅泵送密文；代理 registry 请求（registry 不暴露公网，loopback + token）；配对码爆破限速（IP+码）。
- **registryd（Go）**：SQLite；devices（公钥、加密昵称、lastSeen）、pairs、revocations、pairing_codes（哈希存储、单事务原子领取、短时效单次）。

### 端到端安全协议（C# 两侧实现，relay 只见密文）

- 配对：被控端出码 → registry 存哈希；控制端输入码 → 单事务领取 → 双方互存公钥 → 被控端经中继通知持久化。
- 会话握手：X25519 临时密钥 + Ed25519 对 transcript 签名（SIGMA 简化式）→ HKDF → 双向 AES-256-GCM 会话密钥（序号派生 nonce）。**每次重连完整重握手**。
- 会话内三条逻辑流：desktop / control / file，统一内层帧目录，QUIC=原生流、TCP=[u32 len][u8 sid] mux（帧上限 256KB，关闭语义固定）。
- 撤销：relay 接线前查 registry 配对+撤销；registry 变更推送 relay 踢活跃会话。
- 文件：分块 + ack 位图（断点续传只认 ack 位图）、BLAKE3（托管 C# 实现，官方测试向量验证）、`.part` 临时文件、校验通过原子改名、scope 之外一律拒绝。

## 仓库结构

```
desklink/
├─ DeskLink.sln
├─ src/
│  ├─ Protocol/DeskLink.Protocol/          # 纯 C# 类库：内层帧目录、mux、E2E 握手、BLAKE3、relay 控制帧
│  ├─ vps/                                 # 单一 Go module（module desklink/vps）
│  │  ├─ cmd/{relayd,registryd}/main.go
│  │  ├─ internal/proto/proto.go           # 与 C# 常量对齐
│  │  ├─ internal/{server(quic/tcp/wiring/pump/ratelimit), regclient, regstore(sqlite), regapi}
│  ├─ Service/DeskLink.Service/            # KeyStore(DPAPI) / RelayClient(QUIC→TCP探测+退避) / SessionHost / FileManager / PairingManager / PipeServer / ProcessLauncher(WTS) / console 模式
│  ├─ Agent/DeskLink.DesktopAgent/         # DuplicationCapture / VideoProcessor(NV12) / H264Encoder(MFTEnumEx→IMFTransform+ICodecAPI) / InputInjector / SessionWatcher / PipeChannel
│  └─ Client/DeskLink.Client/              # WPF：Devices/Remote/Files/StatusBar/Settings 视图、H264Decoder、RawInputHook、PipeClient
├─ installer/{install.ps1, uninstall.ps1, vps/{deploy.sh, gen-cert.sh, relay.service, registry.service}}
└─ tests/{Protocol.Tests(xunit), e2e-smoke.ps1}
```

## 与规格的偏差（修改完善项）

1. QUIC 优先 + TCP/TLS 兜底（微软官方：.NET QUIC 仅 Win11+，Win10 缺加密 API）。
2. UAC 用 `PromptOnSecureDesktop=0` 策略替代需代码签名的 UIAccess 清单；代理用**用户提升令牌**运行解决 UIPI 注入。
3. relay 并非字面"无状态"：内存接线表 + 30s 重连宽限窗（重启即断会话，两端退避）。
4. 安装用 PowerShell 脚本 + Program Files 目录，不做 MSI。
5. registry 仅经 relay 代理访问（严格不暴露公网）。
6. 硬件编码器自动探测、异常自动回落软件编码（规格本身允许降级）。

## 实施阶段（每阶段有单机验证点）

- **P0 环境**：`winget install GoLang.Go`、`winget install Microsoft.DotNet.SDK.10`（或回落 9）；验证 `go version`、`dotnet --version`。NuGet 可用（已验证联网）。
- **P1 协议库**：帧编解码、mux、E2E 握手、BLAKE3（嵌入官方测试向量，开发时从 BLAKE3 官方仓库拉取校验数据）→ `dotnet test` 全绿。
- **P2 registryd**：SQLite + 签名验证 + 配对码原子领取/限速 → Go 测试 + curl 验证。
- **P3 relayd**：双监听、挑战认证、接线泵送、宽限窗、撤销踢线、限速 → Go 测试客户端打通两传输。
- **P4 Service 骨架**：KeyStore / RelayClient / 管道 RPC / console 模式（`--data-dir` 支持双实例）→ **单机双实例经本地 relay 配对成功**。
- **P5 E2E 会话**：握手、控制流往返、重连退避 + 重握手 + 文件层仅认 ack 位图 → 双实例加密帧往返验证。
- **P6 文件流**：scope 浏览、分块/ack、暂停恢复、B3、.part/原子改名、冲突/盘满 → 单机全部用例。
- **P7 桌面代理**：捕获→NV12→软件 H.264→管道；注入；锁屏检测 → 本地回环（测试图源→编码→解码→像素校验）。
- **P8 WPF 客户端**：四视图 + TOFU + 全屏 Raw Input → **单机全链路冒烟：本地 relay+registry，agent 捕本机桌面，WPF 自控自**（`e2e-smoke.ps1`）。
- **P9 打磨**：硬件 MFT 路径 + 自适应码率（先码率帧率、后分辨率）+ 多显示器/DPI/旋转（ACCESS_LOST 重建管线）。
- **P10 部署**：install/uninstall.ps1（服务注册 + UAC 策略切换/恢复 + 修复入口）、VPS systemd/证书脚本。

## 关键风险与对策（评审确认）

- **UIPI**：代理用 `WTSQueryUserToken`→`GetTokenInformation(TokenLinkedToken)` 提升令牌启动，否则点不动高完整性窗口（含 UAC 弹窗）。
- **硬件 MFT interop 是最痛点**：v1 软件同步 MFT 保底可靠，硬件路径独立开关 + 自动回落。
- **DPAPI 作用域**：统一 LocalMachine + 文件 ACL 只留 SYSTEM/Admins（console dev 模式与 SYSTEM 模式一致）；不做跨重装恢复（自用可接受）。
- **管道只走已编码码流**（几 Mbps），NV12 90MB/s 绝不过管道；输入线程与渲染线程分离。
- **DXGI 细节**：指针不在帧内（独立控制帧客户端合成）；模式切换/DPI 变化 → `ACCESS_LOST` 整管线重建；SPS/PPS 会话开始先发。
- **System.Net.Quic**：必填 `DefaultStreamErrorCode/DefaultCloseErrorCode`，显式调大 `MaxInboundBidirectionalStreams`；TOFU pin 在 `RemoteCertificateValidationCallback`。

## 验证

- 单测：`dotnet test`（协议/BLAKE3/文件引擎）+ `go test ./...`（registry/relay）。
- 单机冒烟 `tests/e2e-smoke.ps1`：本地 relay+registry → 两个 console 实例配对 → 会话加密帧往返 → 文件断点续传/B3 → agent 真捕获 → WPF 全链路自控（开发用 `--no-inject` 防自控输入环路）。
- 需双机/VPS 才能验证（交付清单形式输出）：真实 NAT、公网 TOFU、Win10 TCP 回退端到端、撤销即时踢线、1080p30 真实带宽。
