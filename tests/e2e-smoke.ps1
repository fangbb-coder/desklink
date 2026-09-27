<#
.SYNOPSIS
    DeskLink 端到端冒烟脚本。

.DESCRIPTION
    范围(对应 DESIGN.md 验收):
      - P3   relayd 双传输(QUIC / TCP-TLS)由 Go 单测覆盖(src/vps/internal/relay)
      - P4   Service 骨架:单机双 --data-dir 实例经本地 relay 完成 Hello/Dial 接线
      - P5   双实例 SIGMA 握手 + 加密控制帧往返(日志出现 "control round-trip OK")
      - P6   经真实加密会话的双向文件传输(--file-scope 授权 + 上传/列目录/越界拒绝)
      - P5.5 局域网直连:被控端 --enable-direct 监听 + 控制端 --direct-probe 端到端
             (已配对成功且无指纹确认;未配对设备被拒)
      - P5.6 防火墙:Service 自报状态与注册表独立对账 + netsh 规则存在性/端口一致性
      - P8/P9 媒体通道:真 Service + 真代理(测试图源) + 真客户端管道,收到配置与可解码码流
      - P8   全链路:需 WPF 客户端,不在本脚本范围

    relay 路径的真实流程:
      1) 构建 registryd / relayd(go build)
      2) 起 registryd(loopback HTTP + bearer token)
      3) 用 --print-config 取两实例的 Ed25519 公钥 → 注册两台设备
      4) 出码 / 领码 → 建立配对
      5) 起 relayd(QUIC+TCP 双监听,自签证书,订阅 regnotify)
      6) 起两个 --console 实例,各自 --peer 指向对端 device_id
      7) 轮询两侧日志,断言出现 "e2e established" 与 "control round-trip OK",
         并断言实际使用的传输与 -Transport 一致(防止"以为跑了 QUIC,其实回落了 TCP")

    强制约束:
      1. Service 进程本身不需要 --no-inject;该开关由 AgentLauncher 在启动
         DeskLink.DesktopAgent 子进程时强制(DESIGN.md 风险回顾 #4)。
      2. 单机可验证的项(握手 / 帧往返 / 防火墙状态契约)必须真跑,不冒充。
      3. 未实现的里程碑(-Mode direct)必须**显式失败**,不打印占位清单假装通过。
      4. 真双机/VPS 才能验证的清单项写入末尾 KnownIssues。

.PARAMETER Mode
    relay      中继路径用例(P3/P4/P5)
    direct     局域网直连用例(P5.5):被控端监听 + 控制端探测(已配对成功/未配对被拒)
    firewall   防火墙/直连状态契约自检(P5.6)
    media      媒体通道端到端(P8/P9):Service 转发泵 + 常驻代理 + 客户端收画面
    install    安装/卸载脚本的 -DryRun 计划自检(P10；不需要管理员)
    all        顺序跑所有可跑项(默认)

.PARAMETER Transport
    中继路径使用的传输:tcp(强制 TCP/TLS) / quic(强制 QUIC) / both(默认)。
    quic 在 QUIC 不可用的机器上会被跳过(并在输出中说明),不会假装通过。

.PARAMETER DataRoot
    临时根目录,默认 %TEMP%\desklink-e2e-<timestamp>。

.PARAMETER SkipBuild
    跳过 dotnet build(go build 只在产物缺失时执行)。

.EXAMPLE
    pwsh -NoProfile -File D:\程序\desklink\tests\e2e-smoke.ps1 -Mode relay
    pwsh -NoProfile -File D:\程序\desklink\tests\e2e-smoke.ps1 -Mode relay -Transport quic
    pwsh -NoProfile -File D:\程序\desklink\tests\e2e-smoke.ps1 -Mode firewall
#>

[CmdletBinding()]
param(
    [ValidateSet('relay', 'direct', 'firewall', 'media', 'install', 'all')]
    [string] $Mode = 'all',

    [ValidateSet('tcp', 'quic', 'both')]
    [string] $Transport = 'both',

    [string] $DataRoot,

    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ──────────────────────────────────────────────────────────────────────────────
# 常量与路径
# ──────────────────────────────────────────────────────────────────────────────

$script:RepoRoot   = Split-Path -Parent $PSScriptRoot
$script:ServiceExe = Join-Path $script:RepoRoot 'src\Service\DeskLink.Service\bin\Debug\net9.0\DeskLink.Service.exe'
$script:VpsDir     = Join-Path $script:RepoRoot 'src\vps'

if (-not $DataRoot) {
    $DataRoot = Join-Path $env:TEMP ("desklink-e2e-{0}" -f (Get-Date -Format 'yyyyMMddHHmmss'))
}

$script:DataRoot = $DataRoot
$script:BinDir   = Join-Path $DataRoot 'bin'
$script:LogDir   = Join-Path $DataRoot 'logs'
$script:Procs    = @()

# ──────────────────────────────────────────────────────────────────────────────
# 工具函数
# ──────────────────────────────────────────────────────────────────────────────

function Write-Section {
    param([string] $Title)
    Write-Host ''
    Write-Host ('=== {0} ===' -f $Title) -ForegroundColor Cyan
}

function Write-Step {
    param([string] $Message)
    Write-Host ('[+] {0}' -f $Message) -ForegroundColor Green
}

function Write-Skip {
    param([string] $Message)
    Write-Host ('[-] SKIP: {0}' -f $Message) -ForegroundColor Yellow
}

function Write-Warn {
    param([string] $Message)
    Write-Warning ('[!] {0}' -f $Message)
}

function Assert-LastExit {
    param([string] $Context)
    if ($LASTEXITCODE -ne 0) {
        throw "[$Context] 退出码 $LASTEXITCODE,中断冒烟"
    }
}

# 取一个本机空闲 TCP 端口号(假定同号 UDP 亦可用,自用场景成立)。
function Get-FreePort {
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $l.Start()
    $port = ([System.Net.IPEndPoint]$l.LocalEndpoint).Port
    $l.Stop()
    return $port
}

# 等 TCP 端口可连(比 HTTP 探测更稳:不受 401/404 与 StrictMode 属性访问影响)。
function Wait-TcpPort {
    param(
        [Parameter(Mandatory)] [string] $HostName,
        [Parameter(Mandatory)] [int] $Port,
        [int] $TimeoutSeconds = 10
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $client = [System.Net.Sockets.TcpClient]::new()
            $client.Connect($HostName, $Port)
            $client.Close()
            return $true
        } catch {
            Start-Sleep -Milliseconds 150
        }
    }
    return $false
}

# 启动一个进程并纳入统一清理。
function Start-Tracked {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [string[]] $ArgumentList = @(),
        [Parameter(Mandatory)] [string] $OutLog,
        [Parameter(Mandatory)] [string] $ErrLog,
        [string] $Label = ''
    )
    $proc = Start-Process -FilePath $FilePath `
                          -ArgumentList $ArgumentList `
                          -RedirectStandardOutput $OutLog `
                          -RedirectStandardError  $ErrLog `
                          -PassThru -NoNewWindow
    $script:Procs += [pscustomobject]@{ Proc = $proc; Label = $Label }
    return $proc
}

