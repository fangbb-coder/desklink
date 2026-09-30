# DeskLink 已知问题与限制（KnownIssues）

> 本文是**诚实清单**：明确区分「已验证」「未验证」「已知不支持」。
> 未验证项不代表功能不存在，而是**没有在本机/本环境跑过**，需要真机人工确认。
> 最后更新：2026-09-27。

---

## 1. 功能边界（设计上不支持，不是缺陷）

| 项 | 说明 |
|---|---|
| 音频 | 第一版不提供。 |
| 剪贴板同步 | 不提供。 |
| 聊天 / 终端 | 不提供。 |
| Windows 登录界面 / 锁屏界面 / Secure Desktop 的捕获与交互 | **不提供**。锁屏时被控端显示"等待本地登录"，不尝试控制登录界面。 |
| 多人组织管理 / 账号体系 / 共享链接 / 设备分组 | 不提供。 |
| 公网端对端直连（NAT 打洞 / 公共中继） | 不提供；**局域网 IP 直连是唯一支持的直连形式**。 |
| mDNS / DNS-SD 自动发现 | 不提供；局域网直连需手动输入 `IP:端口`。 |
| 中继 ↔ 直连的自动回退 | 不提供；路径由用户在 UI 显式选择，失败需手动切换。 |
| 多显示器并行画面 | 第一版只支持**选择单个显示器**；切换显示器不创建第二路并行画面。 |

---

## 1.5 本轮修掉的生产级 Bug（值得记住）

### AgentLauncher 未传 `--run`
**症状**：客户端点"开始控制"后远程页一直没画面；代理进程启动后 1-2 秒就消失。  
**根因**：`AgentLauncher.Start` 拼装参数时漏了 `--run`；代理默认是"一次性自检"模式，跑完立刻退出。冒烟脚本手工带 `--run` 启动代理，掩盖了这个 bug。  
**修复**：`BuildArguments` 强制在第一个位置加 `--run`；新增单测 `BuildArguments_Always_Includes_Run` 钉死这条不变式。

### `ArgumentList` 把"开关+取值"拼成单个字符串
**症状**：代理启动即报"未知参数"，退出码 1。  
**根因**：`$"--pipe {name}"` 传给 `ArgumentList.Add` → 代理 argv 里是一个 `"--pipe DeskLink.Agent.default"` 而不是两个元素，参数解析器匹配不到 `"--pipe"`。  
**修复**：每个开关和取值必须是**两个独立元素**；新增单测 `BuildArguments_Never_Combines_Flag_And_Value` 钉死不变式（`Assert.All(args, a => Assert.DoesNotContain(' ', a))`）。

### 代理 exe 路径只认同目录（开发布局找不到）
**症状**：开发/冒烟时 Service 启动代理走 stub 模式，日志里写 "DeskLink.DesktopAgent.exe not found"。  
**根因**：`ResolveAgentExePath` 只查 `AppContext.BaseDirectory`（Service 的 bin 目录），开发时代理输出在另一个项目目录。  
**修复**：安装路径优先，找不到时按仓库结构回溯 6 级到根目录，再探测 `src/Agent/DeskLink.DesktopAgent/bin/Debug/net9.0-windows/`。

### 安装脚本只复制 `DeskLink.DesktopAgent.*`（丢了 Vortice DLL）
**症状**：安装后代理启动即 `FileNotFoundException`（缺 Vortice.DXGI 等）。  
**根因**：`install.ps1` 用 `-Path 'DeskLink.DesktopAgent.*'` 匹配，只复制前缀一致的文件；Vortice 系列 DLL 名字与代理无关。  
**修复**：`Copy-Item -Path (Join-Path $AgentSource '*') -Recurse -Force` 复制整个输出目录。

### 管道 ACL 未授予交互用户（服务模式下连不上）
**症状**：装成 Windows 服务后，同机非提权用户连不上命名管道，表现为超时。  
**根因**：ACL 只给 SYSTEM/Administrators/CreatorOwner；Administrators 在过滤令牌里是 deny-only，普通交互用户不在集合里。  
**修复**：`PipeAcl.Build` 显式授予当前交互会话用户（`WTSGetActiveConsoleSessionId` → 进程令牌），取不到时回退到 Authenticated Users 并记警告。

## 1.6 2026-09-27 全量审计修复轮（C# + Go VPS）

> 三路并行审查（Agent+Client / Protocol+Service / Go VPS）后的集中修复。
> 修复后 `dotnet test` 499 全绿（此前 Service 的 E2E 传输测试在并行负载下间歇失败），
> `go vet` / `go test ./...` 全绿。审查中**决定不修、仅记录**的项见第 5 节。

### 需求补全

