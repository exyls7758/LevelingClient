param([string]$PublishDirectory='publish',[string]$OutputDirectory='Releases')
$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
 [xml]$p=Get-Content src/Leveling/Leveling.csproj
 $version=$p.Project.PropertyGroup.Version | Where-Object {$_} | Select-Object -First 1
 if(!(Test-Path "$PublishDirectory/Leveling.exe") -or !(Test-Path "$PublishDirectory/Multiverse.Core.dll")){throw 'Incomplete publish'}
 $nativeExpected=(Get-Content native/manifest.json -Raw | ConvertFrom-Json).binary
 if((Get-FileHash "$PublishDirectory/Multiverse.Core.dll").Hash -ne $nativeExpected){throw 'Published native mismatch'}
 $vpk=Join-Path $PWD '.tools/vpk.exe'
 # Keep the installer root separate from existing %LOCALAPPDATA%/Leveling user data.
 & $vpk pack --packId LevelingStream --packVersion $version --packDir $PublishDirectory --mainExe Leveling.exe --packTitle Leveling --icon src/Leveling/Assets/Leveling.ico --releaseNotes RELEASE_NOTES.md --outputDir $OutputDirectory
 if($LASTEXITCODE -ne 0){throw 'Velopack packaging failed'}
} finally {Pop-Location}
