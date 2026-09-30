# DeskLink

自用 Windows 10/11 远程桌面软件。完整设计规格见 [DESIGN.md](./DESIGN.md)。

## 当前阶段

| 里程碑 | 状态 | 负责人 |
|---|---|---|
| P0 环境验证与仓库骨架 | 已交付 | integration-tester |
| **P1 协议库 + Go 协议常量对齐** | **已交付** | **protocol-engineer** |
| P2 registryd | 已交付 | vps-backend-engineer |
| **P3 relayd** | **已交付（双传输 E2E + relay 单测全绿）** | **vps-backend-engineer** |
| **P4 Service 骨架** | **已交付（可编译可运行 + 单机双实例经本地 relay 接线成功）** | **win-service-engineer** |
| **P5 中继 E2E 会话** | **已交付**（SIGMA + 加密往返 + 真实 RTT + 退避重连 + 中继 TOFU 证书固定） | win-service-engineer |
| **P5.5 局域网直连** | **已交付**（DirectServer/DirectClient + 撤销踢线 + 冒烟实跑） | win-service-engineer |
| **P5.6 防火墙策略** | **已交付**（真实 netsh advfirewall 增删/幂等/端口变更 + 修复入口） | win-service-engineer |
| **P6 文件流** | **已交付**（scope/分块/ack 续传/暂停取消/BLAKE3/.part 原子改名/三选项冲突/盘满） | win-service-engineer |
| **P7 桌面代理** | **已交付**（DXGI 抓帧 + NV12 + MF H.264 编解码 + SendInput + 锁屏检测 + 管道） | win-client-engineer |
| **P8 WPF 客户端** | **已交付**（四视图 + TOFU 指纹确认 + 直连对话框 + Raw Input + H264 解码 + 媒体通道客户端） | win-client-engineer |
| **P9 打磨** | **已交付**（自适应码率控制器 + 多显示器旋转/DPI + 硬件 MFT 探测） | win-client-engineer |
| **P9.5 README + KnownIssues** | **已交付**（[KnownIssues.md](./KnownIssues.md)） | win-client-engineer |
| **P10 VPS 部署** | **已交付**（`installer/vps/`：deploy.sh / gen-cert.sh / systemd 单元） | vps-backend-engineer |
| **P10 Windows 安装/卸载** | **已交付**（`install.ps1` / `uninstall.ps1`，含 UAC 策略恢复与防火墙清理） | win-service-engineer |

> **P5 的两个验收子项现已完成**：
> 1. **中继 TOFU**：`RelayTrustPolicy` + `RelayPinStore` 已接入 `RelayClient`
>    （首次记录证书指纹并信任，之后必须一致；`--insecure-relay-tls` 才显式放行）。
> 2. **文件层仅认 ack 位图**：已由 P6 的 `FileTransferEngine` 完整实现
>    （断点续传只以 `FileAck` 位图为凭据）。
>
> **P6 文件传输的关键约定**：
> - 授权目录必须显式给出（`--file-scope <dir>`，可重复）；**不指定 = 一律拒绝**，
>   没有默认根目录（DESIGN：不提供默认全盘浏览）。
> - `FileOpen` 是**两阶段开启**：接收端先回 `FileStatus(Ok,"accepted")` 或拒绝
>   （scope 越界 / 冲突跳过 / 磁盘满），发送端收到接受后才开始送块。
> - 冲突策略（覆盖/重命名/跳过）由**发起端**在 `FileOpen` 里携带，接收端据此决定落盘目标。
> - 校验通过才 `File.Move(overwrite)` 原子改名；校验失败/磁盘满时**保留 `.part`**、
>   **绝不产生半截目标文件**。
>
> **媒体通道已全线打通**（P8/P9 收尾）：Service 起两条本地命名管道
> （`{prefix}.Media.{instance}` 对客户端、`{prefix}.AgentMedia.{instance}` 对代理）
> 做转发泵；桌面代理有**常驻运行循环**（抓屏→旋转→NV12→H.264→发送；收输入→注入）。
> 冒烟 `-Mode media` 用真 Service + 真代理 + 真客户端管道验证了"配置帧 + 可解码码流"到达客户端。
>
> **仍未在真机验证的项**（诚实清单）：真实 SendInput 与 UIPI 拒绝路径、硬件 MFT 选择
> （本机只有软编）、GPU VideoProcessor 转换、`DXGI_ERROR_ACCESS_LOST` 真实触发、
> 锁屏/解锁切换、Raw Input 的 E0 扩展键、两端不同 DPI 下的坐标精度、真实 netsh 写路径，
> 以及"用真实桌面（非测试图源）跑完整远程画面"。完整清单与逐项验证方法见
> [KnownIssues.md](./KnownIssues.md)。