- **断开控制链路（end_session）**：DESIGN 要求"结束控制"是显式操作，但管道契约与 UI 均缺失。
  补齐：`PipeContract.EndSessionResult` → `PipeServer` end_session RPC → `ServiceCore` 结束会话 →
  `MainViewModel.EndControlCommand` → StatusBar 断开按钮；状态条 3s 轮询刷新受控状态。
- **客户端输入转发挂接**：`MediaChannelClient` 的 `SendMouseMove/SendMouseButton/SendWheel/SendKey`
  此前**没有任何调用方**（RawInputHook 类已实现但未挂进 UI）——远程页发不出任何输入。
  现在 `ToggleFullscreen` 进全屏时 Attach、退出/断开时 Detach（见第 6 节"输入端到端"注意事项）。

### C#（端到端 / 协议 / 代理）

- **直连会话缺身份绑定校验**：握手完成后未比对"会话声明的身份"与"握手公钥推导的
  device_id"。修复：`HandshakeSession` 暴露握手公钥，`E2ESessionHost` 校验
  `PeerIdentityMatches`，不一致即断开。
- **FileTransferEngine 丢失唤醒（lost wakeup）**：对端状态帧可能在本端 waiter 注册前
  到达而被丢弃，发送端白等超时——全解决方案并行跑测试时 `E2E_Download_PullWorks`
  间歇卡 60s 失败。修复：accept/final 等待者一律在对应帧**发出之前**注册；
  Paused/Resumed 不再喂给 accept/final 等待者；`NextTransferId` 同时查 `_statusWaiters` 防撞号。
- **`FileAckFrame` 块序号比较未防回绕**：uint 序号相减可能溢出成大数，误判补传范围。
- **`AgentRuntime` 鼠标注入未反归一化**：注入端拿到千分比坐标直接当像素用，落点错位。
  修复：注入前按远端分辨率反归一化。
- **H.264 编解码两端色彩系数不一致**：编码端 BT.709、解码端 BT.601，画面偏色。
  统一为 BT.709（Kr=0.2126），解码缓冲不足一并扩容。
- **`H264Encoder` 码率参数未真正下发 + COM 对象泄漏**：自适应码率调不动；MFT 实例
  与相关 COM 引用未释放。
- **`AdaptiveBitrateController` 缺锁**：`_gate` 字段并发读写。
- **`MediaChannelClient` / `PipeRpcClient` OCE 路径句柄泄漏**：抛
  `OperationCanceledException` 时 `CancellationTokenSource` 未释放。
- **`AgentLauncher` 未持有 `Process` 引用**：无法跟踪/停止子进程。
- **`RawInputHook` 结构布局修正**：RAWMOUSE/RAWKEYBOARD 与 winuser.h 对齐
  （此前字段错位，scancode 与按钮标志解析全错）。
- **`RemoteViewModel` 状态机非法迁移**：错误路径下可跳回不可达状态。

### Go VPS（registryd / relayd）

- **relay 挑战无重放防护**：`VerifyDeviceChallenge` 只验签名 + ±120s 时间容差，同一
  nonce 可重放。新增 `ChallengeReplayGuard`（键 = challengeID‖timestamp‖nonce‖deviceID，
  TTL 240s = 2×容差），已在 `readHello` 接线。
- **重复配对返回 500 而非 409**：`pairs(device_a, device_b) WHERE revoked=0` 部分唯一
  索引命中 `SQLITE_CONSTRAINT_UNIQUE` 被当内部错误。现映射 `ErrPairAlreadyExists`
  → HTTP 409；事务回滚使配对码保持未消费，撤销旧配对后同码可重领。
- **`ListActivePairingCodes` Scan 崩溃**：直接 `Scan(&r.DeviceID)`（*[32]byte）不被
  database/sql 支持，整个 `VerifyAndRedeemPairingCode` 从未可用。改经 `[]byte` 中转
  + 长度校验 + copy。
- **过期配对码清理未接线**：`CleanupExpiredPairingCodes` 写好后无人调用。registryd
  启动清一次 + 每小时 ticker。
- **`IPLimiter` challenge 表只增不删**：加惰性 sweep（每分钟清理超 1 分钟的条目）。

### 1.7 2026-09-29 修复轮：三个"UI 撒谎"缺陷

> 起因是一次交付审查。三个缺陷的共同形态是：**界面告诉用户某件事发生了，
> 而底层根本没发生**。单测能钉死它们，真机看不出来。

#### ① 局域网直连的 UI 是"假接线"（最严重）

**症状**：客户端设备页选"局域网直连"、填 IP:端口、点"连接" → 立刻跳远程页，
然后永远黑屏。

**根因**：`DirectConnectDialog` 在整个客户端**从未被 new 出来过**（全仓只有它自己的
定义和注释）；`MainWindow.OnLanConnectApproved` 收到的 `endpoint` 只被拼进了状态栏
字符串；`IServiceApi` 根本没有任何 dial/connect 方法。真正能建立直连会话的入口
只有 `--direct-probe` 这个**一次性** CLI——它拨完就退出。

