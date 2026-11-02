param([string]$Peer)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $Peer) { $Peer = Read-Host 'IP Tailscale do outro PC (100.x.y.z)' }
$parsed = $null
if (-not [Net.IPAddress]::TryParse($Peer, [ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'Informe apenas o IPv4 do outro PC. Nenhum comando foi executado.'
}
$dir = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ('Leveling_Rede_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$report = [ordered]@{ format='leveling-network-diagnostics-v1'; utc=[DateTimeOffset]::UtcNow.ToString('o'); role='LOCAL_PC'; notes='Somente metricas; nenhum IP, nome, chave ou payload e gravado.' }
function Measure-Ping([string]$Target, [string]$Label) {
    $times = @(); $failures = 0
    $ping = [Net.NetworkInformation.Ping]::new()
    try {
        for ($i=0; $i -lt 20; $i++) {
            try {
                $r = $ping.Send($Target, 1000)
                if ($r.Status -eq [Net.NetworkInformation.IPStatus]::Success) { $times += [double]$r.RoundtripTime }
                else { $failures++ }
            } catch { $failures++ }
            Start-Sleep -Milliseconds 200
        }
    } finally { $ping.Dispose() }
    $sorted = @($times | Sort-Object)
    if ($sorted.Count -eq 0) { return @{ label=$Label; sent=20; received=0; timeouts=$failures } }
    return @{ label=$Label; sent=20; received=$sorted.Count; timeouts=$failures;
        medianMs=[Math]::Round([double]$sorted[[int][Math]::Floor(($sorted.Count-1)*.5)],2);
        p95Ms=[Math]::Round([double]$sorted[[int][Math]::Floor(($sorted.Count-1)*.95)],2);
        maxMs=$sorted[-1] }
}
function Measure-Tailscale([string]$Executable, [string]$Target, [string[]]$Flags, [string]$Label) {
    $lines = @(& $Executable ping @Flags $Target 2>$null | ForEach-Object { $_.ToString() })
    $code = $LASTEXITCODE
    $times=@(); $routes=@(); $failures=0
    foreach ($line in $lines) {
        if ($line -match 'in\s+([0-9]+(?:\.[0-9]+)?)ms') {
            $times += [double]::Parse($Matches[1],[Globalization.CultureInfo]::InvariantCulture)
            $route = if ($line -match 'DERP\(') {'DERP'} elseif ($line -match 'peer-relay') {'PEER_RELAY'} elseif ($line -match '\bvia\s+ICMP\b') {'ICMP'} elseif ($line -match '\bvia\s+TSMP\b') {'TSMP'} elseif ($line -match '\bvia\s+\d{1,3}(?:\.\d{1,3}){3}:\d+\b') {'DIRECT'} else {'UNKNOWN'}
            $routes += $route
        } elseif ($line -match 'timeout|timed out|no response|failed') { $failures++ }
    }
    $sorted=@($times|Sort-Object)
    $result=@{ label=$Label; exitCode=$code; received=$sorted.Count; failures=$failures; routes=@($routes) }
    if ($sorted.Count -gt 0) {
        $result.medianMs=[Math]::Round([double]$sorted[[int][Math]::Floor(($sorted.Count-1)*.5)],2)
        $result.p95Ms=[Math]::Round([double]$sorted[[int][Math]::Floor(($sorted.Count-1)*.95)],2)
        $result.maxMs=$sorted[-1]
    }
    return $result
}
try {
    $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop | Where-Object { $_.NextHop -ne '0.0.0.0' } | Sort-Object RouteMetric | Select-Object -First 1
    if ($route) {
        $adapter = Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction SilentlyContinue
        $report.interface = @{ linkSpeedBps = if ($adapter) { [long]$adapter.TransmitLinkSpeed } else { 0 }; status = if ($adapter) { [string]$adapter.Status } else { 'UNKNOWN' } }
        $report.gateway = Measure-Ping $route.NextHop 'ROTEADOR_LOCAL'
    }
} catch { $report.gatewayError=$_.Exception.GetType().Name }
$report.publicBaseline = Measure-Ping '1.1.1.1' 'INTERNET_ICMP'
$report.peerIcmp = Measure-Ping $Peer 'TAILSCALE_ICMP'
$ts = (Get-Command tailscale.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source)
if (-not $ts -and (Test-Path "$env:ProgramFiles\Tailscale\tailscale.exe")) { $ts = "$env:ProgramFiles\Tailscale\tailscale.exe" }
if ($ts) {
    try {
        $status = & $ts status --json 2>$null | ConvertFrom-Json
        $matched = @($status.Peer.PSObject.Properties.Value | Where-Object { $_.TailscaleIPs -contains $Peer }) | Select-Object -First 1
        if ($matched) {
            $routeKind = if ($matched.Relay) {'DERP'} elseif ($matched.CurAddr -match '^\S+:\d+$') {'DIRECT'} else {'UNKNOWN'}
            $report.tailscaleStatus = @{ active=[bool]$matched.Active; route=$routeKind }
        }
    } catch { $report.statusError=$_.Exception.GetType().Name }
    try {
        $netcheck = & $ts netcheck --format=json 2>$null | ConvertFrom-Json
        $report.netcheck = @{ udp=$netcheck.UDP; ipv4Available=[bool]$netcheck.IPv4; ipv6Available=[bool]$netcheck.IPv6;
            mappingVariesByDestIP=$netcheck.MappingVariesByDestIP; nearestDerp=$netcheck.NearestDERP }
    } catch { $report.netcheckError=$_.Exception.GetType().Name }
    $report.tailscaleDiscovery = Measure-Tailscale $ts $Peer @('--c=20','--until-direct=false','--timeout=2s') 'TAILSCALE_DISCOVERY'
    $report.tailscaleTunnel = Measure-Tailscale $ts $Peer @('--tsmp','--c=20','--until-direct=false','--timeout=2s') 'TAILSCALE_TUNNEL'
} else { $report.tailscaleError='CLI_NOT_FOUND' }
$path=Join-Path $dir 'rede.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
Write-Host "Diagnostico salvo: $path"
Write-Host 'Nenhuma configuracao de rede foi modificada. Pings leves nao medem capacidade UDP sob carga.'
Write-Host 'Compare os resultados dos dois PCs e envie apenas o JSON.'
