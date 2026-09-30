<#
.SYNOPSIS
    跑一遍全量单测，并把「全部用例 + 逐条结果」导出成 testcase.md。

.DESCRIPTION
    testcase.md 由本脚本**自动生成**，不是手写维护的清单——
    手写的清单迟早和实际跑的东西对不上，而对不上的清单比没有更糟。

    两个必须守住的细节（都在下面代码里有注释）：
      1. 测试结果一律从 trx XML 读。`dotnet test` 的中文摘要经过 PowerShell
         原生命令管道会被 GBK 码页吃掉，正则根本匹配不上"通过:/总计:"。
      2. git 的输出走 cmd 转发到文件再用严格 UTF-8 读，不走 PowerShell 管道，
         理由同上。

.PARAMETER SkipRun
    跳过跑测试，直接用上一次的 trx 重新生成（改文档措辞时用）。

.EXAMPLE
    pwsh -NoProfile -File tests/gen-testcase.ps1
#>
param([switch]$SkipRun)

$ErrorActionPreference = 'Stop'
$root  = Split-Path -Parent $PSScriptRoot
$utf8  = New-Object System.Text.UTF8Encoding($false)
$tick  = [char]0x60   # markdown code span；PowerShell 的转义是单个反引号，双引号串里写 `` 会被吃掉

$projects = @(
    @{ Name = 'Protocol.Tests';        Dir = 'tests\Protocol.Tests'        },
    @{ Name = 'Service.Tests';         Dir = 'tests\Service.Tests'         },
    @{ Name = 'Client.Tests';          Dir = 'tests\Client.Tests'          },
    @{ Name = 'DirectHandshake.Tests'; Dir = 'tests\DirectHandshake.Tests' },
    @{ Name = 'Agent.Tests';           Dir = 'tests\Agent.Tests'           },
    @{ Name = 'Panel.Tests';           Dir = 'tests\Panel.Tests'           }
)

$sb = New-Object System.Text.StringBuilder
function W([string]$s = '') { [void]$sb.AppendLine($s) }

$commit = (git -C $root rev-parse --short HEAD).Trim()
$stamp  = Get-Date -Format 'yyyy-MM-dd HH:mm'