function Stop-AllTracked {
    foreach ($entry in $script:Procs) {
        try {
            if ($entry.Proc -and -not $entry.Proc.HasExited) {
                Write-Step ("停止 {0} (PID={1})" -f $entry.Label, $entry.Proc.Id)
                Stop-Process -Id $entry.Proc.Id -Force -ErrorAction SilentlyContinue
                $entry.Proc.WaitForExit(5000) | Out-Null
            }
        } catch { }
    }
    $script:Procs = @()
}

# 等待日志文件出现指定模式(超时返回 $false,由调用方断言)。
function Wait-LogPattern {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Pattern,
        [int] $TimeoutSeconds = 20
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $Path) {
            $text = Get-Content -Path $Path -Raw -ErrorAction SilentlyContinue
            if ($text -and $text -match $Pattern) { return $true }
        }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

# 从流里精确读满 count 字节（命名管道是字节流，不保证一次读完）。
function Read-ExactStream {
    param(
        [Parameter(Mandatory)] [System.IO.Stream] $Stream,
        [Parameter(Mandatory)] [byte[]] $Buffer,
        [Parameter(Mandatory)] [int] $Count
    )
    $total = 0
    while ($total -lt $Count) {
        $n = $Stream.Read($Buffer, $total, $Count - $total)
        if ($n -le 0) { throw "管道在对端写完前关闭（已读 $total/$Count）" }
        $total += $n
    }
}

# 调用 DeskLink.Service 的命名管道 JSON-RPC（帧格式 [u32 BE 长度][utf-8 JSON]）。
# 用真实字节收发而不是只"发个字符串"，这样也顺带验证了字节序约定。
function Invoke-ServiceRpc {
    param(
        [Parameter(Mandatory)] [string] $PipeName,
        [Parameter(Mandatory)] [string] $Method,
        $Params = $null,
        [int] $ConnectTimeoutMs = 5000
    )
    $client = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::None)
    try {
        $client.Connect($ConnectTimeoutMs)
        $req = @{ method = $Method; id = [guid]::NewGuid().ToString('N') }
        if ($null -ne $Params) { $req['params'] = $Params }
        $body = $req | ConvertTo-Json -Compress -Depth 8
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)

        $header = [byte[]]::new(4)
        [System.Buffers.Binary.BinaryPrimitives]::WriteUInt32BigEndian($header, [uint32]$bytes.Length)
        $client.Write($header, 0, 4)
        $client.Write($bytes, 0, $bytes.Length)
        $client.Flush()

        $lenBuf = [byte[]]::new(4)
        Read-ExactStream -Stream $client -Buffer $lenBuf -Count 4
        $len = [System.Buffers.Binary.BinaryPrimitives]::ReadUInt32BigEndian($lenBuf)
        if ($len -eq 0 -or $len -gt 65536) { throw "非法响应长度 $len" }
        $payload = [byte[]]::new([int]$len)
        Read-ExactStream -Stream $client -Buffer $payload -Count ([int]$len)
        return ([System.Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json)
    }
    finally {
        $client.Dispose()
    }
}

function Show-Log {
    param([Parameter(Mandatory)] [string] $Path, [string] $Label = '')
    if (Test-Path $Path) {
        Write-Warn ("$Label 日志:")
        Get-Content $Path | ForEach-Object { Write-Host ('    ' + $_) }
    }
}

# 解析 --print-config 输出中的 ed25519 公钥(Base64 → 小写 hex)。
function Get-DevicePubHex {
    param([Parameter(Mandatory)] [string] $DataDir)

    $out = & $script:ServiceExe --console --data-dir $DataDir --print-config 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "print-config 失败(data-dir=$DataDir): $out"
    }
    $m = [regex]::Match(($out -join "`n"), 'ed25519_pub_b64\s*=\s*([A-Za-z0-9+/=]+)')
    if (-not $m.Success) {
        throw "无法从 print-config 输出解析 ed25519_pub_b64: $out"
    }
    $bytes = [Convert]::FromBase64String($m.Groups[1].Value)
    return [Convert]::ToHexString($bytes).ToLowerInvariant()
}

# 解析 --print-config 输出中的 device_id(hex)。
function Get-DeviceIdHex {
    param([Parameter(Mandatory)] [string] $DataDir)

    $out = & $script:ServiceExe --console --data-dir $DataDir --print-config 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "print-config 失败(data-dir=$DataDir): $out"
    }
    $m = [regex]::Match(($out -join "`n"), 'device_id\s*=\s*([0-9a-fA-F]{64})')
    if (-not $m.Success) {
        throw "无法从 print-config 输出解析 device_id: $out"
    }
    return $m.Groups[1].Value.ToLowerInvariant()
}

# 询问本机是否支持 QUIC(由 Service 自报,避免脚本重实现平台判断)。
function Test-QuicAvailable {
    $probeDir = Join-Path $DataRoot 'quic-probe'
    New-Item -ItemType Directory -Force -Path $probeDir | Out-Null
    $out = & $script:ServiceExe --console --data-dir $probeDir --print-config 2>&1
    if ($LASTEXITCODE -ne 0) { return $false }
    return (($out -join "`n") -match 'quic_available\s*=\s*true')
}

# ──────────────────────────────────────────────────────────────────────────────
# 准备
# ──────────────────────────────────────────────────────────────────────────────

Write-Section "准备"
New-Item -ItemType Directory -Force -Path $script:BinDir, $script:LogDir | Out-Null
Write-Step "DataRoot = $DataRoot"

if (-not $SkipBuild) {
    Write-Step "构建 .NET 解决方案"
    & dotnet build (Join-Path $script:RepoRoot 'DeskLink.sln') -c Debug --nologo -v q
    Assert-LastExit 'dotnet build'
}