### P3–P5 验收方式

```bash
# Go 侧（P3：relayd 双传输 / 接线 / 踢线 / 限速 / 宽限窗）
cd src/vps && go test ./... -count=1

# C# 侧（P1/P4/P5：协议对齐 + 单机双实例加密帧往返）
dotnet test DeskLink.sln

# 端到端：relay(TCP+QUIC, 含 P6 文件传输) + 局域网直连 + 防火墙 + 媒体通道 + 安装脚本自检
pwsh -NoProfile -File tests/e2e-smoke.ps1 -Mode all -Transport both
```

当前测试规模（`dotnet test DeskLink.sln`，全部真实断言、无 `Assert.True(true)` 占位）：
Protocol 71 / Service 200 / Client 137 / DirectHandshake 41 / Agent 137 / Panel 112 = **698 通过**。

> 2026-09-29 修复轮新增 46 个用例（见 [KnownIssues.md](./KnownIssues.md) 1.7 节：
> 直连 UI 假接线 / 无条件"已连接" / 中继地址"已保存"但不生效）。
>
> 同日新增 `DeskLink.Panel`（本机服务图形控制面板）与 95 个配套用例。
>
> 再修一处 WPF 绑定回归：`SettingsView` 把 `ActiveRelayUrl`（private setter）绑到
> `Run.Text`（默认 TwoWay）导致**客户端每次启动即崩**。`WpfSmokeTests` 现在会真正
> `Show()` 并渲染窗口 + 收集数据绑定错误，这类 bug 以后会被测试拦下。
>
> 2026-09-30 面板改成双角色选项卡后，给 `Panel` 也补了同样的渲染冒烟 + 静态护栏。
> 其中一条结论值得记住：**绑到一个不存在的属性，WPF 在 Release 下静默失败，
> `PresentationTraceSources.DataBindingSource` 一个字都不吐**（实测）。
> 所以属性名对不对只能静态扫 XAML 校验，光挂 trace 监听器是抓不到的。
>
> 同日三个次要缺陷的修复（详见 [KnownIssues.md](./KnownIssues.md) 1.8 节）：
> 文件传输不再是假进度条（新增 `file_progress` 只读 RPC，客户端轮询真实分块字节，
> **拿不到就明说拿不到**）；面板补上多显示器入口（`--monitor`）；`--file-scope`
> 与显示器索引落盘到 `<data-dir>\service.json` 跨重启保留，命令行仍永远优先。
> 审文案时还挖出一个流程断点：主控端页让用户"到被控端页粘对方的公钥"，
> 而被控端页当时**根本没有那个输入框**、一键准备也从不配对——已补上。
>
> 另修一处测试基建抖动：`DirectHandshakeFixture` 原来用
> `47000 + Random.Next(1000)` 取端口，只有 1000 个槽位却要喂 40+ 个并行用例，
> 撞端口是必然（实测约 1/4 概率挂在「每个地址或端口只能使用一次」）。改成向内核
> 要临时端口后连跑 8 次全过。

### 图形化：DeskLink 控制面板

命令行只适合排障。日常使用请走 **`DeskLink.Panel.exe`**（控制端与被控端通用）：

| 面板做什么 | 等价的命令行 |
|---|---|
| 读本机公钥并复制 | `DeskLink.Service.exe --data-dir <dir> --print-config` |
| 与对方配对 | `DeskLink.Service.exe --data-dir <dir> --pair-peer-pub <对方公钥>` |
| 放行/关闭入站端口（自动弹 UAC 提权） | `DeskLink.Service.exe --firewall-set 47200 on\|off` |
| 启停本机服务 | `DeskLink.Service.exe --console --data-dir <dir> --enable-direct --inject-agent` |
| 查看会话数 / 中继 / 端到端状态 | `get_status` RPC |

面板是**两个角色选项卡**布局（不是"点一下切换"的单页）：

| 选项卡 | 这一页上有什么 |
|---|---|
| **我是主控端**（左边那一页） | ① 一键准备主控端 ② 把本机公钥交给被控端 ③ 粘贴被控端公钥并配对 ④ **打开控制界面**（WPF 客户端） |
| **我是被控端** | ① 一键准备被控端 ② 把本机公钥与 `IP:端口` 交给主控端 ③ 放行入站端口 |
| 选项卡之外（共用） | 本机服务启停与状态、高级设置、服务日志 |

