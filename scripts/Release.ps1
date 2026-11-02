param([Parameter(Mandatory)][string]$Summary)
$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
 if(!$env:NV_CODEC_SDK -or !$env:CUDA_PATH){throw 'NV_CODEC_SDK and CUDA_PATH must point to the installed NVIDIA SDKs'}
 foreach($f in @("$env:NV_CODEC_SDK/Interface/nvEncodeAPI.h","$env:NV_CODEC_SDK/Interface/nvcuvid.h","$env:NV_CODEC_SDK/Lib/win/x64/nvcuvid.lib","$env:CUDA_PATH/include/cuda.h","$env:CUDA_PATH/lib/x64/cuda.lib")){if(!(Test-Path $f)){throw "Missing required SDK file: $f"}}
 ./scripts/Test.ps1
 $version=./scripts/Bump-Version.ps1
 $previous=git describe --tags --abbrev=0 2>$null
 $changes=if($previous){git log "$previous..HEAD" --pretty=format:'- %s'}else{git log -10 --pretty=format:'- %s'}
 "# Leveling $version`n`n- $Summary`n`n$changes" | Set-Content RELEASE_NOTES.md -Encoding utf8
 $vswhere="${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
 $msbuild=& $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild/**/Bin/MSBuild.exe' | Select-Object -First 1
 if(!$msbuild){throw 'Visual Studio MSBuild not found'}
 & $msbuild src/Leveling/Leveling.csproj /restore /t:Publish /p:Configuration=Release /p:Platform=x64 /p:PublishDir="$pwd/publish/" /verbosity:minimal
 if($LASTEXITCODE -ne 0){throw 'Build failed; nothing published'}
 ./scripts/NativeArtifact.ps1 -Refresh
 if(!(Test-Path .tools/vpk.exe)){dotnet tool install vpk --version 1.2.0 --tool-path .tools;if($LASTEXITCODE -ne 0){throw 'Packager unavailable'}}
 ./scripts/Pack.ps1
 git add src tests scripts native .github .gitignore RELEASE_NOTES.md UPDATES.md AGENTS.md
 if($LASTEXITCODE -ne 0){throw 'Git staging failed'}
 git commit -m "Release $version - $Summary"
 if($LASTEXITCODE -ne 0){throw 'Commit failed'}
 git push origin HEAD:main
 if($LASTEXITCODE -ne 0){throw 'Push failed; authenticate GitHub and retry. The local package is preserved.'}
} finally {Pop-Location}
