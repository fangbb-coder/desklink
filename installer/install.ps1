<#
.SYNOPSIS
    DeskLink Windows 安装程序（服务注册 + UAC 策略 + 局域网直连防火墙放行）。

.DESCRIPTION
    做四件事：
      1) 把构建产物复制到安装目录（默认 %ProgramFiles%\DeskLink）。
      2) 注册并启动 Windows 服务 DeskLinkService（LocalSystem + 开机自启）。
      3) 询问"是否允许局域网直连"；同意则调用 DeskLink.Service.exe --firewall-set 放行端口。
      4) 询问"是否让 UAC 提示显示在可远控的交互桌面"；同意则把
         PromptOnSecureDesktop 置 0，并**把原值备份到注册表**供卸载恢复。

    设计原则：
      - 幂等：重复运行不报错（服务已存在则更新配置并重启）。
      - 诚实：任何需要管理员权限的步骤在非管理员下**直接失败**，不静默跳过。
      - 可测：-DryRun 只打印将要执行的动作，不触碰系统，供冒烟脚本断言。

.PARAMETER InstallDir
    安装目录。默认 %ProgramFiles%\DeskLink。

.PARAMETER DirectPort
    局域网直连端口。默认 47200。

.PARAMETER EnableDirect
    不问，直接放行局域网直连端口。

.PARAMETER DisableDirect
    不问，直接放行关闭（用于修复/回退）。

.PARAMETER SetUacInteractive
    不问，直接把 UAC 提示切到交互桌面（PromptOnSecureDesktop=0）。

.PARAMETER SourceBin
    构建产物目录。默认取仓库内 src\Service\DeskLink.Service\bin\Debug\net9.0。

.PARAMETER SkipBuild
    跳过 dotnet build（假设产物已存在）。

.PARAMETER DryRun
    只打印计划，不做任何变更（也不需要管理员权限）。

.EXAMPLE
    # 预演（不需管理员）
    pwsh -NoProfile -File installer\install.ps1 -DryRun

.EXAMPLE
    # 正式安装（管理员）
    pwsh -NoProfile -File installer\install.ps1 -EnableDirect
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'DeskLink'),
    [int]    $DirectPort = 47200,
    [switch] $EnableDirect,
    [switch] $DisableDirect,
    [switch] $SetUacInteractive,
    [string] $SourceBin,
    [switch] $SkipBuild,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:ServiceName    = 'DeskLinkService'
$script:ServiceDisplay = 'DeskLink 远程桌面服务'
# UAC 备份键：卸载时据此恢复原值。用独立子键而不是把值混在 Firewall 键里，
# 避免卸载时误删防火墙状态。
$script:UacBackupKey   = 'HKLM:\SOFTWARE\DeskLink\UacBackup'
$script:PoliciesKey    = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'

# ──────────────────────────────────────────────────────────────────────────────
# 输出与前置检查
# ──────────────────────────────────────────────────────────────────────────────

function Write-Step { param([string] $M) Write-Host "[+] $M" -ForegroundColor Green }
function Write-Info { param([string] $M) Write-Host "[i] $M" -ForegroundColor Cyan }
function Write-Warn2 { param([string] $M) Write-Host "[!] $M" -ForegroundColor Yellow }
function Write-Plan { param([string] $M) Write-Host "[plan] $M" -ForegroundColor Magenta }

function Test-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    return ([System.Security.Principal.WindowsPrincipal]$id).IsInRole(
        [System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not $DryRun -and -not (Test-Admin)) {
    throw 'DeskLink 安装需要管理员权限（要注册 Windows 服务、写 HKLM、调用 netsh）。请用“以管理员身份运行”的终端重试。'
}

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $SourceBin) {
    $SourceBin = Join-Path $script:RepoRoot 'src\Service\DeskLink.Service\bin\Debug\net9.0'
}
$script:AgentSource = Join-Path $script:RepoRoot 'src\Agent\DeskLink.DesktopAgent\bin\Debug\net9.0-windows'

Write-Info "安装目录   : $InstallDir"
Write-Info "直连端口   : $DirectPort"
Write-Info "产物来源   : $SourceBin"
Write-Info "DryRun     : $($DryRun.IsPresent)"

# ──────────────────────────────────────────────────────────────────────────────
# 1) 构建（可选）
# ──────────────────────────────────────────────────────────────────────────────

if (-not $SkipBuild -and -not $DryRun) {
    Write-Step '构建解决方案（Release 语义仍用 Debug 产物：本项目未做 Release 裁剪）'
    & dotnet build (Join-Path $script:RepoRoot 'DeskLink.sln') -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败（退出码 $LASTEXITCODE）" }
}

# ──────────────────────────────────────────────────────────────────────────────
# 2) 复制文件
# ──────────────────────────────────────────────────────────────────────────────