也就是说 P5.5「局域网直连已交付」只在引擎层成立，**从 UI 完全不可用**。

**修复**：
- 新增 `DirectDialer`（`src/Service/DeskLink.Service/Direct/DirectDialer.cs`）——
  控制端角色的出站拨号器。维护出站会话表、同对端顶替、撤销关闭、与
  `DirectServer` 共用同一套业务处理器装配（避免两条路径行为漂移）。
  拨号**返回即代表会话已建立**（SIGMA 完成、处理器挂好、泵已启动）。
- 新增 `direct_dial` RPC：`PipeContract.DirectDialParams/DirectDialResultDto`
  → `IServiceCore.DialDirectAsync` → `PipeServer` 派发 → `IServiceApi.DialDirectAsync`。
- `DevicesViewModel.ConnectLan` 改为 `ConnectLanAsync`：先真拨号，**拨通才**通知 UI
  跳远程页；失败留在设备页并显示服务端给的可读原因。
- `DirectConnectDialog` 接进 `DevicesView`（"输入地址…"按钮），确认后回填地址框。
- `ServiceCore` 的撤销 / `end_session` / 取文件引擎现在同时覆盖入站与出站会话
  （`AllDirectSessions()`）。

**验证**：`tests/DirectHandshake.Tests/DirectDialerTests.cs`（15 个用例）真起
`DirectServer` + `DirectDialer`，断言**服务端真的观察到会话**、出站会话进活跃表、
重复拨号顶替、撤销只关目标、未配对本地快速拒绝、无人监听时快速失败不挂起。
另有 `tests/manual-lan-demo.ps1` 单机双实例演练（EXIT=0）。

#### ② 点"连接"无条件显示"已连接"

**根因**：`OpenMediaAsync` 只检查**本机**媒体管道连没连上，连上就 `MarkEstablished()`。
媒体通道是本机 Service↔客户端的 IPC，**连得上完全不能证明对端在线**。选中一台离线
设备点连接，UI 立刻显示"已连接（192.168.x.x:47200）"然后黑屏。中继路径因为 Service
启动时会自动 `--peer` 拨号，恰好能对上，掩盖了这个设计问题。

**修复**：`MainViewModel.WaitForRemoteSessionAsync` 轮询 `get_status`，以 Service 侧
真实会话状态为准——直连看 `DirectActiveSessions > 0`（含入站+出站），中继看
`E2EState == "established"`。超时 15s 返回 false，`MainWindow` 如实报错并**留在
设备页**。`MarkEstablished()` 现在只在这道门禁通过后调用。

#### ③ Settings 改中继地址"已保存"但不生效

**根因**：`RelayClient` 在 `Program.BuildHost` 阶段按 `options.RelayUrl` 一次性构造；
`ServiceCore.SetConfig` 只改 `_options.RelayUrl`，不重建连接。UI 弹"已保存"、
返回 `ok=true`，且不提示。这是新用户必踩的坑。

**修复**（选了"如实提示"而不是"热重建连接"）：
- `SetConfigResult` 增加 `RequiresRestart` + `RestartHint`。
- `GetConfigResult` 增加 `ActiveRelayUrl`——**进程真正在用的**地址（启动快照），
  与"已保存但可能未生效"的 `RelayUrl` 分开暴露。
- `SettingsViewModel` 保存后据此提示；加载时若两者不一致则高亮"当前生效：…"。
- `direct_port` 的改动是**立即生效**的（`FirewallHelper.ApplyPortChange` 真写规则），
  不置该标志。

**为什么不热重建**：换掉 RelayClient 会牵动到 relay 的传输、TOFU 信任策略、退避循环
以及进行中的 E2E 会话与媒体通道，失败面远大于收益。DESIGN 也允许路径由用户显式选择。

#### 本轮新增的运维陷阱

**直连必须双向配对。** 中继路径由 registry 帮两端互存公钥，直连没有这个中介：
`DirectServer` 在 SIGMA 之前就查**本地**配对列表，缺任何一边都会在握手前被对端
直接断开，表现为 `transport: 你的主机中的软件中止了一个已建立的连接`——很容易被
误判成防火墙问题。两端都要执行 `--pair-peer-pub <对方公钥>`。

**RPC 管道名是 `DeskLink.Client.{instance}`**，同实例还有 `DeskLink.Agent.*` /
`DeskLink.Media.*` / `DeskLink.AgentMedia.*`（后两者是给桌面代理与媒体通道的）。
写脚本连本机 Service 时容易只写 `DeskLink.{instance}`，表现为连接超时。

### 1.8 2026-09-30 修复轮：三个次要缺陷 + 一次文案审查挖出的流程断点

> 前一轮修的是"UI 撒谎"（1.7），这一轮修的是**更朴素的三件事**，外加一次文案审查
> 意外挖出的功能断点。共同的教训是：**界面上的每句说明都应当能被代码兑现。**

