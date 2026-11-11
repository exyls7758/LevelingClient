$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
 & "$PSScriptRoot/Test-NativeDeployment.ps1"
 $vswhere="${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
 $msbuild=& $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild/**/Bin/MSBuild.exe' | Select-Object -First 1
 if(!$msbuild){throw 'MSBuild required for presenter regression'}
 & $msbuild tests/MediaFoundation/MediaFoundation.vcxproj /p:Configuration=Release /p:Platform=x64 /verbosity:minimal
 if($LASTEXITCODE -ne 0){throw 'Windows decoder test build failed'}
 # Execution requires real video hardware: scripts/Test-VideoCompatibility.ps1.
 & $msbuild tests/Presenter/Presenter.vcxproj /p:Configuration=Release /p:Platform=x64 /verbosity:minimal
 if($LASTEXITCODE -ne 0){throw 'Presenter regression build failed'}
 & ./tests/Presenter/bin/Presenter.exe
 if($LASTEXITCODE -ne 0){throw 'Presenter regression failed'}
 foreach($project in @('src/Leveling/tests/window-startup/Leveling.WindowRegression.csproj','tests/Connections/Connections.Tests.csproj','tests/Audio/Audio.Tests.csproj','tests/Updates/Updates.Tests.csproj','src/Leveling/tests/managed/Leveling.CoreRegression.csproj','src/Leveling/tests/desktop-input-service/InputSession.Tests.csproj')) {
  dotnet run --project $project -c Release
  if($LASTEXITCODE -ne 0){throw "Tests failed: $project"}
 }
} finally {Pop-Location}