$serviceExe = Join-Path $InstallDir 'DeskLink.Service.exe'
$agentExe   = Join-Path $InstallDir 'DeskLink.DesktopAgent.exe'

if ($DryRun) {
    Write-Plan "创建目录 $InstallDir"
    Write-Plan "复制 $SourceBin\* → $InstallDir（服务与依赖）"
    Write-Plan "复制 $script:AgentSource\DeskLink.DesktopAgent.* → $InstallDir（桌面代理）"
} else {
    if (-not (Test-Path $SourceBin)) { throw "找不到构建产物目录：$SourceBin（先去掉 -SkipBuild 或检查路径）" }
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Write-Step "复制服务与依赖 → $InstallDir"
    Copy-Item -Path (Join-Path $SourceBin '*') -Destination $InstallDir -Recurse -Force
    if (Test-Path $script:AgentSource) {
        Write-Step "复制桌面代理及其依赖 → $InstallDir"
        # 必须复制**整个输出目录**，不能只按 `DeskLink.DesktopAgent.*` 前缀挑文件：
        # 代理依赖 Vortice.DXGI/Direct3D11/MediaFoundation/Windows 与 SharpGen.Runtime 等
        # 一堆**名字与代理无关**的 DLL。只复制前缀匹配的文件会得到一个"启动即
        # FileNotFoundException"的代理——而且装完之后很难看出少了什么。
        Copy-Item -Path (Join-Path $script:AgentSource '*') -Destination $InstallDir -Recurse -Force
        # 代理目录里也带着 DeskLink.Protocol.dll 等同名依赖，与 Service 的是同一次构建产物，
        # 覆盖无副作用；但**不能反过来**（先代理后服务）——那会让 Service 的依赖被旧版覆盖。
        Copy-Item -Path (Join-Path $SourceBin '*') -Destination $InstallDir -Recurse -Force
    } else {
        Write-Warn2 "未找到桌面代理产物（$script:AgentSource）：远端画面将不可用。请先构建 DeskLink.DesktopAgent。"
    }
}

# ──────────────────────────────────────────────────────────────────────────────
# 3) 注册服务
# ──────────────────────────────────────────────────────────────────────────────

$existing = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue

if ($DryRun) {
    if ($existing) {
        Write-Plan "服务已存在 → 停止并用新路径重配（sc.exe config）"
    } else {
        Write-Plan "新建服务 $script:ServiceName → \"$serviceExe\"（LocalSystem / Automatic）"
    }
    Write-Plan "配置失败恢复动作（restart/60s）并启动服务"
} else {
    if ($existing) {
        Write-Step "服务已存在，更新可执行路径并重启"
        if ($existing.Status -ne 'Stopped') {
            Stop-Service -Name $script:ServiceName -Force
            (Get-Service $script:ServiceName).WaitForStatus('Stopped', '00:00:30')
        }
        # sc.exe config 改 binPath；New-Service 在服务已存在时会报错，所以走 sc。
        & sc.exe config $script:ServiceName binPath= "`"$serviceExe`"" start= auto | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe config 失败（退出码 $LASTEXITCODE）" }
    } else {
        Write-Step "注册服务 $script:ServiceName"
        New-Service -Name $script:ServiceName `
                    -BinaryPathName "`"$serviceExe`"" `
                    -DisplayName $script:ServiceDisplay `
                    -Description 'DeskLink 远程桌面：中继连接、会话与文件传输（LocalSystem）' `
                    -StartupType Automatic | Out-Null
    }

    # 崩溃自动重启（失败恢复）。sc.exe 的 failure 动作是"累加"的，重复执行会叠加，
    # 因此先 reset 再设置，保证幂等。
    & sc.exe failure $script:ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    Write-Step '已配置服务失败自动重启（60s）'

    Start-Service -Name $script:ServiceName
    Write-Step "服务已启动：$script:ServiceName"
}

# ──────────────────────────────────────────────────────────────────────────────
# 4) 局域网直连端口放行
# ──────────────────────────────────────────────────────────────────────────────

$directDecision = $null
if ($EnableDirect) { $directDecision = $true }
elseif ($DisableDirect) { $directDecision = $false }

if ($null -eq $directDecision) {
    if ($DryRun) {
        Write-Plan '询问用户“是否允许局域网直连”（-DryRun 下不询问，默认按“否”继续）'
        $directDecision = $false
    } else {
        $answer = Read-Host '是否允许局域网直连？将放行入站端口 TCP/UDP，允许同网段设备直连（y/N）'
        $directDecision = ($answer -match '^(y|yes|是)$')
    }
}