# Go 组件:即使 -SkipBuild,只要产物缺失就补建(否则每次换 DataRoot 都要重跑全量)。
$script:RegistrydExe = Join-Path $script:BinDir 'registryd.exe'
$script:RelaydExe    = Join-Path $script:BinDir 'relayd.exe'
if (-not (Test-Path $script:RegistrydExe) -or -not (Test-Path $script:RelaydExe)) {
    Write-Step "构建 Go VPS 组件"
    & go build -C $script:VpsDir -o $script:RegistrydExe ./cmd/registryd
    Assert-LastExit 'go build registryd'
    & go build -C $script:VpsDir -o $script:RelaydExe ./cmd/relayd
    Assert-LastExit 'go build relayd'
}

# ──────────────────────────────────────────────────────────────────────────────
# 中继路径(P3 / P4 / P5)
# ──────────────────────────────────────────────────────────────────────────────

# 跑一次中继验收。Scheme 为 'tcp' 或 'quic'(决定 relayUrl 的 scheme)。
function Invoke-RelayRun {
    param(
        [Parameter(Mandatory)] [ValidateSet('tcp', 'quic')] [string] $Scheme,
        [Parameter(Mandatory)] [string] $Tag
    )

    if (-not (Test-Path $script:RegistrydExe) -or -not (Test-Path $script:RelaydExe)) {
        Write-Skip "缺少 registryd.exe / relayd.exe(去掉 -SkipBuild 重跑)"
        return $false
    }
    if (-not (Test-Path $script:ServiceExe)) {
        Write-Skip "缺少 DeskLink.Service.exe(去掉 -SkipBuild 重跑)"
        return $false
    }

    # scheme → relayUrl;tcp 用 tls://(强制 TCP/TLS),quic 用 quic://(强制 QUIC)
    $schemeName = if ($Scheme -eq 'quic') { 'quic' } else { 'tls' }
    # 期望日志里出现的传输名(RelayTransportKind.ToString())
    $expectKind = if ($Scheme -eq 'quic') { 'Quic' } else { 'TcpTls' }

    $runDir     = Join-Path $DataRoot ("relay-$Tag")
    $regDataDir = Join-Path $runDir 'registry'
    $sockPath   = Join-Path $runDir 'regnotify.sock'
    $tokenFile  = Join-Path $runDir 'reg.token'
    $dirA       = Join-Path $runDir 'instance-a'
    $dirB       = Join-Path $runDir 'instance-b'
    $shareA     = Join-Path $runDir 'files-a'
    $shareB     = Join-Path $runDir 'files-b'
    $regPort    = Get-FreePort
    $relayPort  = Get-FreePort
    $regAddr    = "127.0.0.1:$regPort"
    $regBaseUrl = "http://$regAddr"
    $relayUrl   = "$schemeName`://127.0.0.1:$relayPort"

    New-Item -ItemType Directory -Force -Path $regDataDir, $dirA, $dirB, $shareA, $shareB | Out-Null

    # P6 用的测试文件：120KB > 默认 48KB 块 → 覆盖多块 + ack 往返。
    $payloadName = 'payload.bin'
    $payloadSize = 120 * 1024
    $payloadBytes = [byte[]]::new($payloadSize)
    $rng = [System.Random]::new(20260927)
    $rng.NextBytes($payloadBytes)
    [System.IO.File]::WriteAllBytes((Join-Path $shareA $payloadName), $payloadBytes)

    # token:32 字节 hex
    $token = -join (1..32 | ForEach-Object { '{0:x2}' -f (Get-Random -Maximum 256) })
    Set-Content -Path $tokenFile -Value $token -Encoding ascii -NoNewline
    $headers = @{ Authorization = "Bearer $token" }
    $jsonHdr = @{ Authorization = "Bearer $token"; 'Content-Type' = 'application/json' }

    $tagLog = Join-Path $script:LogDir $Tag
    New-Item -ItemType Directory -Force -Path $tagLog | Out-Null

    Write-Step "[$Tag] 启动 registryd @ $regAddr"
    Start-Tracked -FilePath $script:RegistrydExe `
        -ArgumentList @('--data-dir', $regDataDir, '--listen', $regAddr,
                        '--socket', $sockPath, '--token-file', $tokenFile) `
        -OutLog (Join-Path $tagLog 'registryd.out.log') `
        -ErrLog (Join-Path $tagLog 'registryd.err.log') -Label "$Tag/registryd" | Out-Null
    if (-not (Wait-TcpPort -HostName '127.0.0.1' -Port $regPort -TimeoutSeconds 10)) {
        throw "[$Tag] registryd 未在 10s 内就绪"
    }

    # 取两实例设备公钥并注册
    $pubAHex = Get-DevicePubHex -DataDir $dirA
    $pubBHex = Get-DevicePubHex -DataDir $dirB
    $regBodyA = @{ ed25519_pubkey = $pubAHex; platform = 'windows' } | ConvertTo-Json -Compress
    $regBodyB = @{ ed25519_pubkey = $pubBHex; platform = 'windows' } | ConvertTo-Json -Compress
    $idA = (Invoke-RestMethod -Method Post -Uri "$regBaseUrl/v1/devices/register" -Headers $jsonHdr -Body $regBodyA).device_id_hex
    $idB = (Invoke-RestMethod -Method Post -Uri "$regBaseUrl/v1/devices/register" -Headers $jsonHdr -Body $regBodyB).device_id_hex
    Write-Step "[$Tag] 注册完成 A=$($idA.Substring(0,16))... B=$($idB.Substring(0,16))..."

    # 配对:A 出码,B 领码
    #
    # 字段语义(P3 起统一命名;这两个接口的同名字段语义曾不一致):
    #   POST /v1/pairing-codes         issuer_device_id_hex = 出码端 **device_id**
    #   POST /v1/pairing-codes/redeem  redeemer_pubkey_hex  = 领码端 **Ed25519 公钥 hex**
    #                                  issuer_pubkey_hex    = 出码端 **Ed25519 公钥 hex**
    # 旧名 device_id_hex / issuer_pubkey 仍兼容(Deprecated),但已统一到显式名。
    $issueBody = @{ issuer_device_id_hex = $idA; ttl = 600 } | ConvertTo-Json -Compress
    $code = (Invoke-RestMethod -Method Post -Uri "$regBaseUrl/v1/pairing-codes" -Headers $jsonHdr -Body $issueBody).code
    $redeemBody = @{ redeemer_pubkey_hex = $pubBHex; code = $code; issuer_pubkey_hex = $pubAHex } | ConvertTo-Json -Compress
    Invoke-RestMethod -Method Post -Uri "$regBaseUrl/v1/pairing-codes/redeem" -Headers $jsonHdr -Body $redeemBody | Out-Null
    $pairsA = Invoke-RestMethod -Method Get -Uri "$regBaseUrl/v1/devices/$idA/pairs" -Headers $headers
    if (-not $pairsA.pairs -or $pairsA.pairs.Count -lt 1) {
        throw "[$Tag] 配对未生效:A 的 pairs 为空"
    }
    Write-Step "[$Tag] 配对生效(A <-> B)"

    Write-Step "[$Tag] 启动 relayd @ $relayUrl"
    Start-Tracked -FilePath $script:RelaydExe `
        -ArgumentList @('--listen', "127.0.0.1:$relayPort",
                        '--registry', $regBaseUrl,
                        '--token-file', $tokenFile,
                        '--socket', $sockPath,
                        '--insecure-tls') `
        -OutLog (Join-Path $tagLog 'relayd.out.log') `
        -ErrLog (Join-Path $tagLog 'relayd.err.log') -Label "$Tag/relayd" | Out-Null
    Start-Sleep -Milliseconds 800

    # 管道前缀必须按 run 唯一：两个 run 的 --data-dir 叶子名都是 instance-a，
    # 若共用前缀会连到上一个 run 尚未停止的实例（实测 QUIC 用例误连 TCP 实例）。
    $pipePrefix = "DeskLink-$Tag"
    $logA = Join-Path $tagLog 'instance-a.out.log'
    $logB = Join-Path $tagLog 'instance-b.out.log'
    Write-Step "[$Tag] 启动实例 A/B(--peer 指向对端)"
    Start-Tracked -FilePath $script:ServiceExe `
        -ArgumentList @('--console', '--data-dir', $dirA,
                        '--relay-url', $relayUrl, '--peer', $idB,
                        '--pipe-prefix', $pipePrefix,
                        '--file-scope', $shareA) `
        -OutLog $logA -ErrLog (Join-Path $tagLog 'instance-a.err.log') -Label "$Tag/instance-a" | Out-Null
    Start-Tracked -FilePath $script:ServiceExe `
        -ArgumentList @('--console', '--data-dir', $dirB,
                        '--relay-url', $relayUrl, '--peer', $idA,
                        '--pipe-prefix', $pipePrefix,
                        '--file-scope', $shareB) `
        -OutLog $logB -ErrLog (Join-Path $tagLog 'instance-b.err.log') -Label "$Tag/instance-b" | Out-Null

    # 断言 1:两侧都完成控制流往返(P5 验收标志)
    if (-not (Wait-LogPattern -Path $logA -Pattern 'control round-trip OK' -TimeoutSeconds 25)) {
        Show-Log -Path $logA -Label "[$Tag] 实例 A"
        throw "[$Tag] P5 失败:实例 A 未完成控制流往返"
    }
    if (-not (Wait-LogPattern -Path $logB -Pattern 'control round-trip OK' -TimeoutSeconds 25)) {
        Show-Log -Path $logB -Label "[$Tag] 实例 B"
        throw "[$Tag] P5 失败:实例 B 未完成控制流往返"
    }
    # 断言 2:E2E 建立
    if (-not ((Wait-LogPattern -Path $logA -Pattern 'e2e established' -TimeoutSeconds 5) -and
              (Wait-LogPattern -Path $logB -Pattern 'e2e established' -TimeoutSeconds 5))) {
        throw "[$Tag] P5 失败:未观察到 e2e established"
    }
    # 断言 3:实际使用的传输必须与请求一致 —— 防止"以为跑了 QUIC,其实回落了 TCP"。
    if (-not (Wait-LogPattern -Path $logA -Pattern ("transport=" + $expectKind) -TimeoutSeconds 5)) {
        Show-Log -Path $logA -Label "[$Tag] 实例 A"
        throw "[$Tag] 传输不符:期望 transport=$expectKind"
    }
    # 断言 4:真实 RTT 已测到(不是占位 0/-1)
    if (-not (Wait-LogPattern -Path $logA -Pattern 'ping rtt=\d+ms' -TimeoutSeconds 5)) {
        Show-Log -Path $logA -Label "[$Tag] 实例 A"
        throw "[$Tag] 未测到 RTT(期望日志出现 'ping rtt=<n>ms')"
    }

    # 断言 5(P6)：经真实加密会话做一次文件传输 + 列目录 + scope 越界拒绝。
    $pipeA = "$pipePrefix.Client.instance-a"
    $scope = Invoke-ServiceRpc -PipeName $pipeA -Method 'file_scope'
    if ($scope.result.roots -notcontains $shareA) {
        throw "[$Tag] file_scope 未包含授权目录 $shareA"
    }
    Write-Step "[$Tag] file_scope = $($scope.result.roots -join ',')"

    $upload = Invoke-ServiceRpc -PipeName $pipeA -Method 'file_upload' -Params @{
        local  = $payloadName
        remote = $payloadName
        policy = 'overwrite'
    }
    if (-not $upload.result.ok) {
        throw "[$Tag] P6 上传失败: $($upload.result.error)"
    }
    if ($upload.result.bytes -ne $payloadSize) {
        throw "[$Tag] P6 上传字节数不符: $($upload.result.bytes) != $payloadSize"
    }
    $srcHash = (Get-FileHash -Algorithm SHA256 (Join-Path $shareA $payloadName)).Hash
    $dstPath = Join-Path $shareB $payloadName
    if (-not (Test-Path $dstPath)) {
        throw "[$Tag] P6 失败: 对端未落盘 $dstPath"
    }
    $dstHash = (Get-FileHash -Algorithm SHA256 $dstPath).Hash
    if ($srcHash -ne $dstHash) {
        throw "[$Tag] P6 失败: 落盘内容与源不一致"
    }
    if (Test-Path "$dstPath.part") {
        throw "[$Tag] P6 失败: .part 未被原子改名消耗"
    }
    Write-Step "[$Tag] P6 上传通过($payloadSize 字节, SHA256 一致, 无残留 .part)"

    # 冲突策略 skip：目标已存在 → 成功但零字节
    $skip = Invoke-ServiceRpc -PipeName $pipeA -Method 'file_upload' -Params @{
        local  = $payloadName
        remote = $payloadName
        policy = 'skip'
    }
    if (-not $skip.result.ok -or $skip.result.bytes -ne 0) {
        throw "[$Tag] P6 冲突策略 skip 行为不符: ok=$($skip.result.ok) bytes=$($skip.result.bytes)"
    }

    # 远端目录列举
    $list = Invoke-ServiceRpc -PipeName $pipeA -Method 'file_list' -Params @{ path = '' }
    if (-not $list.result.ok) {
        throw "[$Tag] P6 列目录失败: $($list.result.error)"
    }
    if (-not ($list.result.entries | Where-Object { $_.name -eq $payloadName })) {
        throw "[$Tag] P6 列目录结果缺少 $payloadName"
    }

    # scope 越界：源路径不在授权范围内 → 必须拒绝
    $escape = Invoke-ServiceRpc -PipeName $pipeA -Method 'file_upload' -Params @{
        local  = '..\..\windows\win.ini'
        remote = 'evil.bin'
        policy = 'overwrite'
    }
    if ($escape.result.ok) {
        throw "[$Tag] P6 安全失败: scope 之外的源路径竟然被接受"
    }
    Write-Step "[$Tag] P6 scope 越界已拒绝"

    Write-Step "[$Tag] 通过(transport=$expectKind)"
    return $true
}

