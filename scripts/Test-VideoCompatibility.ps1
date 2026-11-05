$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
 $vswhere="${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
 $msbuild=& $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild/**/Bin/MSBuild.exe' | Select-Object -First 1
 if(!$msbuild){throw 'MSBuild required'}
 & $msbuild tests/MediaFoundation/MediaFoundation.vcxproj /p:Configuration=Release /p:Platform=x64 /verbosity:minimal
 if($LASTEXITCODE -ne 0){throw 'Windows decoder test build failed'}
 # Requires a real D3D11 video device. Hosted CI is not GPU validation.
 & ./tests/MediaFoundation/bin/MediaFoundation.exe
 if($LASTEXITCODE -ne 0){throw 'Windows decoder test failed'}
} finally {Pop-Location}