if ($directDecision) {
    if ($DryRun) {
        Write-Plan "调用 `"$serviceExe`" --firewall-set $DirectPort on  （放行 TCP+UDP 入站）"
    } else {
        Write-Step "放行局域网直连端口 $DirectPort（TCP+UDP）"
        # 复用 C# 侧已单测覆盖的防火墙逻辑，脚本不自己拼 netsh（避免第二份真相）。
        & $serviceExe --firewall-set $DirectPort on
        if ($LASTEXITCODE -ne 0) {
            Write-Warn2 "防火墙放行失败（退出码 $LASTEXITCODE）。局域网直连将不可用；可在 Settings 里重试或手动放行。"
        } else {
            Write-Step '防火墙规则已放行'
        }
    }
} else {
    Write-Info '未启用局域网直连（设备页的“局域网直连”入口将显示为禁用）'
}

# ──────────────────────────────────────────────────────────────────────────────
# 5) UAC 策略（PromptOnSecureDesktop）
# ──────────────────────────────────────────────────────────────────────────────

$uacDecision = $SetUacInteractive.IsPresent
if (-not $SetUacInteractive -and -not $DryRun) {
    $answer = Read-Host '是否让 UAC 提示显示在可远控的交互桌面？（同意后 UAC 弹窗可被远程操作；卸载时会自动恢复原设置）(y/N)'
    $uacDecision = ($answer -match '^(y|yes|是)$')
}

# -DryRun 的目的是让评审/冒烟看到**完整计划**，因此这一步无论是否选择都要打印，
# 而不是像真实路径那样走"未选择"分支只留一句话（否则计划里会看不到 UAC 那一步）。
if ($DryRun) {
    if ($uacDecision) {
        Write-Plan "备份当前 PromptOnSecureDesktop 到 $script:UacBackupKey（仅首次安装写入，保证卸载能恢复原值）"
        Write-Plan "设置 $script:PoliciesKey\PromptOnSecureDesktop = 0（UAC 提示回到可远控的交互桌面）"
    } else {
        Write-Plan '（未选择）不修改 UAC 策略：PromptOnSecureDesktop 保持原值，UAC 弹窗仍在隔离 Secure Desktop'
    }
}

if (-not $DryRun -and $uacDecision) {
    New-Item -ItemType Directory -Force -Path 'HKLM:\SOFTWARE\DeskLink' | Out-Null
    # 只在"还没有备份"时记录原值：重复安装不得覆盖成当前（已被改成 0）的值，
    # 否则卸载时就恢复不回真正的原始设置。
    $hasBackup = Test-Path $script:UacBackupKey
    if (-not $hasBackup) {
        $prev = (Get-ItemProperty -Path $script:PoliciesKey -Name PromptOnSecureDesktop -ErrorAction SilentlyContinue)
        $prevValue = if ($null -ne $prev -and $null -ne $prev.PromptOnSecureDesktop) { [int]$prev.PromptOnSecureDesktop } else { -1 }
        New-Item -ItemType Directory -Force -Path $script:UacBackupKey | Out-Null
        New-ItemProperty -Path $script:UacBackupKey -Name 'PromptOnSecureDesktop' -PropertyType DWord -Value $prevValue -Force | Out-Null
        New-ItemProperty -Path $script:UacBackupKey -Name 'SavedUtc' -PropertyType String -Value (Get-Date -Format 'o') -Force | Out-Null
        Write-Step "已备份原 PromptOnSecureDesktop = $prevValue（-1 表示原值不存在）"
    }
    else {
        Write-Info '已存在 UAC 备份，保留原值不变（保证卸载能恢复）'
    }

    New-ItemProperty -Path $script:PoliciesKey -Name 'PromptOnSecureDesktop' -PropertyType DWord -Value 0 -Force | Out-Null
    Write-Step 'UAC 提示已切到交互桌面（PromptOnSecureDesktop=0）'
    Write-Warn2 '注意：UAC 提示仍需被控端用户点击同意/输入凭据，这不是绕过 UAC。'
}
elseif (-not $DryRun) {
    Write-Info '未修改 UAC 策略（UAC 弹窗将显示在默认的隔离 Secure Desktop，远程不可见）'
}

# ──────────────────────────────────────────────────────────────────────────────
# 6) 首次启动指引
# ──────────────────────────────────────────────────────────────────────────────

if ($DryRun) {
    Write-Plan "运行 `"$serviceExe`" --console --print-config 打印首次启动信息"
} else {
    Write-Step '首次启动信息'
    & $serviceExe --console --print-config
}

Write-Host ''
Write-Step '安装完成'
Write-Info "服务名        : $script:ServiceName"
Write-Info "安装目录      : $InstallDir"
Write-Info "数据目录      : $env:ProgramData\DeskLink（设备私钥 / 配对 / TOFU 记录）"
Write-Info "控制端程序    : $InstallDir\DeskLink.Client.exe"
Write-Info "局域网直连    : $(if ($directDecision) { "已放行 $DirectPort/TCP+UDP" } else { '未启用' })"
Write-Info '卸载          : installer\uninstall.ps1（会恢复 UAC 策略并清理防火墙规则）'