#### ① 文件传输是假进度条

**症状**：传输列表里的进度条从头到尾停在 0%，等大调用返回后"啪"一下跳到 100%。

**根因**：`FilesViewModel` 把 `Progress` 初始化成 0，只在**长调用返回时**置 100。
它从不查询真实分块进度。

**值得记的一件事**：Service 侧的 `FileTransferEngine` **早就有** `FileTransferProgress` /
`OnProgress` / `Snapshot()`，只是**从来没经 RPC 暴露过**。所以这不是"缺一个进度系统"，
而是"已有的东西没接出来"。找到这一点之后修法就只是加一个只读查询。

**修复**：
- `PipeContract` 增加 `FileProgressEntryDto` / `FileProgressResult`。
- `IServiceCore.GetFileProgress()` → `PipeServer` 派发 `file_progress` →
  `IServiceApi.GetFileProgressAsync`。
- `FilesViewModel` 在等长调用**期间并行轮询**（250ms），按「方向 + 归一化路径」
  匹配（上传=sending、下载=receiving），传输结束立刻取消，不留孤儿轮询。
- `FilesView` 进度条右侧补上真实数字（`12.1 MB / 48.0 MB（25.2%）`）。

**核心约定：拿不到就明说拿不到。** 轮询失败、Service 太老、没有会话——
这些情况下 `ProgressKnown=false`，进度条走**不确定态**，文字显示"未获取到分块进度"，
**绝不停在 0% 装作在传**。这正是原缺陷被投诉的地方。测试里
`Service报不出来进度_转不确定态而不是假装0` 锁死这条。

> 写测试时发现的一个真实缺陷：传输成功后 `ProgressKnown` 仍是 false，
> 于是"已完成"的条目挂着"未获取到分块进度"。传完了就是全部传完，这是确知的真值。
> 已加 `item.ProgressKnown = true`。

#### ② 多显示器没有任何 UI 入口

**症状**：`--monitor <n>` 这个参数一直存在于 `DeskLink.DesktopAgent`，但**面板拼不出它**，
多屏用户只能去命令行手改。

**修复**：`CommandLineParser` 增 `--monitor`（校验 `>= 0`，进 `--help`）；
`PanelSettings.MonitorIndex` 落盘；`MainViewModel.MonitorIndex` + `ServiceCli.BuildRunArgs`
在 `> 0` 时透传。`0` 不传（= 主显示器 = Service 默认行为）。
`--monitor` 需要重启 Service 才生效，界面上直接写明了这一点。

#### ③ `--file-scope` 不持久化

**症状**：直接跑 `DeskLink.Service.exe --file-scope D:\share` 的人，下次不带这个参数
重启，授权目录就静默变成"一律拒绝"——文件功能无声失效，只看得到"传输被拒绝"。

**修复**：
- 新增 `ServiceConfig`，落盘到 `<data-dir>\service.json`（snake_case）。
- `CommandLineParser` 改**两遍扫描**：先廉价取出 `--data-dir`，读出本文件，再做完整解析。
  这解决的是"配置在 data-dir 里、而 data-dir 本身由命令行决定"的鸡生蛋问题。
- **命令行永远优先于落盘值**——落盘只是默认值。
- 坏掉的 `service.json` 返回 null 而不是抛异常：**绝不让一个坏配置阻止 Service 启动**。
- `SetConfig` 也能就地改这两项（`file_scope_roots` / `capture_monitor_index`）并落盘，
  结果字段 `persisted` 如实回报"到底存没存住"。
- 顺手让 `get_config` 也返回这两项，客户端不再只能看不能改。

> **为什么不把中继地址/直连端口也塞进这个文件**：它们各有"立即生效 vs 需重启"的
> 语义，由 `ServiceCore.SetConfig` 就地处理（见 1.7 ③）。混进来会让
> "哪些是持久化配置"变得说不清。

#### ④ 文案审查挖出的流程断点（不属于三个缺陷，是审文案时发现的）

主控端页第 2 步写着：

> 对方拿到这串后，要在他那边的『我是被控端』页粘进『对方的公钥』输入框再点配对

而**被控端页当时根本没有这个输入框**——整页只有"一键准备"、复制公钥与地址、放行端口。
更糟的是 `PrepareControlledAsync` **从不配对**。

于是：直连握手是双向 SIGMA，两边都得认对方（见 1.7 的"运维陷阱"），
被控端不配 = 永远连不上，而用户完全按界面指引操作、无从发现问题。

**修复**：被控端页补上「对方的公钥（主控端发给你的）」输入框 + 配对按钮，
与主控端页**共用同一个 `PeerPub`**（在一页填了、切到另一页可见）。
`PrepareControlledAsync` 也改成有公钥就顺手配对；**配对排在提权之前**——
`PeerPub` 只在内存里不落盘，提权重启（`runas`）会把这个框清空。
收尾横幅里"还没配对"会被顶到最前面。

