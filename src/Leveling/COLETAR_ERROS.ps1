param(
    [string]$AppDirectory = $PSScriptRoot,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Join-Path $env:LOCALAPPDATA 'Leveling\Diagnostics'
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root ('Exports\Diagnostico_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$errors = New-Object System.Collections.Generic.List[string]
function Copy-Recent([string]$Source, [string]$Target, [string]$Filter, [int]$Count, [long]$MaximumBytes) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { return }
    New-Item -ItemType Directory -Force -Path $Target | Out-Null
    Get-ChildItem -LiteralPath $Source -File -Filter $Filter -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First $Count | ForEach-Object {
            try {
                if ($_.Length -le $MaximumBytes) {
                    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Target $_.Name) -ErrorAction Stop
                }
            } catch { $errors.Add('Cópia: ' + $_.Exception.GetType().Name) }
        }
}
try {
    $AppDirectory = (Resolve-Path -LiteralPath $AppDirectory).Path
    Copy-Recent (Join-Path $root 'Startup') (Join-Path $OutputDirectory 'Startup') 'startup_*.log' 5 2097152
    Copy-Recent (Join-Path $root 'VideoIncidents') (Join-Path $OutputDirectory 'VideoIncidents') 'video_incident_*.jsonl' 5 4194304
    $names = @('Leveling.exe','Leveling.dll','Leveling.deps.json','Leveling.runtimeconfig.json','Microsoft.UI.Xaml.dll','Multiverse.Core.dll')
    $files = foreach ($name in $names) {
        $file = Join-Path $AppDirectory $name
        try {
            if (Test-Path -LiteralPath $file -PathType Leaf) {
                $item = Get-Item -LiteralPath $file
                [pscustomobject]@{ name=$name; bytes=$item.Length; sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
            } else { [pscustomobject]@{ name=$name; missing=$true } }
        } catch { [pscustomobject]@{ name=$name; error=$_.Exception.GetType().Name } }
    }
    $files | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'arquivos.json') -Encoding UTF8
    try {
        $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if ($dotnet) {
            $runtime = & $dotnet.Source --list-runtimes 2>&1
            if ($LASTEXITCODE -eq 0) { $runtime | Out-File -LiteralPath (Join-Path $OutputDirectory 'dotnet_runtimes.txt') -Encoding UTF8 }
            else { $errors.Add('dotnet --list-runtimes retornou ' + $LASTEXITCODE) }
        } else { $errors.Add('dotnet.exe não encontrado no PATH.') }
    } catch { $errors.Add('Runtime: ' + $_.Exception.GetType().Name) }
    $events = Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=(Get-Date).AddMinutes(-60) } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in @('.NET Runtime','Application Error','Windows Error Reporting') -and $_.Message -match 'Leveling(\.CoreRegression)?\.exe' } |
        Select-Object -First 12 TimeCreated,ProviderName,Id,Message
    $events | Format-List | Out-String -Width 240 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'eventos.txt') -Encoding UTF8
    $werTarget = Join-Path $OutputDirectory 'WER'
    New-Item -ItemType Directory -Force -Path $werTarget | Out-Null
    $werRoot = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\WER'
    $reports = @()
    foreach ($branch in @('ReportArchive','ReportQueue')) {
        $folder = Join-Path $werRoot $branch
        if (Test-Path -LiteralPath $folder) {
            $reports += @(Get-ChildItem -LiteralPath $folder -Recurse -File -Filter 'Report.wer' -ErrorAction SilentlyContinue)
        }
    }
    $index = 0
    foreach ($report in ($reports | Sort-Object LastWriteTimeUtc -Descending)) {
        if ($index -ge 7) { break }
        try {
            if ($report.Length -gt 2097152) { continue }
            $text = Get-Content -LiteralPath $report.FullName -Raw -ErrorAction Stop
            if ($text -notmatch '(?m)^(AppName|AppPath|AppPathUnexpanded|FriendlyEventName)=.*Leveling(\.CoreRegression)?\.exe(?:\s|$)') { continue }
            $index++
            Copy-Item -LiteralPath $report.FullName -Destination (Join-Path $werTarget ('Report_' + $index + '.wer'))
        } catch { $errors.Add('WER: ' + $_.Exception.GetType().Name) }
    }
    $check = Join-Path $AppDirectory 'CHECK_CLIENT.ps1'
    if (Test-Path -LiteralPath $check) {
        try {
            & $check -AppDirectory $AppDirectory -Role Package *>&1 | Out-String -Width 240 |
                Set-Content -LiteralPath (Join-Path $OutputDirectory 'verificacao_pacote.txt') -Encoding UTF8
        } catch {
            $errors.Add('Pacote: ' + $_.Exception.Message)
        }
    }
} catch { $errors.Add('Coleta: ' + $_.Exception.Message) }
@'
LEVELING 11.74c4 — DIAGNÓSTICO LOCAL
Os arquivos coletados podem conter caminhos de usuário, nomes de aplicativos e detalhes técnicos.
Revise antes de compartilhar. Não há envio automático pela rede.
Nenhum dump de memória, mídia, tecla, senha ou arquivo de configuração foi copiado intencionalmente.
Dumps podem conter dados privados e só devem ser compartilhados separadamente após revisão.
Esta coleta não instala software, não modifica a aplicação e não altera o registro do Windows.
'@ | Set-Content -LiteralPath (Join-Path $OutputDirectory 'LEIA_ANTES_DE_ENVIAR.txt') -Encoding UTF8
$errors | Set-Content -LiteralPath (Join-Path $OutputDirectory 'falhas_da_coleta.txt') -Encoding UTF8
Write-Host "Diagnóstico salvo: $OutputDirectory" -ForegroundColor Cyan
Write-Host 'Revise os arquivos antes de compartilhar. Nenhum dump foi incluído.'
