# DeskLink 真机人工验证清单（MANUAL-VERIFICATION）

> 本清单把 [KnownIssues.md](../KnownIssues.md) 第 2、3、6 节以及"双机/真 VPS 清单"里的
> 全部未验证项整理成可勾选的操作步骤，编号标注来源（如 `K#2.2` = KnownIssues 第 2 节 #2）。
> 建议按 **A（单机）→ B（双机局域网）→ C（公网 VPS）** 顺序执行；全部通过后把
> KnownIssues 里对应条目划掉。
>
> 术语：**控制端** = 操作别人电脑的一端（跑 DeskLink.Client.exe）；
> **被控端** = 被远控的一端（跑 DeskLink.Service + 桌面代理）。
> 最后更新：2026-09-27。

> ### 🖱 图形化路径优先（2026-09-29 新增）
>
> 本清单原先全是命令行。日常验收请优先用 **`DeskLink.Panel.exe`**（两台机器通用）。
> 面板是**两个角色选项卡**：「我是主控端」在左，「我是被控端」在右；本机服务与高级设置在选项卡之外共用。
>
> - **被控端**：打开面板 → 切到「我是被控端」页 → 点「一键准备被控端」。
>   面板会自动完成：读公钥 → 放行入站防火墙（弹 UAC 提权）→ 开直连 → 注入代理 → 启动服务 → 填默认共享目录。
>   然后把该页给出的**公钥**与**本机地址**发给主控端。
> - **主控端**：打开面板 → 切到「我是主控端」页 → 点「一键准备主控端」→
>   把自己的**公钥**发给被控端（直连必须双向配对）→ 粘进本页的「被控端公钥」输入框点「配对」→
>   点「打开控制界面」，在设备页选「局域网直连」并填被控端 `IP:端口`。
> - **双向配对是被控端也要做的一步**（2026-09-30 补）：被控端页第 3 步有
>   「对方的公钥（主控端发给你的）」输入框。把主控端的公钥粘进去点「配对」，
>   或者在点「一键准备被控端」之前就粘好（那样会顺手一起配）。**只配主控端一边必然连不上**，
>   表现为握手前被断开，很容易误判成防火墙问题。
>
> 面板做的每一件事都有等价命令行（见 README 对照表），所以下面各条勾选项仍然有效，
> 只是可以用面板完成。**下面 B 节写命令行的部分保留**，用于面板出问题时定位。

---

## 0. 前置准备

```powershell
# 0.1 构建全解决方案（0 警告 0 错误）
dotnet build DeskLink.sln

# 0.2 三个可执行文件（Debug 布局；安装后则以安装目录为准）
#   服务    src\Service\DeskLink.Service\bin\Debug\net9.0\DeskLink.Service.exe
#   客户端  src\Client\DeskLink.Client\bin\Debug\net9.0-windows\DeskLink.Client.exe
#   代理    src\Agent\DeskLink.DesktopAgent\bin\Debug\net9.0-windows\DeskLink.DesktopAgent.exe

# 0.3 本机自动化基线（先跑，确保失败不是环境问题）
dotnet test DeskLink.sln
pwsh -NoProfile -File tests/e2e-smoke.ps1 -Mode all -Transport both
```

- 除注明外，命令都在**仓库根**的 PowerShell 里执行；涉及防火墙/安装的步骤必须用**管理员**终端。
- 局域网直连端口默认 `47200`（TCP/UDP）；放行需管理员（`--firewall-set` 或 install.ps1 代办）。
- 管道实例名 = `--data-dir` 末段（默认数据目录 `%ProgramData%\DeskLink` → 实例 `desklink`），
  实际管道名形如 `DeskLink.Agent.desklink`、`DeskLink.Media.desklink`。

---

## A. 单机可验（1 台机器）

### A1. 安装 / 卸载真实执行 `K#6-末`

- [ ] 前置：管理员 PowerShell；未装过或已卸干净
- [ ] `installer\install.ps1`（非 `-DryRun`），同意 UAC 策略调整
- [ ] 验收：
  - `Get-Service *DeskLink*` 有服务且可启动
  - `DeskLink.Service.exe --firewall-status` 显示 47200 TCP/UDP 已放行
  - 注册表 `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System`
    的 `PromptOnSecureDesktop` = 0
