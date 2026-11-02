param([switch]$Refresh)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=Join-Path $root 'src/Leveling/Multiverse.Core'
$native=Join-Path $root 'native'
$manifest=Join-Path $native 'manifest.json'
$files=@(Get-ChildItem $source -File | Where-Object { $_.Extension -in '.h','.cpp','.vcxproj' } | Sort-Object Name)
$hashes=[ordered]@{}
foreach($f in $files){
 # Git checkout may use CRLF or LF; hash the same canonical UTF-8 source on both machines.
 $text=[IO.File]::ReadAllText($f.FullName).Replace("`r`n","`n")
 $sha=[Security.Cryptography.SHA256]::Create()
 try {$hashes[$f.Name]=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-','')}
 finally {$sha.Dispose()}
}
if($Refresh){
 New-Item -ItemType Directory -Force $native | Out-Null
 Copy-Item -LiteralPath (Join-Path $source 'bin/x64/Release/Multiverse.Core.dll') -Destination $native
 @{sources=$hashes;binary=(Get-FileHash (Join-Path $native 'Multiverse.Core.dll')).Hash} | ConvertTo-Json -Depth 4 | Set-Content $manifest -Encoding utf8
}else{
 $m=Get-Content $manifest -Raw | ConvertFrom-Json
 if($m.binary -ne (Get-FileHash (Join-Path $native 'Multiverse.Core.dll')).Hash){throw 'Native binary checksum mismatch'}
 if(@($m.sources.PSObject.Properties).Count -ne $hashes.Count){throw 'Native source inventory changed: rebuild required'}
 foreach($k in $hashes.Keys){if($m.sources.$k -ne $hashes[$k]){throw "Native source changed: rebuild required: $k"}}
 Write-Output 'PASS native binary matches audited source inventory'
}