**同一轮修掉的其他文案问题**：
- 术语混用：主控端页第 4 步写"控制端才需要这一步"，全文其余都是"主控端"。
- **"QUIC 可用：…"是写死的字面量**，`MainViewModel.QuicAvailable` 明明存在却没绑——
  本机根本不支持 QUIC 时界面也在说可用。又一处"UI 撒谎"。改成 `QuicText`，
  两个分支都把话说满（不可用时明说只能走 TCP-TLS）。
- 提权提示两处说法矛盾（"弹出提权提示" vs "提权重启"），统一成实际行为：
  弹 UAC → 以管理员身份重新打开面板 → 自动放行。

#### 本轮顺带修的测试基建抖动

`DirectHandshakeFixture` 原来用 `47000 + Random.Shared.Next(1000)` 取端口。
**1000 个槽位要喂 40 多个用例**，而且 xUnit 会并行跑测试类，撞端口是必然事件——
实测 `Tcp_DeviceIdExchange_Succeeds` 约 **1/4** 的概率挂在
`SocketException: 每个地址或端口只能使用一次` 上。改成向内核要临时端口
（`TcpListener` bind port 0 再读回）后连跑 8 次全过。

> 教训：这种"换台机器就换一批用例中招"的失败，**不能归到"偶发"就放过**。
> 重复跑隔离用例是最快的定性手段——如果是真抖动，隔离跑会稳定复现。

## 2. P7 桌面代理 —— 未在本机验证的 6 项

以下能力**代码已实现且编译通过、部分逻辑有单测覆盖**，但没有在真实桌面场景下跑过。
需要人工在真机上逐项确认。

| # | 未验证项 | 为什么没验 | 怎么验 |
|---|---|---|---|
| 1 | **真实 `SendInput` 注入与 UIPI 拒绝路径** | 自动化测试会污染开发桌面（真的会移动鼠标/按键）；`--no-inject` 模式下只验证了"不调用 SendInput" | 被控端开一个高完整性（管理员）窗口，控制端点它；确认能点动。再用非提升令牌启动代理，确认返回"被控端需要重新登录以恢复控制"而不是静默失败 |
| 2 | **硬件 MFT 选择路径** | 本机只枚举到软件 MFT，回落路径已验证，硬件分支未走到 | 在有 Intel/NVIDIA/AMD 硬件编码器的机器上用测试图源起代理，确认日志 `BackendName` 前缀为 `hardware:`；再用 `--no-hardware-encoder` 对照确认变 software（步骤见 tests/MANUAL-VERIFICATION.md A3） |
| 3 | **GPU VideoProcessor 转换（BGRA→NV12）** | 走的是 CPU 路径并已验证；GPU 路径未执行 | 在支持 D3D11 VideoProcessor 的机器上强制启用 GPU 路径，比对输出 NV12 与 CPU 路径的一致性 |
| 4 | **`DXGI_ERROR_ACCESS_LOST` 真实触发** | 需要真的切换分辨率/旋转屏幕/重启显卡驱动；只单测了恢复逻辑 | 会话中改变被控端分辨率或旋转屏幕，确认状态条出现"正在恢复画面"且 1–3 秒内自动恢复、控制权不丢 |
| 5 | **锁屏 / 解锁切换** | 未在会话中真的锁屏 | 会话中锁屏被控端，确认显示"等待本地登录"；解锁后自动恢复画面 |
| 6 | **代理与运行中的 Service 的管道对接** | 测试时 Service 未常驻运行 | 起真实 Service（`--console --inject-agent`；`--no-inject` 是 AgentLauncher 传给代理的内部参数，不是 Service 启动参数），确认代理能连上 `DeskLink.Agent.{instance}` 并完成 `ping`（步骤见 tests/MANUAL-VERIFICATION.md A2） |

### P7 的互操作坑（已踩，写下来避免重踩）

1. `D3D11CreateDevice` 传**非空 adapter** 时 `DriverType` 必须是 `Unknown`；传 `Hardware` 会返回 `E_INVALIDARG`。
2. 不声明 DPI 感知时 `DesktopCoordinates` 会被系统虚拟化（例如报 1493×933）而复制返回物理尺寸（2240×1400）→ 已用 PerMonitorV2 `app.manifest` 修正。**改动清单文件会重新引入该问题。**
3. 编码器 MFT 必须**先设输出类型再设输入类型**，否则 `MF_E_TRANSFORM_TYPE_NOT_SET`。
4. MF 软件 H.264 编码器有前瞻缓冲：必须发 `MFT_MESSAGE_COMMAND_DRAIN` 才能拿到尾部访问单元。
5. `MFT_ENUM_FLAG_*` 是**并集**语义：先枚举 `SYNCMFT`，再对每个 MFT 测 `MF_ENUMERATE_HARDWARE_URL`。
6. Vortice 3.2.0 没有 `ICodecAPI` 绑定（本项目手写了 COM vtable）；`MapFlags` 没有 `None`（强转 0）。

