$ErrorActionPreference='Stop'
$p=Join-Path $PSScriptRoot '../src/Leveling/Leveling.csproj'
$s=Get-Content $p -Raw
$v=[regex]::Match($s,'<Version>([^<]+)</Version>').Groups[1].Value
if($v -match '^(\d+\.\d+\.\d+)-rc\.(\d+)$'){$next=$Matches[1]+'-rc.'+([int]$Matches[2]+1)}
elseif($v -match '^(\d+)\.(\d+)\.(\d+)$'){$next=$Matches[1]+'.'+$Matches[2]+'.'+([int]$Matches[3]+1)}
else{throw "Unsupported version: $v"}
$s=$s.Replace('<Version>'+ $v +'</Version>','<Version>'+ $next +'</Version>')
$s=[regex]::Replace($s,'<InformationalVersion>[^<]+</InformationalVersion>','<InformationalVersion>Leveling '+$next+'</InformationalVersion>')
[IO.File]::WriteAllText((Resolve-Path $p),$s,[Text.UTF8Encoding]::new($false))
Write-Output $next
