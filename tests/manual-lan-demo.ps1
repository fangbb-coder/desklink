<#
.SYNOPSIS
    DeskLink 局域网直连**单机双实例**演练脚本（不需要第二台机器）。

.DESCRIPTION
    它证明的是"直连链路真的通了"，而不是"画面/键鼠真的通了"：

      被控端实例 (--enable-direct 监听)  ←── SIGMA/AES-GCM ──→  控制端实例 (direct_dial 拨号)

    跑完后会断言：
      1. direct_dial 返回 ok（真实完成 QUIC/TCP-TLS + SIGMA 握手，不是"没报错就算过"）
      2. 控制端 get_status 的 direct_active_sessions > 0
      3. 被控端 get_status 的 direct_active_sessions > 0（入站侧也看到会话）
      4. 用一台未配对设备拨号会被**本地**拒绝，并给出可读原因

    画面与键鼠仍需真机人工验收（见 tests/MANUAL-VERIFICATION.md 的 B1/B2）——
    本脚本不启动桌面代理，也不连媒体通道，因此不会移动你的鼠标。

    ⚠ 直连必须**双向配对**：中继路径由 registry 帮两端互存公钥，
    直连没有这个中介，缺任何一边都会在 SIGMA 之前被对端直接断开
    （表现为 transport 被中止，很容易误判成"防火墙问题"）。

.EXAMPLE
    pwsh -NoProfile -File tests\manual-lan-demo.ps1
    pwsh -NoProfile -File tests\manual-lan-demo.ps1 -Port 47500 -KeepArtifacts
#>
[CmdletBinding()]
param(
    [int] $Port = 47500,
    [switch] $KeepArtifacts
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:RepoRoot   = Split-Path -Parent $PSScriptRoot
$script:ServiceExe = Join-Path $script:RepoRoot 'src\Service\DeskLink.Service\bin\Debug\net9.0\DeskLink.Service.exe'
$script:VerifyScript = Join-Path $script:RepoRoot 'tests\verify-lan.ps1'

$script:Failures = 0
$script:Procs = @()

function Write-Ok   { param($m) Write-Host "[OK]   $m" -ForegroundColor Green }
function Write-Bad  { param($m) Write-Host "[FAIL] $m" -ForegroundColor Red; $script:Failures++ }
function Write-Info { param($m) Write-Host "[i]    $m" -ForegroundColor Gray }
function Write-Step { param($m) Write-Host "`n=== $m ===" -ForegroundColor Cyan }

function Assert-True { param([bool] $Cond, [string] $Message)
    if ($Cond) { Write-Ok $Message } else { Write-Bad $Message }
}

function Start-ServiceInstance {
    param([string] $Label, [string] $DataDir, [string[]] $ExtraArgs)
    $out = Join-Path $script:Root "$Label.out"
    $err = Join-Path $script:Root "$Label.err"
    $p = Start-Process -FilePath $script:ServiceExe `
        -ArgumentList (@('--console', '--data-dir', $DataDir) + $ExtraArgs) `
        -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $script:Procs += $p
    return $p
}

# 调 Service 的 --print-config 取身份字段。
function Get-Identity {
    param([string] $DataDir)
    $out = & $script:ServiceExe --console --data-dir $DataDir --print-config 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "--print-config 失败：$out" }
    $pub = ([regex]'(?m)^\s*ed25519_pub_b64\s*=\s*(\S+)').Match($out).Groups[1].Value
    $id  = ([regex]'(?m)^\s*device_id\s*=\s*(\S+)').Match($out).Groups[1].Value
    if (-not $pub -or -not $id) { throw "无法解析设备身份：$out" }
    return [pscustomobject]@{ Pub = $pub; DeviceId = $id }
}

# 走 verify-lan.ps1 读会话状态（管道 RPC 客户端只有一份实现，避免两处漂移）。
# 用 -AsJson 而不是解析中文输出：中文经子进程 + 控制台代码页转手后容易变乱码，
# 跨进程一律按 JSON 字段名取值。
function Get-SessionJson {
    param([string] $Instance)
    $raw = & pwsh -NoProfile -File $script:VerifyScript -Mode status -Instance $Instance -AsJson 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "status 查询失败：$raw" }
    return $raw | ConvertFrom-Json
}

function Get-DirectSessionCount {
    param([string] $Instance)
    $s = Get-SessionJson -Instance $Instance
    $v = Get-Prop $s 'direct_active_sessions'
    if ($null -eq $v) { return -1 }
    return [int] $v
}

# null-safe 取属性（Service 省略 null 字段时直接访问会抛）。
function Get-Prop {
    param($Object, [string] $Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $Default }
    return $p.Value
}