- [ ] 卸载 `installer\uninstall.ps1`：服务删除、防火墙规则回收、`PromptOnSecureDesktop` 恢复为 1
- [ ] **失败时**：对照 `tests/e2e-smoke.ps1 -Mode firewall -DryRun` 的计划输出定位是哪步没做

### A2. 服务模式下管道连通（ACL 修复实测） `K#3.5 K#2.6`

- [ ] 前置：A1 安装完成，服务以 LocalSystem 运行，**用普通（非提权）交互用户登录**
- [ ] 启动客户端，设备页点刷新
- [ ] 验收：刷新成功无超时 —— 即普通用户能连上 SYSTEM 服务的 RPC 管道
- [ ] 控制台模式对照：`DeskLink.Service.exe --console --inject-agent`，
  日志里代理启动并完成管道 ping（`DeskLink.Agent.desklink`）
- [ ] **失败时**：`--console` 下正常而服务模式超时 → 回看 PipeAcl 的交互用户授予日志

### A3. 硬件编码器路径 `K#2.2 K#2.3`

- [ ] 前置：有 Intel/NVIDIA/AMD 硬件编码器的机器
- [ ] 用测试图源手动起代理（真实模式同理由 Service 起）：

```powershell
$agent = 'src\Agent\DeskLink.DesktopAgent\bin\Debug\net9.0-windows\DeskLink.DesktopAgent.exe'
& $agent --run --test-pattern --pattern-size 640x480 `
  --pipe DeskLink.Agent.desklink --media-pipe DeskLink.Media.desklink
```

- [ ] 验收：日志 `BackendName` 前缀为 `hardware:`；加 `--no-hardware-encoder` 再跑一次变为软件回落，两路画面均正常
- [ ] 顺带核对显示器枚举：`& $agent --list-monitors`
- [ ] **失败时**：只有 software 路径 → 记录显卡型号与驱动版本（可能真无硬件 MFT，属正常回落）

### A4. 文件传输进度粒度 `K#3.6`

- [ ] 前置：本机跑 `DeskLink.Service.exe --console --file-scope <某个目录>`；客户端连本机 Service
- [ ] 在文件页向该目录传一个 >100MB 文件
- [ ] 验收：进度条按块推进（不是长时间 0% 后直接 100%）
- [ ] **失败时**：进度太粗 → 需要给契约加 `file_progress` 事件（记入 KnownIssues）

---

## B. 双机局域网（同子网两台真实机器）

> **先做这一步的零门槛前置**：在**任意一台**机器上跑
> `pwsh -NoProfile -File tests\manual-lan-demo.ps1`（不需要第二台机器）。
> 它会真起两个 Service 实例、双向配对、走 `direct_dial` 建立真实加密会话，
> 并断言两端都看到活跃会话。它能提前排掉"代码根本没连上"这一类问题，
> 让你在双机排查时不必怀疑协议层。

> 通用前置：
>
> 1. **双向配对**（直连没有 registry 代劳，缺一边就会在 SIGMA 前被对端断开）：
>    - 被控端管理员终端执行 `DeskLink.Service.exe --console --data-dir <A> --print-config`，
>      记下 `ed25519_pub_b64`；
>    - 控制端执行 `--console --data-dir <B> --print-config`，记下自己的公钥；
>    - 控制端：`--data-dir <B> --pair-peer-pub <被控端公钥>`
>    - 被控端：`--data-dir <A> --pair-peer-pub <控制端公钥>`
> 2. 被控端起服务（管理员）：
>    `DeskLink.Service.exe --console --data-dir <A> --enable-direct --inject-agent --file-scope D:\DeskLinkShare`
> 3. 被控端放行入站端口（管理员）：
>    `DeskLink.Service.exe --firewall-set 47200 on`
> 4. 控制端起服务：
>    `DeskLink.Service.exe --console --data-dir <B>`
> 5. 两端各跑一次自检，确认没有红色项：
>    - 被控端：`pwsh -NoProfile -File tests\verify-lan.ps1 -Mode preflight -Instance <A的实例名> -ExpectRole controlled`
>    - 控制端：`pwsh -NoProfile -File tests\verify-lan.ps1 -Mode preflight -Instance <B的实例名>`
>    （实例名 = `--data-dir` 的末段；RPC 管道是 `DeskLink.Client.{实例名}`）