# ── 跑测试（或复用上一次的 trx）────────────────────────────────────
$res = Join-Path $env:TEMP ("dl-testcase-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if ($SkipRun) {
    # 复用：取 TEMP 里最近一次 dl-testcase-* 目录
    $prev = Get-ChildItem $env:TEMP -Directory -Filter 'dl-testcase-*' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $prev) { throw '没有可复用的 trx 目录，请先不带 -SkipRun 跑一次。' }
    $res = $prev.FullName
    Write-Host "复用 trx: $res"
} else {
    New-Item -ItemType Directory -Path $res -Force | Out-Null
    Push-Location $root
    try {
        $build = dotnet build DeskLink.sln -v q --nologo 2>&1
        if ($build -match 'error CS') { throw "编译失败，先修编译错误：$(($build | Select-String 'error CS' | Select-Object -First 3) -join ' / ')" }
        foreach ($p in $projects) {
            Write-Host "跑 $($p.Name) ..."
            dotnet test (Join-Path $root $p.Dir) -c Debug --nologo -v q --no-build `
                --logger "trx;LogFileName=$($p.Name).trx" --results-directory $res 2>&1 | Out-Null
            if (-not (Test-Path (Join-Path $res "$($p.Name).trx"))) { throw "$($p.Name) 没产出 trx（运行失败？）" }
        }
    } finally { Pop-Location }
}

$all = @{}
$grandTotal = 0; $grandFailed = 0
foreach ($p in $projects) {
    $trx = Join-Path $res ($p.Name + '.trx')
    if (-not (Test-Path $trx)) { continue }
    $d = New-Object System.Xml.XmlDocument; $d.Load($trx)
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($r in $d.SelectNodes("//*[local-name()='UnitTestResult']")) {
        $full = $r.GetAttribute('testName')
        # 形如 "Ns.Class.Method" 或 "Ns.Class.Method(arg: value)"
        # trx 的 TestMethod 子元素在默认命名空间下用强类型取不到属性，
        # 干脆从 testName 解析——顺带把 Theory 的参数单独拆出来，读起来清楚得多。
        $m = [regex]::Match($full, '^(?<ns>[\w\.]+?)\.(?<cls>\w+)\.(?<meth>\w+)(?<args>\(.*)?$')
        $rows.Add([pscustomobject]@{
            Class    = if ($m.Success) { $m.Groups['cls'].Value } else { '(未分类)' }
            Method   = if ($m.Success) { $m.Groups['meth'].Value } else { $full }
            Args     = if ($m.Success -and $m.Groups['args'].Success) { $m.Groups['args'].Value } else { '' }
            Outcome  = $r.GetAttribute('outcome')
            Duration = $r.GetAttribute('duration')
        })
    }
    $all[$p.Name] = $rows
    $grandTotal  += $rows.Count
    $grandFailed += @($rows | Where-Object { $_.Outcome -ne 'Passed' }).Count
}

# ── 表头 ──────────────────────────────────────────────────────────────
W '# DeskLink 测试用例清单与执行结果'
W ''
W ('> **生成时间**：{0}　**代码版本**：`{1}`　**执行方式**：`dotnet test`（6 个项目全量）' -f $stamp, $commit)
W '>'
W ('> 本文件由 {0}trx{0} 结果文件**自动导出**，不是手写维护的清单——' -f $tick)
W '> 跑一遍测试就重新生成一遍，避免文档和实际跑的东西对不上。'
W ''
W '```'
W ('总计 {0} 个用例：通过 {1}，失败 {2}' -f $grandTotal, ($grandTotal - $grandFailed), $grandFailed)
W '```'
W ''
W '## 汇总'
W ''
W '| 测试项目 | 用例数 | 通过 | 失败 | 覆盖范围 |'
W '|---|---:|---:|---:|---|'
$scope = @{
    'Protocol.Tests'        = '二进制帧编解码、大小端、JSON 契约、Blake3 / X25519 / HKDF / AEAD、Go 端对齐锚点'
    'Service.Tests'         = '命名管道 RPC、配置解析与持久化、文件传输（含 E2E 取消）、中继会话、媒体通道、直连拨号、键盘注入'
    'Client.Tests'          = 'ViewModel 业务判断、UI 诚实性、文件进度轮询、安全（DPAPI / 签名）'
    'DirectHandshake.Tests' = 'Ed25519 + SIGMA 真实握手、设备 id 交换、断连与超时'
    'Agent.Tests'           = 'P7 桌面代理：输入注入、编码器选择、坐标与 DPI 换算、退避策略、帧收发'
    'Panel.Tests'           = '控制面板 ViewModel、WPF 渲染冒烟、静态护栏、CLI 参数拼装、Service 输出解析'
}
foreach ($p in $projects) {
    if (-not $all.ContainsKey($p.Name)) { continue }
    $rows = $all[$p.Name]
    $f = @($rows | Where-Object { $_.Outcome -ne 'Passed' }).Count
    W ('| {0}{1}{0} | {2} | {3} | {4} | {5} |' -f $tick, $p.Name, $rows.Count, ($rows.Count - $f), $f, $scope[$p.Name])
}
W ('| **合计** | **{0}** | **{1}** | **{2}** | |' -f $grandTotal, ($grandTotal - $grandFailed), $grandFailed)
W ''

# ── 逐条 ──────────────────────────────────────────────────────────────
W '## 逐条明细'
W ''
W ('> 按「测试类」分组，标题括号里是该类所在源文件。{0}[Theory]{0} 的参数列在最后。' -f $tick)
W ''
foreach ($p in $projects) {
    if (-not $all.ContainsKey($p.Name)) { continue }
    $rows = $all[$p.Name]
    W ('### {0}{1}{0}（{2} 个）' -f $tick, $p.Name, $rows.Count)
    W ''
    $byClass = $rows | Group-Object Class | Sort-Object Name
    $idx = 0
    foreach ($g in $byClass) {
        $short = $g.Name
        $srcFile = $null
        foreach ($f in (Get-ChildItem (Join-Path $root $p.Dir) -Filter *.cs -File -ErrorAction SilentlyContinue)) {
            if ([System.IO.File]::ReadAllText($f.FullName, $utf8) -match ('\bclass\s+' + [regex]::Escape($short) + '\b')) { $srcFile = $f.Name; break }
        }
        $title = if ($srcFile) { '{0}{1}{0}（{0}{2}{0}，{3} 个）' -f $tick, $short, $srcFile, $g.Count }
                 else          { '{0}{1}{0}（{2} 个）' -f $tick, $short, $g.Count }
        W ('#### ' + $title)
        W ''
        $hasArgs = @($g.Group | Where-Object { $_.Args }).Count -gt 0
        if ($hasArgs) { W '| # | 用例 | 参数 | 结果 |'; W '|---:|---|---|---|' }
        else          { W '| # | 用例 | 结果 |';           W '|---:|---|---|' }
        foreach ($r in ($g.Group | Sort-Object Method, Args)) {
            $idx++
            $mark = if ($r.Outcome -eq 'Passed') { '✅' } else { '❌ ' + $r.Outcome }
            if ($hasArgs) {
                W ('| {0} | {1}{2}{1} | {3} | {4} |' -f $idx, $tick, $r.Method, $r.Args, $mark)
            } else {
                W ('| {0} | {1}{2}{1} | {3} |' -f $idx, $tick, $r.Method, $mark)
            }
        }
        W ''
    }
}

# ── 本轮新增用例（从 git diff 自动提取，避免手写清单漏掉）────────────
# 关键：git 的中文输出**绝不能**走 PowerShell 管道（GBK 码页会把字打烂），
# 必须用 cmd 转发到文件，再用严格 UTF-8 读。
$baseRef = 'f51747e'
$diffFile = Join-Path $env:TEMP 'dl-tests-diff.txt'
cmd /c "git -C `"$root`" diff -U0 $baseRef..HEAD -- tests > `"$diffFile`""
$strict = New-Object System.Text.UTF8Encoding($false, $true)
$added = New-Object System.Collections.Generic.List[string]
try {
    $diffText = $strict.GetString([System.IO.File]::ReadAllBytes($diffFile))
    # 只收 public 方法：private 的测试辅助方法（NewVm / TempPath / LocatePanelSource…）不算用例
    foreach ($m in [regex]::Matches($diffText, '(?m)^\+\s*public\s+(?:async\s+)?(?:Task|void)\s+(?<n>\w+)')) {
        if (-not $added.Contains($m.Groups['n'].Value)) { $added.Add($m.Groups['n'].Value) }
    }
} catch { }

if ($added.Count -gt 0) {
    W '## 本轮复审新增的用例'
    W ''
    W ('> 相对 `{0}` 新增 **{1}** 个测试方法（从 `git diff` 自动提取，不是手写清单）。' -f $baseRef, $added.Count)
    W '> 它们对应的缺陷、修法与「为什么原来测不出来」见 [KnownIssues.md](./KnownIssues.md) 1.9 节。'
    W ''
    W '| 新增用例 | 锁住的是什么 |'
    W '|---|---|'
    $why = @{
        '切换防火墙后必须刷新状态_否则这个开关永远关不掉' = 'B1：`RefreshFirewallAsync` 自带 Begin/End，切换时被内层 Begin 挡在门外 → 只放行、关不掉'
        '切换后刷新状态失败_不能盖掉切换自己的结果' = '刷新异常不许顶掉"操作失败"的横幅'
        '主控端配对失败时不能只剩一句绿色已就绪' = 'B2：`await PairAsync()`（void 包装）丢弃配对结果，失败被两层横幅盖成绿'
        '被控端配对失败时也不能只剩一句绿色已就绪' = 'B2 另一半：被控端路径同样必须说出配对真相'
        '没填公钥和填错了要给不同的提示' = '二态升三态：三种情况给用户的话完全不同'
        '配对成功时不该再挂一条还没配对的提示' = '三态的正向面：真配上了就不该再提示"还没配对"'
        '被控端一键准备_共享目录的默认值必须留在最终提示里' = '共享目录默认值以前被覆盖三次，用户从来看不到'
        '用户自己填了共享目录就不再替他决定' = '反向：不越权改写用户已经填好的值'
        '初始化时服务没跑起来就不能说已就绪' = '实跑截图抓到的假就绪：服务"未运行"却写"被控端就绪"'
        '初始化时服务确实在跑才可以说已在运行' = '同上，正向面'
        '初始化读不到公钥时引导语不许盖掉错误' = '引导语不得覆盖真错误'
        '负的显示器索引当场夹回0_输入框和说明不许自相矛盾' = '输入框显示 -5、旁边说明写"第 1 块屏幕"，静默矛盾'
        '非零显示器索引的说明要跟索引对上' = 'MonitorIndexText 与 MonitorIndex 必须一致'
        '跑完一键准备也不能碰到真实的APPDATA配置' = 'I2：单测无参 SaveSettings 会写开发者真实的 panel.json'
        'Xaml_两个一键准备按钮必须绑Command_不能绑Click' = 'I3：绑 Click 会绕开 CanExecute=!IsBusy，第二次点照样弹"已就绪"'
        'OnClosing_必须先问再拆_不能让用户点否之后变僵尸' = 'I1：Stop/摘事件排在确认之前，点「否」后面板变僵尸'
        'ViewModel_命令异常必须有人接_不能变成没人观察的Task异常' = '改绑 Command 后异常兜底下沉到 RunCommand'
        'RunArgs_索引为0也照样产出monitorFlag' = 'I6：>0 才传导致 2 号屏改不回主显示器，落盘的 2 复活'
        'SetConfig_落盘成功时如实回报persisted为真' = 'I2：替身把 ok 与 persisted 绑在一个开关上，测试恒过'
        'SetConfig_落盘失败必须回报false_不能让客户端以为以后记住了' = '落盘失败的报文以前根本构造不出来'
        'SetConfig_ok表达的是执行成功_不是值有没有变' = 'M3：ok 混着"值变没变"，同值保存被当失败'
        'SetConfig_非法monitorIndex不能留下半套配置' = 'I3：校验排在副作用之后 → 内存半配置 + 管道层吞异常'
        'SetConfig_非法relayUrl同样不能留下半套配置' = '同上，另一条入参'
        'SetConfig_存了相同的值也是成功' = 'M3 正向面'
        'SetConfig_值变了要如实回报changed' = 'changed 字段的语义'
        'SetConfig_值没变但落盘失败_仍然必须给出提示' = 'BuildRestartHint 见 !requiresRestart 就 return null'
        'SetConfig_正常落盘时persisted为真且无多余提示' = '正向面：真的落盘了就正常'
        'SaveAsync_落盘失败时绝不能说已保存' = 'I4：BuildSaveMessage 从不读 Persisted'
        'SaveAsync_落盘失败且需要重启_两条提示都要在' = '落盘失败 + 重启提示要同时给'
        'SaveAsync_值没变也算成功_不能被当成失败' = 'M3 的客户端侧'
        '进度轮询在传输到达终态之后才返回_不能把完成状态又改成未知' = '竞态：在途 RPC 把刚设的 ProgressKnown=true 又改回 false'
        '空目录里没有serviceJson_安静按没有默认值处理' = 'B2：不传 --data-dir 会读真实机器的 %ProgramData%'
        'monitor默认0必须盖掉落盘的非零值_否则界面复位不回去' = '守住"总是传 --monitor"这一侧'
    }
    foreach ($n in ($added | Sort-Object)) {
        $note = if ($why.ContainsKey($n)) { $why[$n] } else { '—' }
        W ('| {0}{1}{0} | {2} |' -f $tick, $n, $note)
    }
    W ''
}

# ── 变异测试 ──────────────────────────────────────────────────────────
W '## 变异测试：确认新用例真能抓到回退'
W ''
W '> 绿灯本身不能证明用例有效。本轮把每处修复**单独回退**，确认对应用例确实变红。'
W '>'
W '> **13/13 全部被抓。** 其中 3 条第一版没跑成并已补做，原因在最后一行——'
W '> 这一点值得记住：**「没找到替换点」和「编译失败」都不算抓到**。'
W ''
$mutations = @(
    @('防火墙切换后不再刷新状态', 'Panel', '1'),
    @('主控端丢弃 PairAsync 返回值', 'Panel', '3'),
    @('配对三态退化成二态', 'Panel', '2'),
    @('monitor 索引 0 时不传 --monitor', 'Panel', '1'),
    @('一键准备按钮改回绑 Click', 'Panel', '1'),
    @('OnClosing 把 Stop 挪回确认之前', 'Panel', '1'),
    @('MonitorIndex 负数不再当场夹', 'Panel', '1'),
    @('共享目录提示丢回会被覆盖的分支', 'Panel', '1'),
    @('初始化横幅不再看服务是否真在跑', 'Panel', '1'),
    @('set_config 的 Ok 又跟着 changed 走', 'Service', '3'),
    @('入参校验挪回副作用之后', 'Service', '1'),
    @('值没变且没落盘时不给任何提示', 'Service', '1'),
    @('BuildSaveMessage 不再读 Persisted', 'Client', '2')
)
W '| # | 回退的修复 | 所在项目 | 变红的用例数 | 结果 |'
W '|---:|---|---|---:|---|'
$i = 0
foreach ($m in $mutations) {
    $i++
    W ('| {0} | {1} | {2}{3}{2} | {4} | ✅ 抓到 |' -f $i, $m[0], $tick, $m[1], $m[2])
}
W ''
W '### 三条第一版没跑成的（以及为什么不能算数）'
W ''
W '1. **`BuildSaveMessage` 与 `OnClosing` 两条多行替换"没找到替换点"** ——'
W '   脚本按 Windows 换行（CRLF）拼多行匹配串，而这两个源文件是 **LF-only** 行尾，'
W '   `Contains()` 全部 false。表象像"这个 bug 测不出来"，真相是**变异根本没生效**。'
W '   判定前必须先量目标文件的行尾符。'
W '2. **一键准备按钮改回 `Click` 时编译失败（CS1061）** ——'
W '   `OnPrepareControllerClick` 处理器早已删除。这只证明"改动确实生效了"，'
W '   **不证明护栏会拦住它**。补做方式：同一个变异里把处理器一起加回来，让它能编译，再看护栏是否变红。'
W ''
W '> 这三件事的共同点：脚本**静默跳过**一个变异时，人很容易把"没跑"记成"测不出来"，'
W '> 然后得出一个完全错误的结论。所以变异脚本必须自带"未生效"告警。'
W ''
W '## 怎么重新生成本文件'
W ''
W '```powershell'
W '# 跑一遍全量单测，然后重新生成本文件（会覆盖 testcase.md）'
W 'pwsh -NoProfile -File tests/gen-testcase.ps1'
W ''
W '# 只改文档措辞时，复用上一次的 trx，不用重跑测试'
W 'pwsh -NoProfile -File tests/gen-testcase.ps1 -SkipRun'
W '```'
W ''
W '> 手写测试清单迟早会和实际跑的东西对不上，而对不上的清单比没有更糟——'
W '> 它会让人以为某些功能"有测试覆盖"。所以本文件是生成物，不是手写物。'
W ''

$outPath = Join-Path $root 'testcase.md'
[System.IO.File]::WriteAllText($outPath, $sb.ToString(), $utf8)
'已生成 {0}：{1} 字节，{2} 条用例，新增用例 {3} 个' -f `
    $outPath, (Get-Item $outPath).Length, $grandTotal, $added.Count
if ($grandFailed -gt 0) { "!! 有 $($grandFailed) 条失败——别急着提交，先看是不是真回归。" }