---

## 3. P8 WPF 客户端 —— 未验证项

| # | 未验证项 | 为什么没验 | 怎么验 |
|---|---|---|---|
| 1 | **端到端远程画面** | 需要 Service 侧媒体通道 + 被控端代理同时在真实桌面上运行 | 真机起被控端（`--enable-direct` 或中继）与控制端，点"局域网直连/中继"进入远程页，确认有画面 |
| 2 | **Raw Input 真实 scancode 与扩展键（E0）** | 需要真实键盘交互，且会干扰开发桌面 | 全屏后按右 Alt/Ctrl、方向键、小键盘、F1–F12，确认远端行为正确 |
| 3 | **DPI 归一化坐标** | 需要两端不同 DPI 的显示器 | 客户端 150% 缩放 + 被控端 100%，确认鼠标落点准确 |
| 4 | **硬件解码器** | 本机解码走软解/回落 | 在有硬件解码的机器上确认 `H264Decoder` 选择硬件路径 |
| 5 | **中继证书 TOFU 首次确认** | 需要真实 relay + 自签证书首次接入 | 首次连接 relay 时确认弹出指纹确认；清 pin 后确认重新弹 |
| 6 | **文件传输进度粒度** | 契约层没有进度推送 RPC（`file_upload` 是阻塞式请求/响应） | 大文件传输时确认进度条按块推进；若过于粗糙，需要给契约加 `file_progress` 事件 |

### P8 的坑（已踩）

- WPF 项目在 `net9.0-windows` 下**隐式 using 不含 `System.IO`**，XAML 的 `_wpftmp` 编译阶段同样继承——必须显式 `using System.IO;`。
- `App.xaml` 里定义的 `{StaticResource}` 在**没有 `Application` 实例**时不可用（测试宿主构造 Window 的场景）→ 转换器定义在控件自身的 `Resources` 里。
- `WriteableBitmap` 有线程亲和性：解码在媒体读循环线程上，必须经 `Dispatcher` 再 `WritePixels`。
- `MediaFrameReader.TryRead` 在长度前缀非法时**抛异常**（不可恢复），必须留在非 UI 线程。

---

## 3.5 命名管道的访问控制（重要安全边界）

Service 的两类管道（RPC 与媒体）都以 SYSTEM/Administrators/创建者 +
**当前控制台登录用户**的 ACL 创建（见 `PipeAcl`）。

**修复记录**：早期只授予 SYSTEM/Admins/创建者，导致服务以 LocalSystem 运行时
**同机的非提权交互用户根本连不上管道**（Administrators 在过滤令牌里是 deny-only）——
`--console` 开发模式下看不出问题，一装成服务就"控制端连不上服务"，且现象是超时。

**多用户机器的注意事项**：若服务运行时**无人登录控制台**（WTS 取不到控制台用户），
`PipeAcl` 会退回授予 `Authenticated Users` 并记录警告 —— 此时同机其它用户也能连上管道。
缓解方式：把服务改成以特定用户运行，或按需收紧 ACL。自用单用户机器上不受影响。

---

## 4. 部署与运行环境

### 4.1 DPAPI 与重装

- 设备私钥用 **DPAPI LocalMachine** 保护，只保存在本机（`%ProgramData%\DeskLink`）。
- **重装系统或更换机器后私钥不可恢复**，必须重新配对。这是自用场景下的有意取舍。
- `--console` 开发模式与 LocalSystem 服务模式使用同一作用域；若数据目录被 ACL 收紧（SYSTEM+Administrators），普通用户运行 `--console` 会失去写权限。`FileSystemAcl` 会在收紧前**实测探测**，写不了就跳过收紧并返回 `SkippedReason`。

### 4.2 防火墙

- 局域网直连需要放行入站 `47200/TCP` 与 `47200/UDP`。
- **未提权时 `FirewallHelper` 不做任何变更并返回 false**，不会"记录意图假装成功"。
  因此 `-Mode firewall` 冒烟在非管理员下只做只读契约自检并明确警告。
- 真实 netsh **写路径**（增/删/幂等/端口变更）需要在**管理员**终端复跑
  `pwsh -NoProfile -File tests/e2e-smoke.ps1 -Mode firewall`。
  命令拼装与幂等语义已由 `FirewallTests`（13 个用例）完整覆盖。
- 进程路径移动后已放行的规则会失效，需要走 `FirewallHelper.Repair(port)`（Settings 的修复入口）。

### 4.3 UAC 策略

- 安装程序在用户明确同意后设置 `PromptOnSecureDesktop=0`，使 UAC 提示出现在可远控的交互桌面；
  **卸载时必须恢复**（`uninstall.ps1`）。