### B0. 拨号链路（可先在命令行验证，不必开 UI）

- [ ] 控制端：`pwsh -NoProfile -File tests\verify-lan.ps1 -Mode dial -Instance <B> -PeerPub <被控端公钥> -Target <被控端IP>:47200`
- [ ] 验收：退出码 0，输出含"拨号成功"，且随后 `direct_active_sessions = 1`
- [ ] **失败时**看输出里的逐项对照（配对 / 防火墙 / SIGMA 三类原因）；
      也可以开 `-Mode watch` 在另一个窗口观察会话数变化
- [ ] 可选：现在打开客户端设备页，选"局域网直连"、填同一个 `IP:端口`、点"连接"，
      应进入远程页（**不会再出现"已连接但黑屏"**——会话不存在时 UI 会如实报错并留在设备页）

### B1. 真实桌面画面端到端 `K#3.1 K#6.媒体1`

- [ ] 前置：B0 通过。被控端桌面已解锁（有真实内容可看）
- [ ] 客户端设备页选中该设备 → 局域网直连 → 填 `IP:端口` → 点"连接"
- [ ] 验收：被控端真实桌面画面出现，鼠标移动时画面实时跟随
- [ ] **失败时**：画面黑但状态显示已连接 → 查被控端代理日志（DXGI/MFT）与 Service 转发泵日志；
      状态直接报错"未能建立远程会话" → 回 B0 查链路

### B2. 输入端到端（须先切全屏） `K#6.媒体2`

- [ ] 前置：B1 已有画面。被控端打开记事本
- [ ] **客户端先点"全屏"**（输入转发只在全屏模式挂接，见 KnownIssues 1.6；非全屏不转发是设计行为）
- [ ] 控制端移动鼠标、点击、打字、滚轮
- [ ] 验收：被控端记事本里点击/输入逐字同步
- [ ] **失败时**：画面动但输入无反应 → 确认全屏状态（RawInputHook 仅前台转发）与代理注入日志

### B3. Raw Input 全键位（E0 扩展键） `K#3.2 K#6.媒体3`

- [ ] 前置：B2 正常
- [ ] 全屏下依次按：右 Alt / 右 Ctrl / 方向键 / 小键盘数字 / F1–F12
- [ ] 验收：远端行为与按键一致（右键与左键不混淆 = E0 位正确）
- [ ] **失败时**：记录具体键位与远端实际输出，对照 `MapVirtualKey` 映射

### B4. DPI 坐标精度 `K#3.3 K#6.媒体4`

- [ ] 客户端显示设 150% 缩放，被控端 100%
- [ ] 全屏下点击被控端屏幕四角与中心（用记事本光标或画图点做靶）
- [ ] 验收：落点准确，无系统性偏移
- [ ] **失败时**：偏移量固定 → 归一化/反归一化系数问题；随缩放变化 → DPI 虚拟化未关闭

### B5. 分辨率 / 旋转切换恢复（ACCESS_LOST） `K#2.4`

- [ ] 前置：B1 会话进行中
- [ ] 被控端改分辨率或旋转屏幕（触发 `DXGI_ERROR_ACCESS_LOST`）
- [ ] 验收：状态条出现"正在恢复画面"，1–3 秒自动恢复，控制权不丢
- [ ] **失败时**：恢复失败/代理退出 → 看 AccessLostRecovery 日志

### B6. 锁屏 / 解锁 `K#2.5`

- [ ] 会话中被控端锁屏（Win+L）
- [ ] 验收：客户端显示"等待本地登录"；解锁后自动恢复画面
- [ ] **失败时**：锁屏后画面冻结而非提示 → SessionWatcher 分支问题

### B7. SendInput 注入与 UIPI `K#2.1`

- [ ] 被控端开一个**管理员**记事本（高完整性），控制端点它 → 能点动（UIPI 允许）
- [ ] 对照：手动以**非管理员**终端起代理 → 控制端操作 →
  远端应提示"被控端需要重新登录以恢复控制"而不是静默失败