因此**控制端的所有功能都在「我是主控端」这一页下**，被控端的专属项（放行入站端口）只在
「我是被控端」页，两边不会串味。切选项卡只是换视图，不会顺手改掉
`--enable-direct` / `--inject-agent` ——那属于「一键准备」的动作。

**主控端**：打开面板 → 「我是主控端」页 → 一键准备 → 粘贴被控端公钥配对 → 点「打开控制界面」，
在设备页选「局域网直连」并填被控端 `IP:端口`。
**被控端**：打开面板 → 「我是被控端」页 → 一键准备（自动放行防火墙）→ 把**公钥**和**本机地址**发给主控端。

面板与 WPF 客户端职责不重叠：**面板管本机服务，客户端管远程操控**。

⚠ 面板显示的防火墙状态是**唯一权威**的"能不能被连上"判据。不要把
`get_status` 的 `direct_enabled` 当成"直连已开启"——它取自防火墙探测而非监听状态，
语义警告写在 `PipeContract.StatusResult.DirectEnabled` 的注释里。

### 直连与真机验收（命令行排障路径）

```powershell
# 单机双实例直连演练（不需要第二台机器；不启动桌面代理，不会移动鼠标）
pwsh -NoProfile -File tests/manual-lan-demo.ps1

# 直连环境自检 / 会话状态 / 真实拨号 / 实时观察
pwsh -NoProfile -File tests/verify-lan.ps1 -Mode preflight
pwsh -NoProfile -File tests/verify-lan.ps1 -Mode status
pwsh -NoProfile -File tests/verify-lan.ps1 -Mode dial -PeerPub <对端公钥> -Target <IP>:47200
pwsh -NoProfile -File tests/verify-lan.ps1 -Mode watch
```

⚠ **直连必须双向配对**：中继由 registry 帮两端互存公钥，直连没有这个中介，
缺任何一边都会在 SIGMA 之前被对端直接断开（报 `transport: ...软件中止了一个已建立的连接`，
很容易误判成防火墙问题）。两端都要 `--pair-peer-pub <对方公钥>`。

> 直连拨号现在由 UI 真正发起（`direct_dial` RPC → `DirectDialer`）。
> 在此之前客户端只校验 IP:端口就显示"已连接"，而没有任何东西去拨号——
> 详见 KnownIssues 1.7 节①。

`e2e-smoke.ps1 -Mode relay` 会自行构建并拉起 registryd / relayd，注册两台设备、
建立配对，然后以两个独立 `--data-dir` 启动 Service 实例，断言：

- 双方日志出现 `e2e established` 与 `control round-trip OK`（P5 验收标志）；
- 实际使用的传输与 `-Transport` 一致（防止"以为跑了 QUIC，其实回落了 TCP"）；
- 出现真实 RTT（`ping rtt=<n>ms`，不是占位值）。

其余模式：`-Mode firewall` 用独立预言机（直接读注册表）对账 `FirewallHelper.QueryEnabled`
的语义；`-Mode direct` 因 P5.5 未实现而**显式失败**（不会打印占位清单假装通过）。

`relayUrl` 的 scheme 决定传输：`quic://` 强制 QUIC（不可用时报错，不静默回落）、
`tls://` 强制 TCP/TLS、`https://` 优先 QUIC 并允许回落。

运维自检：`DeskLink.Service.exe --console --data-dir <dir> --firewall-status`
打印防火墙/直连状态 JSON（只读，不需提权）。

## 仓库结构

```
desklink/
├─ DESIGN.md                              # 设计规格（首要事实来源）
├─ README.md                              # 本文件
├─ DeskLink.sln                           # 解决方案
├─ src/
│  ├─ Protocol/DeskLink.Protocol/         # 纯 C# 协议库（P1）
│  │  ├─ Relay/                           # 中继明文控制层（P3/P4：Hello/Dial/挑战签名）
│  │  ├─ Session/                         # E2E 加密会话（P5：AEAD 分帧）
│  │  └─ Handshake/                       # SIGMA 握手（P1/P5）
│  ├─ vps/
│  │  ├─ internal/proto/                  # Go 协议常量包（P1）
│  │  ├─ internal/regstore/ regapi/ regnotify/   # registryd（P2）
│  │  ├─ internal/relay/                  # relayd：接线表 + 密文泵 + 踢线（P3）
│  │  └─ cmd/registryd/ cmd/relayd/       # VPS 两个进程入口
│  ├─ Service/DeskLink.Service/           # Windows 服务 + console 模式（P4/P5）
│  │  ├─ Relay/                           # QUIC / TCP-TLS 传输 + 退避重连
│  │  └─ Session/                         # 控制握手 → SIGMA → 加密收发泵
│  ├─ Agent/DeskLink.DesktopAgent/        # （P7+）
│  └─ Client/DeskLink.Client/             # （P8+）
└─ tests/
   ├─ Protocol.Tests/                     # xunit 协议库单测（含 C#↔Go 黄金向量对齐）
   ├─ Service.Tests/                      # xunit Service 单测 + P5 双实例 E2E
   └─ e2e-smoke.ps1                       # 端到端冒烟（真 registryd + relayd）
```

