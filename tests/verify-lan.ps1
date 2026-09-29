<#
.SYNOPSIS
    DeskLink 局域网直连（B1/B2）真机验收辅助脚本。

.DESCRIPTION
    MANUAL-VERIFICATION.md 的 B 节需要两台真实机器 + 人眼看画面与输入，
    无法自动化。但**把"环境是否就绪"和"会话是否真的建立"这两件事
    变成可断言的命令行输出**，人工只需要盯住"画面动 / 键鼠生效"。

    本脚本因此不代替 B1/B2，而是消灭 B1/B2 最常见的两种假失败：
      1. 环境没配好（防火墙没放行 / 服务没起 / 代理没起）→ preflight 直接报缺什么；
      2. 会话没建立但 UI 显示"已连接" → status/dial 如实打印会话状态。

    典型用法（控制端）：
      pwsh -NoProfile -File tests\verify-lan.ps1 -Mode preflight
      pwsh -NoProfile -File tests\verify-lan.ps1 -Mode status
      pwsh -NoProfile -File tests\verify-lan.ps1 -Mode dial `
          -PeerPub <被控端 ed25519 公钥 base64> -Target 192.168.1.20:47200

    典型用法（被控端）：
      pwsh -NoProfile -File tests\verify-lan.ps1 -Mode preflight -ExpectRole controlled

.PARAMETER Mode
    preflight  检查本机是否具备直连条件（防火墙/服务/配置），缺什么列什么
    status     打印 Service 侧真实会话状态（中继 E2E + 直连活跃会话数）
    dial       通过 direct_dial RPC 真实拨号一次，并打印结果与会话状态
    watch      每秒刷新会话状态，便于观察连接/断开（Ctrl+C 退出）

.PARAMETER Target
    被控端地址，格式 host:port（-Mode dial 必填）。

.PARAMETER PeerPub
    被控端 Ed25519 公钥（base64）。被控端用
    `DeskLink.Service.exe --console --data-dir <dir> --print-config` 取得。

.PARAMETER PipeName
    目标 Service 的 RPC 管道名。默认按 --Instance 推导为
    `DeskLink.Client.{Instance}` —— 与 PipeServer 的命名一致
    （同实例还有 DeskLink.Agent.* / DeskLink.Media.* / DeskLink.AgentMedia.*，
    那些是给桌面代理与媒体通道用的，本脚本不碰）。

.PARAMETER Instance
    数据目录末段（默认 data），用于推导管道名。

.PARAMETER ExpectRole
    controlled 时额外检查本机是否已启用直连监听（--enable-direct / 防火墙已放行）。

.EXAMPLE
    pwsh -NoProfile -File tests\verify-lan.ps1 -Mode preflight -ExpectRole controlled
#>
[CmdletBinding()]
param(
    [ValidateSet('preflight', 'status', 'dial', 'watch')]
    [string] $Mode = 'preflight',

    [string] $Target,
    [string] $PeerPub,
    [string] $Instance = 'desklink',
    [string] $PipeName,
    [ValidateSet('controller', 'controlled')]
    [string] $ExpectRole = 'controller',

    # 输出原始 JSON 而不是中文表格。给脚本调用方用——中文经控制台代码页转手
    # 后很容易变成乱码，跨进程用 JSON 解析才不会踩这个坑。
    [switch] $AsJson
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$ServiceExe = Join-Path $RepoRoot 'src\Service\DeskLink.Service\bin\Debug\net9.0\DeskLink.Service.exe'

if (-not $PipeName) { $PipeName = "DeskLink.Client.$Instance" }

$script:Failures = 0

function Write-Ok    { param($m) Write-Host "[OK]   $m" -ForegroundColor Green }
function Write-Bad   { param($m) Write-Host "[FAIL] $m" -ForegroundColor Red; $script:Failures++ }
function Write-Info  { param($m) Write-Host "[i]    $m" -ForegroundColor Gray }
function Write-Note  { param($m) Write-Host "[!]    $m" -ForegroundColor Yellow }

# 从流里读满 count 字节（命名管道是字节流，不保证一次读完）。
function Read-ExactStream {
    param([System.IO.Stream] $Stream, [byte[]] $Buffer, [int] $Count)
    $read = 0
    while ($read -lt $Count) {
        $n = $Stream.Read($Buffer, $read, $Count - $read)
        if ($n -eq 0) { throw "管道在读完 $read/$Count 字节前关闭" }
        $read += $n
    }
}

# 调用 Service 命名管道 JSON-RPC。帧格式 [u32 BE 长度][utf-8 JSON]。
# 刻意手写字节收发：既顺带验证字节序约定，也避免脚本依赖任何 C# 程序集。
function Invoke-ServiceRpc {
    param(
        [Parameter(Mandatory)] [string] $Method,
        $Params = $null,
        [int] $ConnectTimeoutMs = 5000
    )
    $client = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::None)
    try {
        $client.Connect($ConnectTimeoutMs)
        $req = @{ method = $Method; id = [guid]::NewGuid().ToString('N') }
        if ($null -ne $Params) { $req.params = $Params }
        $json = [System.Text.Encoding]::UTF8.GetBytes(($req | ConvertTo-Json -Depth 8 -Compress))

        $len = [System.BitConverter]::GetBytes([uint32] $json.Length)
        [Array]::Reverse($len)   # → big-endian
        $client.Write($len, 0, 4)
        $client.Write($json, 0, $json.Length)
        $client.Flush()

        $hdr = New-Object byte[] 4
        Read-ExactStream -Stream $client -Buffer $hdr -Count 4
        [Array]::Reverse($hdr)
        $n = [System.BitConverter]::ToUInt32($hdr, 0)
        if ($n -gt 4MB) { throw "响应帧过大：$n 字节" }

        $body = New-Object byte[] $n
        Read-ExactStream -Stream $client -Buffer $body -Count $n
        return [System.Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json
    }
    finally { $client.Dispose() }
}

# ──────────────────────────────────────────────────────────────────────────────
# 会话状态
# ──────────────────────────────────────────────────────────────────────────────

# 安全读取 JSON 字段：Service 省略 null 字段时会直接抛 PropertyNotFound
# （Set-StrictMode -Version Latest 下尤其明显）。诊断工具不该因为一个空字段崩掉。
function Get-Field {
    param($Object, [string] $Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p -or $null -eq $p.Value) { return $Default }
    return $p.Value
}

function Get-SessionStatus {
    $resp = Invoke-ServiceRpc -Method 'get_status'
    if ($null -ne (Get-Field $resp 'error')) {
        throw "get_status 失败：$((Get-Field $resp 'error').message)"
    }
    return $resp.result
}

function Format-SessionStatus {
    param($s)
    $peer = Get-Field $s 'e2e_peer_device_id' '(无)'
    $lines = @(
        "  中继 relay_state      = $(Get-Field $s 'relay_state')"
        "  中继 e2e_state        = $(Get-Field $s 'e2e_state')"
        "  中继 e2e_peer         = $peer"
        "  中继 round_trip_ok    = $(Get-Field $s 'e2e_control_round_trip_ok')"
        "  中继 rtt_ms           = $(Get-Field $s 'e2e_rtt_ms')"
        "  直连 direct_enabled   = $(Get-Field $s 'direct_enabled')"
        "  直连 active_sessions  = $(Get-Field $s 'direct_active_sessions' 0)"
        "  媒体 client_connected = $(Get-Field $s 'media_client_connected')"
        "  媒体 agent_connected  = $(Get-Field $s 'media_agent_connected')"
    )
    $lines | ForEach-Object { Write-Info $_ }
}

# ──────────────────────────────────────────────────────────────────────────────
# preflight
# ──────────────────────────────────────────────────────────────────────────────

function Invoke-Preflight {
    Write-Host "=== 直连环境自检（role=$ExpectRole）===" -ForegroundColor Cyan

    if (-not (Test-Path $ServiceExe)) {
        Write-Bad "找不到 DeskLink.Service.exe：$ServiceExe"
        Write-Note "先在仓库根执行：dotnet build DeskLink.sln"
        return
    }
    Write-Ok "Service 可执行文件存在"

    # 1) 防火墙（读状态永远是只读的，不提权也不会改任何东西）
    $fwJson = & $ServiceExe --console --data-dir $env:ProgramData'\DeskLink' --firewall-status 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Bad "firewall-status 执行失败：$fwJson"
    } else {
        $fw = ($fwJson | Out-String).Trim() | ConvertFrom-Json
        Write-Info "direct_port=$($fw.direct_port) elevated=$($fw.elevated) tcp_rule=$($fw.rule_tcp_present) udp_rule=$($fw.rule_udp_present)"
        if ($ExpectRole -eq 'controlled') {
            if ($fw.rule_tcp_present) {
                Write-Ok "入站 TCP 规则已放行（端口 $($fw.rule_tcp_port)）"
            } else {
                Write-Bad "入站 TCP 规则不存在 —— 被控端会被防火墙挡住"
                Write-Note "管理员终端执行：& '$ServiceExe' --firewall-set $($fw.direct_port) on"
            }
            if ($fw.rule_udp_present) {
                Write-Ok "入站 UDP 规则已放行（QUIC）"
            } else {
                Write-Note "UDP 规则缺失：QUIC 不可用，会回落 TCP/TLS（可用但更慢）"
            }
        } else {
            Write-Info "控制端通常无需入站规则；出站拨号不受本机防火墙入站规则影响"
        }
    }

    # 2) Service 是否在跑（管道能不能连上）
    try {
        $s = Get-SessionStatus
        Write-Ok "Service RPC 可达（管道 $PipeName）"
        Format-SessionStatus $s
    } catch {
        Write-Bad "Service RPC 不可达：$($_.Exception.Message)"
        Write-Note "以服务模式：Get-Service DeskLinkService"
        Write-Note "或开发模式：DeskLink.Service.exe --console --inject-agent"
        return
    }

    # 3) 设备身份
    try {
        $info = Invoke-ServiceRpc -Method 'get_device_info'
        $pub = $info.result.ed25519_pub_b64
        Write-Ok "本机 ed25519 公钥（base64）= $pub"
        Write-Info "把**对端**的公钥通过 --pair-peer-pub 配进来；两端必须互配"
    } catch {
        Write-Bad "get_device_info 失败：$($_.Exception.Message)"
    }

    if ($ExpectRole -eq 'controlled') {
        Write-Note "被控端还需确认：Service 以 --enable-direct 启动，且桌面代理已连上（看上面的 agent_connected）"
    }
    Write-Note "preflight 只检查环境。画面/输入是否真的通，必须人工按 MANUAL-VERIFICATION.md 的 B1/B2 验收。"
}

# ──────────────────────────────────────────────────────────────────────────────
# status / watch
# ──────────────────────────────────────────────────────────────────────────────

function Invoke-Status {
    if ($AsJson) {
        Get-SessionStatus | ConvertTo-Json -Depth 6 -Compress
        return
    }
    Write-Host "=== 会话状态 ===" -ForegroundColor Cyan
    Format-SessionStatus (Get-SessionStatus)
}

function Invoke-Watch {
    Write-Host "=== 会话状态实时观察（Ctrl+C 退出）===" -ForegroundColor Cyan
    Write-Note "在另一个终端跑 WPF 客户端连接，看这里的数字变化即可判断链路是否真的建立"
    while ($true) {
        try {
            $s = Get-SessionStatus
            $stamp = Get-Date -Format 'HH:mm:ss'
            $relay = $s.e2e_state
            $direct = $s.direct_active_sessions
            $agent = $s.media_agent_connected
            $client = $s.media_client_connected
            Write-Host ("$stamp  relay={0,-12} direct_sessions={1}  agent={2,-5} client={3}" -f $relay, $direct, $agent, $client)
        } catch {
            Write-Host ("{0}  RPC 不可达：{1}" -f (Get-Date -Format 'HH:mm:ss'), $_.Exception.Message)
        }
        Start-Sleep -Seconds 1
    }
}

# ──────────────────────────────────────────────────────────────────────────────
# dial
# ──────────────────────────────────────────────────────────────────────────────

function Invoke-Dial {
    Write-Host "=== 发起局域网直连（direct_dial）===" -ForegroundColor Cyan

    if (-not $Target)    { Write-Bad '缺少 -Target（格式 host:port）'; return }
    if (-not $PeerPub)   { Write-Bad '缺少 -PeerPub（被控端 ed25519 公钥 base64）'; return }

    $parts = $Target -split ':'
    if ($parts.Count -ne 2) { Write-Bad "-Target 格式应为 host:port，收到 '$Target'"; return }
    $hostName = $parts[0]
    $port = 0
    if (-not [int]::TryParse($parts[1], [ref] $port)) { Write-Bad "端口不是数字：$($parts[1])"; return }

    Write-Info "拨号目标 = $hostName`:$port"
    Write-Info "对端公钥 = $($PeerPub.Substring(0, [Math]::Min(16, $PeerPub.Length)))…"

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $resp = Invoke-ServiceRpc -Method 'direct_dial' -Params @{
        peer_pub_b64 = $PeerPub
        host         = $hostName
        port         = $port
    } -ConnectTimeoutMs 5000
    $sw.Stop()

    $err = Get-Field $resp 'error'
    if ($null -ne $err) {
        Write-Bad "direct_dial 被服务端拒绝：$($err.message)"
        return
    }

    $r = $resp.result
    if ((Get-Field $r 'ok' $false)) {
        Write-Ok ("拨号成功（transport={0}，耗时 {1}ms）" -f (Get-Field $r 'transport' '?'), $sw.ElapsedMilliseconds)
    } else {
        Write-Bad "拨号失败：$(Get-Field $r 'detail' '未知原因')"
        Write-Note '失败原因逐项对照：'
        Write-Note '  · 提示"配对" → 本机配对列表里没有这台设备'
        Write-Note '     控制端：--pair-peer-pub <被控端公钥>'
        Write-Note '     被控端：--pair-peer-pub <控制端公钥>   ← 直连必须**双向**配对'
        Write-Note '       中继路径由 registry 帮两边互存公钥；直连没有这个中介，'
        Write-Note '       少一边就会在 SIGMA 之前被对端直接断开（表现为 transport 被中止）'
        Write-Note '  · transport 超时/被中止 → 查被控端端口防火墙规则与 --enable-direct'
        Write-Note '  · sigma 失败/超时     → 查两端时钟，以及配对是否已被撤销'
        return
    }

    Start-Sleep -Milliseconds 500
    Write-Info '拨号后的会话状态：'
    Format-SessionStatus (Get-SessionStatus)
    Write-Note '注意：direct_active_sessions > 0 只说明加密会话已建立；'
    Write-Note '画面是否出现、键鼠是否生效，仍需人工按 B1/B2 验收。'
}

# ──────────────────────────────────────────────────────────────────────────────
# 入口
# ──────────────────────────────────────────────────────────────────────────────

switch ($Mode) {
    'preflight' { Invoke-Preflight }
    'status'    { Invoke-Status }
    'watch'     { Invoke-Watch }
    'dial'      { Invoke-Dial }
}

if ($script:Failures -gt 0) {
    Write-Host ''
    Write-Bad "$($script:Failures) 项未就绪"
    exit 1
}
exit 0