function Invoke-DialAndCapture {
    param([string] $Instance, [string] $PeerPub, [string] $Target)
    $out = & pwsh -NoProfile -File $script:VerifyScript -Mode dial `
        -Instance $Instance -PeerPub $PeerPub -Target $Target 2>&1 | Out-String
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
}

# ──────────────────────────────────────────────────────────────────────────────

$script:Root = Join-Path $env:TEMP ("desklink-lan-demo-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$dirCtrl  = Join-Path $script:Root 'controlled'
$dirPeer  = Join-Path $script:Root 'controller'
$dirOther = Join-Path $script:Root 'stranger'
New-Item -ItemType Directory -Force -Path $dirCtrl, $dirPeer, $dirOther | Out-Null

try {
    Write-Step "0) 前置检查"
    if (-not (Test-Path $script:ServiceExe)) {
        Write-Bad "找不到 DeskLink.Service.exe，先执行 dotnet build DeskLink.sln"
        return
    }
    Write-Ok "构建产物就位"

    Write-Step "1) 启动被控端实例（--enable-direct，监听 :$Port）"
    Start-ServiceInstance -Label 'controlled' -DataDir $dirCtrl `
        -ExtraArgs @('--enable-direct', '--direct-port', "$Port") | Out-Null
    Start-Sleep -Seconds 3

    $ctrl = Get-Identity -DataDir $dirCtrl
    Write-Info "被控端 ed25519_pub_b64 = $($ctrl.Pub)"
    Write-Info "被控端 device_id       = $($ctrl.DeviceId)"

    Write-Step "2) 启动控制端实例"
    Start-ServiceInstance -Label 'controller' -DataDir $dirPeer -ExtraArgs @() | Out-Null
    Start-Sleep -Seconds 3
    $peer = Get-Identity -DataDir $dirPeer
    Write-Info "控制端 ed25519_pub_b64 = $($peer.Pub)"

    Write-Step "3) 双向配对（直连没有 registry 代劳，缺一边就断）"
    $pair1 = & $script:ServiceExe --console --data-dir $dirPeer  --pair-peer-pub $ctrl.Pub 2>&1 | Out-String
    $pair2 = & $script:ServiceExe --console --data-dir $dirCtrl --pair-peer-pub $peer.Pub 2>&1 | Out-String
    Write-Info "控制端 ← 被控端: $($pair1.Trim())"
    Write-Info "被控端 ← 控制端: $($pair2.Trim())"

    Write-Step "4) 拨号前：两端都不应有活跃直连会话"
    $beforeCtrl = Get-DirectSessionCount -Instance 'controlled'
    $beforePeer = Get-DirectSessionCount -Instance 'controller'
    Assert-True ($beforeCtrl -eq 0) "被控端初始 active_sessions = $beforeCtrl"
    Assert-True ($beforePeer -eq 0) "控制端初始 active_sessions = $beforePeer"

    Write-Step "5) 控制端通过 direct_dial RPC 真实拨号"
    $dial = Invoke-DialAndCapture -Instance 'controller' -PeerPub $ctrl.Pub -Target "127.0.0.1:$Port"
    if ($dial.ExitCode -ne 0) {
        Write-Bad "拨号未成功（exit=$($dial.ExitCode)）"
        Write-Host $dial.Output
        return
    }
    Assert-True ($dial.Output -match '拨号成功') "direct_dial 返回 ok"

    Write-Step "6) 拨号后：两端都应看到活跃会话"
    Start-Sleep -Milliseconds 500
    $afterPeer = Get-DirectSessionCount -Instance 'controller'
    $afterCtrl = Get-DirectSessionCount -Instance 'controlled'
    Assert-True ($afterPeer -ge 1) "控制端 active_sessions = $afterPeer（出站会话）"
    Assert-True ($afterCtrl -ge 1) "被控端 active_sessions = $afterCtrl（入站会话）"

    Write-Step "7) 未配对设备必须被本地拒绝"
    $stranger = Get-Identity -DataDir $dirOther
    $bad = Invoke-DialAndCapture -Instance 'controller' -PeerPub $stranger.Pub -Target "127.0.0.1:$Port"
    Assert-True ($bad.ExitCode -ne 0) "未配对拨号返回非零退出码"
    Assert-True ($bad.Output -match '配对') "未配对拨号给出可读原因（本地拒绝，未打网络）"

    Write-Step "8) 会话仍然存活（说明前一步的拒绝没有误伤已建立的会话）"
    Assert-True ((Get-DirectSessionCount -Instance 'controller') -ge 1) "控制端会话仍在"
}
finally {
    foreach ($p in $script:Procs) {
        try { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force } } catch { }
    }

    Write-Step "被控端日志里的直连关键行"
    $log = Join-Path $script:Root 'controlled.out'
    if (Test-Path $log) {
        Get-Content $log -ErrorAction SilentlyContinue |
            Select-String -Pattern 'DirectServer|DirectDialer|session established' |
            Select-Object -Last 8 | ForEach-Object { Write-Info "  $_" }
    }

    if ($KeepArtifacts) {
        Write-Info "产物保留在：$script:Root"
    } else {
        # DataDir 里的 ACL 探测残留会拒绝删除，删不掉就跳过（不影响结果）。
        try { Remove-Item -Recurse -Force $script:Root -ErrorAction Stop } catch { }
    }
}

Write-Step "结果"
if ($script:Failures -eq 0) {
    Write-Ok "直连链路演练全部通过：dial 成功、两端会话可见、未配对被拒"
    Write-Host ''
    Write-Info "这只证明**链路**通了。画面与键鼠必须另按 tests\MANUAL-VERIFICATION.md 的 B1/B2 真机验收。"
    exit 0
} else {
    Write-Bad "$($script:Failures) 项未通过"
    exit 1
}