- UAC 提示仍需被控端用户点击同意或输入凭据——这不是"绕过 UAC"，只是让它出现在可交互桌面上。

### 4.4 硬件与系统

| 项 | 限制 |
|---|---|
| QUIC | 需要 Windows 11（build 20000+）或 Linux/macOS。**Win10 自动回落 TCP/TLS**（`https://` 允许回落；`quic://` 会显式报错而不静默降级）。 |
| 软件 H.264 编码 | 中等 CPU 上 1080p30 可能不达标。软编路径的分辨率上限应收紧（如 720p），避免模糊降级。 |
| 显卡驱动 | DXGI Desktop Duplication 依赖驱动；驱动异常/切换会导致 `ACCESS_LOST`，触发整管线重建（状态条显示"正在恢复画面"）。 |
| 分辨率 / 刷新率 | 极高分辨率（4K+）或高刷新率下，编码与带宽可能成为瓶颈；自适应码率会先降帧率与码率，再降分辨率。 |
| 多显示器 | 仅单显示器画面；显示器热插拔/拓扑变化会触发管线重建。 |

---

## 5. 协议与实现层面的已知取舍

| 项 | 说明 |
|---|---|
| relay 并非字面"无状态" | 内存接线表；**relay 重启即断会话**（30s 重连宽限窗字段已预留、未启用），两端靠指数退避重连。 |
| 撤销语义 | 撤销立即清除配对关系并踢线；被撤销设备再次接入**必须重新输入新的配对码**（协议无"复活"路径）。 |
| 断点续传的粒度 | 只认 `FileAck` 位图；接收端用 `.part` 的**连续前缀长度**作为初始 ack，因此中间有洞的 `.part` 会被重传洞之后的部分（不会校验每个块的内容）。 |
| 媒体通道 | RPC 管道按设计只走元数据；桌面码流与输入事件走**独立**的本地媒体通道（`DeskLink.Media.{instance}`）。这条通道是本地 IPC，**不做端到端加密**（同机进程间），依赖命名管道 ACL 限权。 |
| 命名管道字节序 | 一律 **big-endian**（`[u32 len BE][...]`）。历史上 RPC 管道曾是 little-endian 且与注释不符，已于 2026-09-27 统一。 |
| 文件传输并发 | 引擎支持同一对端上的多条并发传输（按 transferId 区分），但 UI 当前串行发起。 |
| symlink / junction 逃逸 | scope 校验按路径字符串，未拒绝**指向 scope 外**的符号链接/挂载点（审查项 M-5）。自用双端可信场景下风险可控；修复需加重解析点检查。 |
| ~~`SetConfig` 的 relayUrl 运行时不生效~~ | **已修（2026-09-29）**：见 1.7 节③。`SetConfigResult.RequiresRestart` + `GetConfigResult.ActiveRelayUrl`，UI 如实提示"需重启 DeskLinkService"。中继地址仍需重启才生效（有意取舍），但不再假装已保存即生效。 |
| 直连路径的双向配对 | 直连**必须两端互配**（中继由 registry 代劳，直连没有中介）。缺一边会在 SIGMA 前被对端断开，报 `transport: ...软件中止了一个已建立的连接`——易误判为防火墙问题。见 1.7 节"运维陷阱"。 |
| scope 写回注册表未实现 | DESIGN 允许"会话内 scope 授权写回注册表作为常用目录"；当前 scope 仅来自 `--file-scope` 命令行参数，无持久化。 |
| 撤销推送的丢失窗口 | 撤销恰逢目标设备离线时，registry→relay 推送会错过（审查中危#2）；设备重连时服务侧配对校验仍会拒绝，但 relay 旧接线要等会话自然超时。 |
| regapi `verify-challenge` 无 nonce 去重 | 只有 relay 挑战有 `ChallengeReplayGuard`；registry HTTP 端点靠 Bearer token + IP 限速缓解。 |
| 多显示器切换无 UI 入口 | 引擎支持选定单显示器，客户端无切换入口。 |

---

## 6. 测试覆盖现状（2026-09-29 更新）

```
dotnet build DeskLink.sln                     → 0 错误 / 103 警告(CA1416 平台兼容 + 1 xUnit1031)
dotnet test  DeskLink.sln                     → 全部通过（545）
  Protocol.Tests        71
  Service.Tests        176
  Client.Tests         120
  DirectHandshake.Tests 41
  Agent.Tests          137
go build / go vet / gofmt -l                  → 干净
go test ./... -count=1                        → 全部 ok
pwsh tests/e2e-smoke.ps1 -Mode all -Transport both → EXIT=0
pwsh tests/manual-lan-demo.ps1                → EXIT=0（单机双实例直连演练，见 1.7 节①）
```