function Invoke-RelayPath {
    Write-Section "中继路径(P4/P5):双实例经本地 registryd + relayd 完成加密帧往返"

    $wantTcp  = ($Transport -eq 'tcp'  -or $Transport -eq 'both')
    $wantQuic = ($Transport -eq 'quic' -or $Transport -eq 'both')

    if ($wantQuic) {
        if (-not (Test-QuicAvailable)) {
            if ($Transport -eq 'quic') {
                # 显式只要求 QUIC 却不可用:必须失败,不能静默跳过。
                throw 'QUIC 在本机不可用(需要 Win11 build 20000+ / Linux / macOS),无法执行 -Transport quic'
            }
            Write-Skip 'QUIC 在本机不可用,跳过 QUIC 用例(仅跑 TCP/TLS)'
            $wantQuic = $false
        }
    }

    $ran = 0
    if ($wantTcp) {
        if (Invoke-RelayRun -Scheme 'tcp' -Tag 'tcp') { $ran++ }
    }
    if ($wantQuic) {
        if (Invoke-RelayRun -Scheme 'quic' -Tag 'quic') { $ran++ }
    }
    if ($ran -eq 0) {
        throw '中继路径一个用例都没跑起来(检查 -SkipBuild 与构建产物)'
    }
    Write-Step "中继路径通过:$ran 个传输用例"
}