## 开发环境

- Windows 10/11
- .NET 9 SDK（`dotnet --version` ≥ 9.0.300）
- Go 1.22+（VPS 端开发用）
- PowerShell 7（部署/测试脚本）

## 构建与测试

```powershell
# 编译所有 C# 项目
dotnet build DeskLink.sln

# 跑协议库单测（必须全绿）
dotnet test tests/Protocol.Tests/Protocol.Tests.csproj

# VPS 端 Go 测试（待 Go 工具链到位）
cd src/vps
go test ./internal/proto/...
```

## 安装与卸载

```powershell
# 预演（不需管理员，只打印计划）
pwsh -NoProfile -File installer\install.ps1 -DryRun

# 正式安装（管理员）：注册服务 + 放行局域网直连端口 + 可选切换 UAC 到交互桌面
pwsh -NoProfile -File installer\install.ps1 -EnableDirect

# 卸载（管理员）：删服务 + 回收防火墙 + 恢复 UAC 策略；数据目录默认保留
pwsh -NoProfile -File installer\uninstall.ps1
pwsh -NoProfile -File installer\uninstall.ps1 -RemoveData   # 连设备私钥一起删
```

- 防火墙逻辑由 `DeskLink.Service.exe --firewall-set <port> on|off` 承担（复用 13 个单测覆盖的
  `FirewallHelper`），脚本不自己拼 `netsh`，避免出现第二份真相。
- UAC 策略（`PromptOnSecureDesktop`）安装时备份到 `HKLM\SOFTWARE\DeskLink\UacBackup`，
  卸载时按备份恢复；备份不存在时**删除**该项而不是写一个猜测值。
- VPS 部署见 [`installer/vps/README.md`](./installer/vps/README.md)。

## 变更说明

- P1 协议库变更说明见 `.team/kickoff-build/protocol-engineer/p1-protocol-library-notes.md`
- VPS 协议设计草案见 `.team/kickoff-build/vps-prep/01-proto-spec.md`

## 协议字节序（重要约定）

**多字节整数一律 big-endian**（见 `src/vps/internal/proto/proto.go` 的"编码总原则"）。
`FrameCodec`（u16 payloadLen）与 `MuxFrame`（u32 len）早先误用 little-endian，
已于 2026-09-26 更正；`HandshakeMessages` 一直使用 big-endian。

跨语言对齐的唯一权威锚点：

- C#：`tests/Protocol.Tests/E2EAlignmentTests.cs`（`*_Matches_Go_*` 用例）
- Go：`src/vps/internal/proto/crosslang_golden_test.go`

两种语言必须产出完全相同的字节串；任一端改编码都会立刻使上述用例变红。

**本地媒体通道**：远端画面不能走 RPC 管道（那条按设计只走元数据、帧上限 64KB）。
Service 另开两条本地管道并做转发泵，线上格式 `[u32 len BE][u8 frameType][payload]`：
- 画面下行（代理→Service→客户端）队列满时**丢最旧的**（画面只要最新的）；
- 输入上行（客户端→Service→代理）队列满时**优先丢最旧的鼠标移动**，
  绝不丢按键（丢掉"抬起"会让远端按键永久卡住）；
- 转发泵把丢帧数/队列深度/输入丢弃数经 `MediaFlow` 帧回传代理，
  代理据此降档 —— 这是自适应码率的闭环（代理自己看不到链路状况）。

**命名管道 RPC 也走 big-endian**：`PipeContract.cs` 声明 `[u32 length BE][utf-8 JSON]`，
`PipeServer` / `PipeChannel` / 两侧测试都按 BE 实现（曾经是 little-endian 且与注释不符，
已于 2026-09-27 统一）。改动字节序时务必全局搜索「手写字节的测试」——
`PipeServerTests.Oversized_Frame_Disconnects` 就曾因为手写 LE 字节串在改 BE 后
变成一个**合法**长度，表现为测试宿主「挂死后被判崩溃」。
