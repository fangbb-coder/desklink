<#
.SYNOPSIS
    DeskLink Windows 卸载程序：停服务 → 删服务 → 清防火墙 → 恢复 UAC 策略 → 删文件。

.DESCRIPTION
    卸载必须**恢复到装机前状态**（DESIGN.md 验收项）：
      1) 停止并删除 Windows 服务 DeskLinkService。
      2) 回收局域网直连的入站防火墙规则（复用 DeskLink.Service.exe --firewall-set off）。
      3) 把 PromptOnSecureDesktop 恢复成安装时备份的原值；备份不存在则**不动**该值
         （宁可不动，也不要把用户的策略改成我们猜的值）。
      4) 删除安装目录；数据目录（设备私钥/配对/TOFU）默认保留，需 -RemoveData 才删。

.PARAMETER InstallDir
    安装目录。默认 %ProgramFiles%\DeskLink。

.PARAMETER DirectPort
    局域网直连端口。默认 47200。

.PARAMETER RemoveData
    同时删除数据目录 %ProgramData%\DeskLink（**含设备私钥，不可恢复**）。

.PARAMETER DryRun
    只打印计划，不做任何变更（也不需要管理员权限）。

.EXAMPLE
    pwsh -NoProfile -File installer\uninstall.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'DeskLink'),
    [int]    $DirectPort = 47200,
    [switch] $RemoveData,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:ServiceName  = 'DeskLinkService'
$script:UacBackupKey = 'HKLM:\SOFTWARE\DeskLink\UacBackup'
$script:PoliciesKey  = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$script:DataDir      = Join-Path $env:ProgramData 'DeskLink'

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
    throw 'DeskLink 卸载需要管理员权限（要删服务、改 HKLM、调 netsh）。请用“以管理员身份运行”的终端重试。'
}

Write-Info "安装目录 : $InstallDir"
Write-Info "数据目录 : $script:DataDir$(if ($RemoveData) { '（将被删除）' } else { '（保留）' })"
Write-Info "DryRun   : $($DryRun.IsPresent)"

$serviceExe = Join-Path $InstallDir 'DeskLink.Service.exe'

# ──────────────────────────────────────────────────────────────────────────────
# 1) 防火墙规则回收（在删文件之前做：需要 Service 可执行文件来复用已测逻辑）
# ──────────────────────────────────────────────────────────────────────────────

if ($DryRun) {
    Write-Plan "调用 `"$serviceExe`" --firewall-set $DirectPort off（回收 TCP+UDP 入站规则）"
} elseif (Test-Path $serviceExe) {
    Write-Step "回收局域网直连防火墙规则（端口 $DirectPort）"
    & $serviceExe --firewall-set $DirectPort off
    if ($LASTEXITCODE -ne 0) {
        Write-Warn2 "防火墙规则回收失败（退出码 $LASTEXITCODE）。请手动检查 netsh advfirewall 里名为 DeskLink-Direct-* 的规则。"
    } else {
        Write-Step '防火墙规则已回收'
    }
} else {
    Write-Warn2 "找不到 $serviceExe，跳过防火墙回收。请手动删除 netsh 中 DeskLink-Direct-TCP / DeskLink-Direct-UDP 规则。"
}

# ──────────────────────────────────────────────────────────────────────────────
# 2) 停止并删除服务
# ──────────────────────────────────────────────────────────────────────────────

$svc = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
if ($DryRun) {
    if ($svc) { Write-Plan "停止并删除服务 $script:ServiceName" }
    else { Write-Plan "服务 $script:ServiceName 不存在（跳过）" }
} else {
    if ($svc) {
        if ($svc.Status -ne 'Stopped') {
            Write-Step "停止服务 $script:ServiceName"
            Stop-Service -Name $script:ServiceName -Force
            try { (Get-Service $script:ServiceName).WaitForStatus('Stopped', '00:00:30') } catch { }
        }
        Write-Step "删除服务 $script:ServiceName"
        # sc.exe delete 在服务已被标记删除时会返回非 0，属幂等情形，不视为失败。
        & sc.exe delete $script:ServiceName | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warn2 "sc.exe delete 退出码 $LASTEXITCODE（可能已被删除）" }
    } else {
        Write-Info '服务不存在，跳过'
    }
}

# ──────────────────────────────────────────────────────────────────────────────
# 3) 恢复 UAC 策略
# ──────────────────────────────────────────────────────────────────────────────

if ($DryRun) {
    Write-Plan "若存在 $script:UacBackupKey 则恢复 PromptOnSecureDesktop 原值，然后删除备份键"
} else {
    if (Test-Path $script:UacBackupKey) {
        $backup = Get-ItemProperty -Path $script:UacBackupKey -ErrorAction SilentlyContinue
        $prev = if ($null -ne $backup -and $null -ne $backup.PromptOnSecureDesktop) { [int]$backup.PromptOnSecureDesktop } else { -1 }

        if ($prev -lt 0) {
            # 装机前该值不存在 → 删掉我们添加的值，而不是写 0/1（那会引入用户原本没有的策略）。
            Write-Step '原 PromptOnSecureDesktop 不存在 → 删除该项，恢复为默认'
            Remove-ItemProperty -Path $script:PoliciesKey -Name 'PromptOnSecureDesktop' -ErrorAction SilentlyContinue
        } else {
            Write-Step "恢复 PromptOnSecureDesktop = $prev"
            New-ItemProperty -Path $script:PoliciesKey -Name 'PromptOnSecureDesktop' -PropertyType DWord -Value $prev -Force | Out-Null
        }
        Remove-Item -Path $script:UacBackupKey -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Info '没有 UAC 备份（安装时未修改 UAC 策略），跳过'
    }
}

# ──────────────────────────────────────────────────────────────────────────────
# 4) 删除文件
# ──────────────────────────────────────────────────────────────────────────────

if ($DryRun) {
    Write-Plan "删除安装目录 $InstallDir"
    if ($RemoveData) { Write-Plan "删除数据目录 $script:DataDir（含设备私钥）" }
    else { Write-Plan "保留数据目录 $script:DataDir" }
    Write-Plan '删除注册表键 HKLM:\SOFTWARE\DeskLink（防火墙状态记录）'
} else {
    if (Test-Path $InstallDir) {
        Write-Step "删除安装目录 $InstallDir"
        Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($RemoveData) {
        if (Test-Path $script:DataDir) {
            Write-Warn2 "删除数据目录 $script:DataDir（设备私钥将不可恢复）"
            Remove-Item -Path $script:DataDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    } else {
        Write-Info "保留数据目录 $script:DataDir（再次安装会复用设备身份与配对关系）"
    }

    # 防火墙状态记录键：既然规则已回收，这个键留着只会误导后续安装。
    Remove-Item -Path 'HKLM:\SOFTWARE\DeskLink\Firewall' -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Step '卸载完成'
if (-not $RemoveData) {
    Write-Info "数据目录仍保留在 $script:DataDir；如需彻底清除请加 -RemoveData 重新运行。"
}