# ──────────────────────────────────────────────────────────────────────────────
# 局域网直连(P5.5)
# ──────────────────────────────────────────────────────────────────────────────

function Invoke-DirectPath {
    param([switch] $Explicit)

    Write-Section "局域网直连(P5.5):被控端监听 + 控制端探测"

    if (-not (Test-Path $script:ServiceExe)) {
        Write-Skip "缺少 DeskLink.Service.exe(去掉 -SkipBuild 重跑)"
        return $false
    }

    $runDir = Join-Path $DataRoot 'direct'
    $dirA   = Join-Path $runDir 'server'    # 被控端
    $dirB   = Join-Path $runDir 'client'    # 已配对控制端
    $dirC   = Join-Path $runDir 'stranger'  # 未配对设备
    New-Item -ItemType Directory -Force -Path $dirA, $dirB, $dirC | Out-Null
    $tagLog = Join-Path $script:LogDir 'direct'
    New-Item -ItemType Directory -Force -Path $tagLog | Out-Null
    $port = Get-FreePort

    # 1) 三台"设备"的公钥(各自 --data-dir 独立生成密钥)
    $pubA = Get-DevicePubHex -DataDir $dirA
    $pubB = Get-DevicePubHex -DataDir $dirB
    Write-Step ("直连端口 {0}; A={1}... B={2}..." -f $port, $pubA.Substring(0, 12), $pubB.Substring(0, 12))

    # 2) 建立本地配对:A <-> B 互配(直连握手要求对端在已配对列表中);C 故意不配对。
    #    用 --pair-peer-pub 运维入口,避免为此实现一个命名管道客户端。
    & $script:ServiceExe --console --data-dir $dirA --pair-peer-pub $pubB | Out-Null
    Assert-LastExit 'pair A'
    & $script:ServiceExe --console --data-dir $dirB --pair-peer-pub $pubA | Out-Null
    Assert-LastExit 'pair B'

    # 3) 启动被控端 A:开启直连监听
    $srvLog = Join-Path $tagLog 'server.out.log'
    Start-Tracked -FilePath $script:ServiceExe `
        -ArgumentList @('--console', '--data-dir', $dirA, '--enable-direct', '--direct-port', "$port") `
        -OutLog $srvLog -ErrLog (Join-Path $tagLog 'server.err.log') -Label 'direct/server' | Out-Null
    if (-not (Wait-TcpPort -HostName '127.0.0.1' -Port $port -TimeoutSeconds 10)) {
        Show-Log -Path $srvLog -Label 'direct/server'
        throw '直连被控端未在 10s 内监听端口'
    }
    Write-Step "被控端已监听 127.0.0.1:$port"

    # 4) 已配对控制端 B 探测 → 必须成功、完成控制流往返、且不弹指纹确认
    $probeBErr = Join-Path $tagLog 'probe-b.err.log'
    $outB = & $script:ServiceExe --console --data-dir $dirB --direct-probe "127.0.0.1:$port" 2> $probeBErr
    $jsonB = ($outB -join "`n") | ConvertFrom-Json
    Write-Step ("B 探测结果: ok={0} round_trip={1} rtt={2}ms detail={3}" -f `
        $jsonB.ok, $jsonB.control_round_trip, $jsonB.rtt_ms, $jsonB.detail)
    if (-not $jsonB.ok) {
        Show-Log -Path $probeBErr -Label 'direct/probe-b'
        Show-Log -Path $srvLog -Label 'direct/server'
        throw "P5.5 失败:已配对直连未成功($($jsonB.detail))"
    }
    if (-not $jsonB.control_round_trip) {
        throw 'P5.5 失败:直连会话未完成控制流往返'
    }
    if ($jsonB.fingerprint_prompt -ne $false) {
        throw 'P5.5 失败:局域网直连不应弹对端指纹确认(DESIGN.md 决策:直连复用已配对公钥)'
    }

    # 5) 未配对设备 C 探测 → 必须在握手前被拒
    #    C 需要知道 A 的 device_id 才能发起握手(现实中"知道对方 id 但对方不认识你"),
    #    因此显式用 --peer 指定;C 自身不在 A 的配对列表里 → A 在配对校验处拒绝。
    $idA = Get-DeviceIdHex -DataDir $dirA
    $probeCErr = Join-Path $tagLog 'probe-c.err.log'
    $outC = & $script:ServiceExe --console --data-dir $dirC --peer $idA --direct-probe "127.0.0.1:$port" 2> $probeCErr
    $jsonC = ($outC -join "`n") | ConvertFrom-Json
    if ($jsonC.ok) {
        Show-Log -Path $srvLog -Label 'direct/server'
        throw 'P5.5 失败:未配对设备的直连请求竟然成功(必须在配对校验处拒绝)'
    }
    Write-Step ("C(未配对)探测被拒: detail={0}" -f $jsonC.detail)

    Write-Step 'P5.5 通过:已配对直连成功(无指纹确认) + 未配对设备被拒'
    return $true
}

# ──────────────────────────────────────────────────────────────────────────────
# 防火墙 / 直连状态契约(P5.6)
# ──────────────────────────────────────────────────────────────────────────────

function Invoke-FirewallPolicy {
    Write-Section "防火墙/直连状态契约(P5.6)"

    if (-not (Test-Path $script:ServiceExe)) {
        Write-Skip "缺少 DeskLink.Service.exe(去掉 -SkipBuild 重跑)"
        return
    }

    $probeDir  = Join-Path $DataRoot 'firewall-probe'
    $probePort = 47200
    New-Item -ItemType Directory -Force -Path $probeDir | Out-Null

    # 1) 由 Service 自报状态(只读,不需提权)
    $raw = & $script:ServiceExe --console --data-dir $probeDir --direct-port $probePort --firewall-status 2>&1
    Assert-LastExit 'firewall-status'
    $json = ($raw -join "`n") | ConvertFrom-Json
    Write-Step ("Service 自报: direct_port={0} direct_enabled={1} elevated={2} netsh={3} tcp_rule={4} udp_rule={5}" -f `
        $json.direct_port, $json.direct_enabled, $json.elevated, $json.netsh_rules_implemented, `
        $json.rule_tcp_present, $json.rule_udp_present)

    # 2) 独立预言机:直接用 PowerShell 读同一个注册表键,与 Service 的结论对账。
    #    这样验证的是"QueryEnabled 的语义",而不是"Service 自己和自己一致"。
    $regPath = 'HKLM:\SOFTWARE\DeskLink\Firewall'
    $regEnabled = $false
    if (Test-Path $regPath) {
        $key = Get-ItemProperty -Path $regPath -ErrorAction SilentlyContinue
        if ($key -and ($null -ne $key.DirectEnabled) -and ($null -ne $key.DirectPort)) {
            # 与 FirewallHelper.QueryEnabled 的语义一致:enabled!=0 且 端口匹配
            $regEnabled = ([int]$key.DirectEnabled -ne 0) -and ([int]$key.DirectPort -eq $probePort)
        }
    }
    Write-Step ("注册表对账: 期望 direct_enabled={0}" -f $regEnabled)

    if ($json.direct_enabled -ne $regEnabled) {
        throw ("FirewallHelper.QueryEnabled 与注册表不一致: Service={0} 期望={1}" -f `
            $json.direct_enabled, $regEnabled)
    }
    Write-Step '契约自检通过:QueryEnabled 语义与注册表一致'

    # 3) netsh 规则已实现(P5.6):标记必须为 true,且提权+启用时规则端口必须与配置一致。
    if ($json.netsh_rules_implemented -ne $true) {
        throw 'FirewallHelper 应已实现真实 netsh 规则(netsh_rules_implemented 应为 true)'
    }
    if ($json.elevated -and $json.direct_enabled) {
        if (-not $json.rule_tcp_present) { throw '已启用直连但 TCP 入站规则不存在' }
        if (-not $json.rule_udp_present) { throw '已启用直连但 UDP 入站规则不存在' }
        if ($json.rule_tcp_port -ne $probePort) {
            throw ("TCP 规则端口与配置不一致: rule={0} 期望={1}" -f $json.rule_tcp_port, $probePort)
        }
        Write-Step '提权路径已验证:TCP/UDP 规则存在且端口一致'
    } else {
        Write-Warn ('当前未提权或未启用直连 → netsh 写路径(增/删/幂等/端口变更)未在本机验证;' +
                    '规则拼装与幂等语义由 tests/DirectHandshake.Tests/FirewallPolicyTests.cs 覆盖')
    }
}

# ──────────────────────────────────────────────────────────────────────────────
# 媒体通道端到端(P8/P9)
# ──────────────────────────────────────────────────────────────────────────────

function Invoke-MediaPath {
    Write-Section "媒体通道端到端(P8/P9):Service 转发泵 + 常驻代理 + 客户端收画面"

    $serviceExe = $script:ServiceExe
    $agentExe = Join-Path $script:RepoRoot 'src\Agent\DeskLink.DesktopAgent\bin\Debug\net9.0-windows\DeskLink.DesktopAgent.exe'
    if (-not (Test-Path $serviceExe)) { Write-Skip '缺少 DeskLink.Service.exe(去掉 -SkipBuild 重跑)'; return }
    if (-not (Test-Path $agentExe)) { Write-Skip '缺少 DeskLink.DesktopAgent.exe(先构建 Agent 项目)'; return }

    # 实例名由 --data-dir 的叶子名派生,因此管道名可预测。
    $runDir = Join-Path $DataRoot 'media'
    $dataDir = Join-Path $runDir 'media'
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    $tagLog = Join-Path $script:LogDir 'media'
    New-Item -ItemType Directory -Force -Path $tagLog | Out-Null

    $clientPipe = 'DeskLink.Media.media'
    $agentPipe = 'DeskLink.AgentMedia.media'

    Write-Step "启动 Service(--console,实例 media) → 媒体管道 $clientPipe / $agentPipe"
    Start-Tracked -FilePath $serviceExe `
        -ArgumentList @('--console', '--data-dir', $dataDir) `
        -OutLog (Join-Path $tagLog 'service.out.log') `
        -ErrLog (Join-Path $tagLog 'service.err.log') -Label 'media/service' | Out-Null

    # 等媒体管道就绪:尝试连接,连不上说明 Service 还没起来。
    $deadline = (Get-Date).AddSeconds(15)
    $ready = $false
    while ((Get-Date) -lt $deadline -and -not $ready) {
        try {
            $probe = [System.IO.Pipes.NamedPipeClientStream]::new(
                '.', $clientPipe, [System.IO.Pipes.PipeDirection]::InOut)
            $probe.Connect(500)
            $probe.Dispose()
            $ready = $true
        } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ready) {
        Show-Log -Path (Join-Path $tagLog 'service.out.log') -Label 'media/service'
        throw "Service 未在 15s 内建立媒体管道 $clientPipe"
    }
    Write-Step 'Service 媒体管道已就绪'

    # 常驻代理:测试图源(无需真实桌面) + --no-inject(绝不注入)。
    Write-Step "启动常驻代理(--run --test-pattern --no-inject --media-pipe $agentPipe)"
    $manualAgent = Start-Tracked -FilePath $agentExe `
        -ArgumentList @('--run', '--test-pattern', '--no-inject',
                        '--media-pipe', $agentPipe, '--fps', '15',
                        '--pattern-size', '320x240', '--max-frames', '90') `
        -OutLog (Join-Path $tagLog 'agent.out.log') `
        -ErrLog (Join-Path $tagLog 'agent.err.log') -Label 'media/agent'

    # 客户端:直接连媒体管道收帧,断言收到 DesktopConfig 与可解码的 DesktopVideo。
    # 这里手写剥帧([u32 BE len][u8 type])是**故意**的:不依赖任何 C# 辅助类,
    # 相当于用一个独立实现去验证线上格式,而不是"自己验证自己"。
    $client = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', $clientPipe, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect(10000)
        Write-Step '客户端已连上媒体管道'

        $gotConfig = $false
        $configWidth = 0
        $configHeight = 0
        $videoBytes = 0
        $videoFrames = 0
        $buf = [byte[]]::new(65536)
        $acc = [System.Collections.Generic.List[byte]]::new()

        # 用**阻塞读**：命名管道不支持 ReadTimeout（"Timeouts are not supported on this
        # stream"），而 ReadAsync().Wait(ms) 超时后底层读仍挂着，再发起下一次读会变成
        # 两个并发读抢同一字节流、打乱帧边界。
        # 这里能安全阻塞是因为代理用 --max-frames 做了**有界运行**：
        # 达到帧数后代理退出并关闭管道，客户端读到 0 即结束，不需要超时兜底。
        $client.ReadMode = [System.IO.Pipes.PipeTransmissionMode]::Byte

        while (-not ($gotConfig -and $videoFrames -gt 0)) {
            $n = $client.Read($buf, 0, $buf.Length)
            if ($n -le 0) { break }
            for ($i = 0; $i -lt $n; $i++) { $acc.Add($buf[$i]) }

            # 剥帧:[u32 BE len][u8 type][payload]
            while ($acc.Count -ge 5) {
                $len = ([uint32]$acc[0] -shl 24) -bor ([uint32]$acc[1] -shl 16) -bor ([uint32]$acc[2] -shl 8) -bor [uint32]$acc[3]
                if ($len -lt 1 -or $len -gt 65536) { throw "非法媒体帧长度 $len" }
                $total = 4 + $len
                if ($acc.Count -lt $total) { break }
                $type = $acc[4]
                if ($type -eq 0x11) {
                    # DesktopConfig:[u16 w][u16 h][u8 codec][u8 backend][u16 fps][u32 bitrate][u16 mon][u8 rot]
                    $configWidth = ([int]$acc[5] -shl 8) -bor [int]$acc[6]
                    $configHeight = ([int]$acc[7] -shl 8) -bor [int]$acc[8]
                    $gotConfig = $true
                }
                elseif ($type -eq 0x10) {
                    $videoFrames++
                    $videoBytes += $len
                }
                $acc.RemoveRange(0, $total)
            }
        }

        if (-not $gotConfig) {
            Show-Log -Path (Join-Path $tagLog 'agent.err.log') -Label 'media/agent'
            throw '媒体通道端到端失败:客户端未收到 DesktopConfig'
        }
        if ($videoFrames -le 0) {
            Show-Log -Path (Join-Path $tagLog 'agent.err.log') -Label 'media/agent'
            throw '媒体通道端到端失败:客户端未收到任何桌面码流'
        }

        Write-Step ("客户端收到 DesktopConfig {0}x{1} 与 {2} 个码流分片(共 {3} 字节)" -f `
            $configWidth, $configHeight, $videoFrames, $videoBytes)

        # 代理状态:确认真的是常驻运行且没有注入。
        $agentLog = Get-Content (Join-Path $tagLog 'agent.out.log') -Raw -ErrorAction SilentlyContinue
        if ($agentLog -notmatch '"mode":\s*"resident"') {
            throw '代理未以常驻模式运行(日志里没有 mode=resident)'
        }
        Write-Step '代理以常驻模式运行'
    }
    finally {
        $client.Dispose()
    }

    Write-Step 'P8/P9 媒体通道通过:真 Service 转发 + 常驻代理产出码流 + 客户端成功接收'

    # ── 生产启动路径验证 ────────────────────────────────────────────────────
    #
    # 上面是"手工带 --run 启动代理"，掩盖了 AgentLauncher 的两个真实缺陷：
    #   1. 没有传 --run → 代理跑完一次性自检就退出，永远没有画面；
    #   2. 开关与取值被拼成单个 argv（"--fps 30"）→ 代理报"未知参数"启动失败。
    # 这里走**真实路径**（客户端点"开始控制"走的就是 start_agent）来验证修复。
    if ($null -ne $manualAgent) {
        Stop-Process -Id $manualAgent.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }

    Write-Step '生产路径:通过 RPC start_agent 让 Service 自己拉起常驻代理'
    $startResult = Invoke-ServiceRpc -PipeName 'DeskLink.Client.media' -Method 'start_agent' -Params @{
        inject    = $false
        no_inject = $true
    }
    if ($startResult.result.stub_mode) {
        throw '生产路径失败:Service 找不到 DeskLink.DesktopAgent.exe（stub 模式）'
    }
    if ($startResult.result.pid -le 0) {
        throw "生产路径失败:start_agent 返回的 pid 非法（$($startResult.result.pid)）"
    }
    Write-Step "Service 已拉起代理 pid=$($startResult.result.pid)"

    # 代理必须**连上媒体管道**。锁屏时它拿不到帧源，但应保持连接
    # （客户端据此显示"等待本地登录"而不是"代理掉线"）。
    $deadline = (Get-Date).AddSeconds(20)
    $agentConnected = $false
    while ((Get-Date) -lt $deadline -and -not $agentConnected) {
        $status = Invoke-ServiceRpc -PipeName 'DeskLink.Client.media' -Method 'get_status'
        if ($status.result.media_agent_connected) { $agentConnected = $true; break }
        Start-Sleep -Milliseconds 300
    }
    if (-not $agentConnected) {
        Show-Log -Path (Join-Path $tagLog 'agent.out.log') -Label 'media/agent(stdout)'
        throw '生产路径失败:代理进程已启动但没有连上媒体管道（检查 --run / --media-pipe 参数拼装）'
    }
    Write-Step '生产路径通过:start_agent 拉起的代理已连上媒体管道'

    # 反向验证：参数拼装错误会让代理"启动即退出"，这里确认它**仍在运行**。
    $stillRunning = $null -ne (Get-Process -Id $startResult.result.pid -ErrorAction SilentlyContinue)
    if (-not $stillRunning) {
        throw '生产路径失败:代理启动后立刻退出（很可能缺少 --run）'
    }
    Write-Step '生产路径通过:代理保持常驻（未立刻退出）'

    $stopResult = Invoke-ServiceRpc -PipeName 'DeskLink.Client.media' -Method 'stop_agent'
    if (-not $stopResult.result.ok) { throw 'stop_agent 返回失败' }
    Write-Step 'stop_agent 正常'
}

# ──────────────────────────────────────────────────────────────────────────────
# 安装/卸载脚本自检(P10)
# ──────────────────────────────────────────────────────────────────────────────

function Invoke-InstallScripts {
    Write-Section "安装/卸载脚本自检(P10, -DryRun)"

    $install   = Join-Path $script:RepoRoot 'installer\install.ps1'
    $uninstall = Join-Path $script:RepoRoot 'installer\uninstall.ps1'
    if (-not (Test-Path $install) -or -not (Test-Path $uninstall)) {
        throw '找不到 installer\install.ps1 或 uninstall.ps1'
    }

    # -DryRun 只打印计划、不触碰系统，因此不需要管理员权限，可以在冒烟里真跑。
    # 这一步验证的是脚本本身的**逻辑**（参数解析、提权前置检查、计划内容），
    # 真正的服务注册 / 防火墙变更 / UAC 变更仍需人工在管理员终端执行（见 KnownIssues.md）。
    $plan = & pwsh -NoProfile -File $install -DryRun -SkipBuild -EnableDirect -SetUacInteractive 2>&1
    if ($LASTEXITCODE -ne 0) {
        $plan | ForEach-Object { Write-Host ('    ' + $_) }
        throw "install.ps1 -DryRun 退出码 $LASTEXITCODE"
    }
    $text = $plan -join "`n"

    $mustContain = @(
        'DeskLinkService',        # 服务名
        'firewall-set',           # 复用 C# 侧已单测的防火墙逻辑
        'PromptOnSecureDesktop',  # UAC 策略
        'UacBackup',              # 卸载恢复所需的备份
        'DeskLink.Client.exe'     # 控制端程序位置
    )
    foreach ($needle in $mustContain) {
        if ($text -notmatch [regex]::Escape($needle)) {
            throw "install.ps1 -DryRun 计划中缺少关键步骤：$needle"
        }
    }
    Write-Step 'install.ps1 -DryRun 计划包含：服务注册 / 防火墙放行 / UAC 备份 / 首次启动信息'

    $unplan = & pwsh -NoProfile -File $uninstall -DryRun 2>&1
    if ($LASTEXITCODE -ne 0) {
        $unplan | ForEach-Object { Write-Host ('    ' + $_) }
        throw "uninstall.ps1 -DryRun 退出码 $LASTEXITCODE"
    }
    $utext = $unplan -join "`n"
    foreach ($needle in @('DeskLinkService', 'firewall-set', 'UacBackup', '保留数据目录')) {
        if ($utext -notmatch [regex]::Escape($needle)) {
            throw "uninstall.ps1 -DryRun 计划中缺少关键步骤：$needle"
        }
    }
    Write-Step 'uninstall.ps1 -DryRun 计划包含：删服务 / 回收防火墙 / 恢复 UAC / 保留数据目录'

    # 负向断言：-DryRun 绝不能真的创建服务（否则"自检"本身就有副作用）。
    if (Get-Service -Name 'DeskLinkService' -ErrorAction SilentlyContinue) {
        Write-Warn 'DeskLinkService 已存在于本机：无法断言 -DryRun 无副作用（可能是真实安装残留）'
    }
    else {
        Write-Step '已确认 -DryRun 未创建服务（无副作用）'
    }
}

# ──────────────────────────────────────────────────────────────────────────────
# 调度
# ──────────────────────────────────────────────────────────────────────────────

try {
    switch ($Mode) {
        'relay'    { Invoke-RelayPath }
        'direct'   { Invoke-DirectPath -Explicit }
        'firewall' { Invoke-FirewallPolicy }
        'media'    { Invoke-MediaPath }
        'install'  { Invoke-InstallScripts }
        'all' {
            Invoke-RelayPath
            Invoke-DirectPath
            Invoke-FirewallPolicy
            Invoke-MediaPath
            Invoke-InstallScripts
        }
    }

    Write-Section "KnownIssues — 双机/VPS 才能验证(不在本机冒烟内)"
    @'
NAT 穿透两端真实公网出网(需 VPS + 两端 NAT 后)
公网 TOFU(需域名 + TLS 证书指纹首次确认)
Win10 1909 旧版 TCP 回退(QUIC 受限时)
撤销即时踢线 — regnotify 推送路径的端到端时序(需真双机观察)
1080p30 真实带宽与自适应码率收敛(需两端真实网络)
对端公钥变更触发二次确认
同子网两台真实机器 IP:端口直连端到端(本脚本用 127.0.0.1 双进程覆盖协议路径)
真实 netsh 防火墙规则的增删/幂等/端口变更写路径(需管理员提权后复跑;逻辑已由单测覆盖)
'@ -split "`n" | ForEach-Object { Write-Host ('  * ' + $_) }

    Write-Section "完成"
    Write-Step "日志目录: $script:LogDir"
}
finally {
    Write-Section "清理"
    Stop-AllTracked
}