> 上一版记录的"499"是 2026-09-27 的数字。2026-09-29 的修复轮新增 46 个用例
> （DirectDialer 端到端 15 / ServiceCore 配置与拨号 12 / PipeServer 派发 3 /
> Client UI 诚实性 16），故为 545。
>
> 注：早先记录的"0 警告"只在**增量编译**时成立。全量重编会出现 103 个
> `CA1416` 平台兼容性警告（`net9.0-windows` 下调用 Windows-only 的
> `ProtectedData` / `PipeSecurity` / `QuicConnection` 等）——不影响正确性，
> 但"0 警告"的说法应以"无 error CS"为准。

### 新增的验收工具

| 脚本 | 用途 |
|---|---|
| `tests/verify-lan.ps1` | 直连环境自检与拨号诊断。`-Mode preflight/status/dial/watch`；`-AsJson` 输出机器可读状态。`watch` 每秒刷新会话，便于观察连接/断开。 |
| `tests/manual-lan-demo.ps1` | **单机**双实例直连演练（不需要第二台机器）：起被控端+控制端、双向配对、真实 `direct_dial`、断言两端都看到会话、断言未配对被本地拒绝。不启动桌面代理，因此**不会移动你的鼠标**。 |

这两个脚本只证明**链路**通；画面与键鼠仍必须走 B1/B2 真机人工验收。

冒烟覆盖：中继 TCP/QUIC 双传输的 SIGMA 握手 + 加密控制帧往返 + 真实 RTT +
**P6 文件传输**（上传 + SHA256 对账 + 无残留 `.part` + 冲突 skip + 列目录 + scope 越界拒绝）+
P5.5 局域网直连（已配对成功、未配对被拒）+ P5.6 防火墙只读契约自检 +
**P8/P9 媒体通道端到端**（真 Service 转发泵 + 真常驻代理 + 真客户端管道收到配置与码流）+
**P10 安装/卸载脚本 `-DryRun` 计划自检**（并断言未产生副作用）。

### 媒体通道已打通（原"唯一未接通的链路"已闭环）

链路现在是完整的：

```
桌面代理 --(AgentMedia 管道)--> Service 转发泵 --(Media 管道)--> WPF 客户端
   ^                                                              |
   +---------------------- 输入事件（反向）------------------------+
```

- Service：`MediaPipeServer` 两条管道 + 转发泵（画面丢最旧 / 输入优先丢移动保按键 /
  `MediaFlow` 背压反馈）。
- 代理：`AgentRuntime` 常驻循环（抓屏→旋转→NV12→H.264→分片发送；收输入→注入；
  `DXGI ACCESS_LOST` 只重建管线不重连管道；自适应码率闭环）。
- 客户端：`MediaChannelClient` 收帧重组访问单元 → H264 解码 → 显示。

冒烟 `-Mode media` 用**真 Service + 真常驻代理（测试图源）+ 真客户端管道**验证通过：
客户端收到 `DesktopConfig 320x240` 与桌面码流分片。

### 媒体通道仍未在真机验证的项

| # | 项 | 为什么没验 | 怎么验 |
|---|---|---|---|
| 1 | **用真实桌面跑完整远程画面** | 冒烟用测试图源（无头、可复现）；真抓屏会受锁屏影响 | 解锁桌面后起 `DeskLink.DesktopAgent.exe --run --media-pipe <name>`，客户端进远程页确认有画面 |
| 2 | **输入端到端**（客户端鼠标/键盘 → 远端真实动作） | 会真的操作开发桌面 | 被控端开记事本，**控制端切全屏**（输入转发只在全屏模式挂接，见 1.6 节），确认点击/输入远端同步 |
| 3 | **Raw Input 全屏捕获 + E0 扩展键** | 需要真实键盘交互 | 全屏后按右 Alt/Ctrl、方向键、小键盘 |
| 4 | **两端不同 DPI 下的坐标精度** | 需要两端不同缩放 | 客户端 150% + 被控端 100%，确认落点准确 |
| 5 | **多显示器 + 旋转的真机画面** | 需要真实纵向显示器 | 把被控端显示器设为纵向，确认远端不横躺 |
| 6 | **背压降档的真实收敛** | 需要真实慢链路 | 用限速工具压带宽，确认状态条出现降档且输入仍跟手 |

### 仍然只能靠双机 / 真 VPS 验证的清单

- 真实 NAT 穿透（两端公网出网）
- 公网 TOFU（需域名 + 证书指纹首次确认）
- Win10 1909 旧版 TCP 回退端到端
- 撤销即时踢线（regnotify 推送路径的端到端时序，含局域网直连会话同步关闭）
- 1080p30 真实带宽与自适应码率收敛
- 对端公钥变更触发二次确认
- 两台同子网真实机器 `IP:端口` 直连端到端
- 安装/卸载的真实执行（服务注册、UAC 策略切换与恢复、netsh 写路径）—— 需管理员终端