- [ ] **失败时**：高完整性窗口点不动 → UIPI/完整性等级问题；静默失败 → 注入错误未上报

### B8. 多显示器 + 旋转画面 `K#6.媒体5`

- [ ] 被控端接第二台显示器（其中一台设纵向）
- [ ] 验收：客户端画面默认主显示器且纵向不横躺；热插拔后管线重建、画面恢复
- [ ] 记录：显示器切换入口当前没有 UI（KnownIssues 第 5 节已知限制）

### B9. 背压降档收敛 `K#6.媒体6`

- [ ] 用限速工具（如 clumsy）把控制端入带宽压到 <2Mbps
- [ ] 验收：状态条出现降档提示；画面变糊但输入仍跟手；恢复带宽后码率回升
- [ ] **失败时**：一直糊 → AdaptiveBitrateController 升档条件过严（记录日志）

### B10. 1080p30 真实带宽 `K#6-末`

- [ ] 两端千兆有线，跑满 1080p30 桌面操作
- [ ] 记录：客户端统计里的实际码率 / 帧率 / 延迟，作为后续调优基线

---

## C. 公网 VPS / 真 NAT

### C1. VPS 部署

- [ ] 前置：一台公网 VPS（Linux），`installer/vps/deploy.sh` + `gen-cert.sh`
- [ ] 验收：`relayd`（QUIC+TCP/TLS 双监听）与 `registryd`（仅 127.0.0.1）两个 systemd 服务在线；
  VPS 防火墙只放行业务端口
- [ ] **失败时**：证书未生成/路径不匹配 → 先看 deploy.sh 日志

### C2. 配对全流程 + 重复配对 409 `K#6-末`

- [ ] 被控端设备页"生成配对码"，控制端输入码领取
- [ ] 验收：双方互存公钥、设备列表互见；随后**不撤销**直接再走一次配对码 →
  客户端明确报"已存在配对关系，请先撤销"（HTTP 409），且配对码未被消费
- [ ] **失败时**：报 500 → 回归（本轮 Go 修复项，记为 bug）

### C3. 中继 NAT 穿透会话 `K#6-末`

- [ ] 两端在不同 NAT 后，走中继建立远控会话
- [ ] 验收：画面 + 输入 + 文件传输全部可用（重复 B1/B2/A4 于中继路径）

### C4. 公网 TOFU 指纹 `K#3.5`

- [ ] 首次连接 relay → 弹指纹确认，同意后记住
- [ ] 二次连接不再弹；删除 `%APPDATA%\DeskLink\client-fingerprints.json` 后重新连接 → 再次弹
- [ ] 公钥变更：删除被控端数据目录（重新生成设备密钥）→ 控制端连接 → 触发**公钥变更二次确认**
- [ ] **失败时**：变更后静默放行 → 回归（安全问题，必须修）

### C5. Win10 TCP 回退 `K#4.4`

- [ ] 用 Win10（无 QUIC 支持）机器跑控制端/被控端
- [ ] `--relay-url https://...` → 自动回落 TCP-TLS，会话正常
- [ ] `--relay-url quic://...` 在该机器 → **显式报错**而非静默降级
- [ ] **失败时**：https URL 直接失败 → 回退判定问题

### C6. 撤销即时踢线 `K#6-末`

- [ ] 前置：中继会话 + 局域网直连会话同时在跑（双路径）
- [ ] 控制端设备页对被控端点"撤销"
- [ ] 验收：
  - 中继会话立即断开（不等 30s 超时）
  - **局域网直连会话同步关闭**（DESIGN 要求双路径同时失效）
  - 被撤销方立即重连 → 被拒，必须重新输入新配对码
- [ ] **失败时**：直连会话仍活着 → 撤销推送丢失窗口（KnownIssues 第 5 节已知限制，记录时序）

---

## 记录模板

每完成一项，按此格式记回 KnownIssues 或工单：

```
[日期] 验证项编号（如 B3）：PASS / FAIL
机器 A（被控端）：型号 / 系统 / 显卡 / DPI
机器 B（控制端）：型号 / 系统 / DPI
观测：……（失败时附两端日志关键行）
```
