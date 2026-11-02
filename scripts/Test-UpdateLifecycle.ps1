$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
 $testRoot=Join-Path $PWD ('evidence/lifecycle-'+[guid]::NewGuid().ToString('N'))
 New-Item -ItemType Directory -Force $testRoot | Out-Null
 dotnet publish tests/UpdateLifecycle/UpdateLifecycle.csproj -c Release -r win-x64 --self-contained true -o "$testRoot/publish" /p:Version=1.0.0
 if($LASTEXITCODE -ne 0){throw 'Fixture build failed'}
 & ./.tools/vpk.exe pack --packId LevelingUpdateTest --packVersion 1.0.0 --packDir "$testRoot/publish" --mainExe UpdateLifecycle.exe --outputDir "$testRoot/baseline"
 if($LASTEXITCODE -ne 0){throw 'Baseline packaging failed'}
 dotnet publish tests/UpdateLifecycle/UpdateLifecycle.csproj -c Release -r win-x64 --self-contained true -o "$testRoot/publish" /p:Version=1.0.1
 if($LASTEXITCODE -ne 0){throw 'New-version fixture build failed'}
 & ./.tools/vpk.exe pack --packId LevelingUpdateTest --packVersion 1.0.1 --packDir "$testRoot/publish" --mainExe UpdateLifecycle.exe --outputDir "$testRoot/feed"
 if($LASTEXITCODE -ne 0){throw 'Update packaging failed'}
 Expand-Archive -LiteralPath "$testRoot/baseline/LevelingUpdateTest-win-Portable.zip" -DestinationPath "$testRoot/installed"
 $env:LEVELING_UPDATE_TEST_ROOT=$testRoot
 $exe=Get-ChildItem "$testRoot/installed" -Filter UpdateLifecycle.exe -Recurse | Select-Object -First 1
 Start-Process -FilePath $exe.FullName -WindowStyle Hidden
 $deadline=(Get-Date).AddSeconds(60)
 while(!(Test-Path "$testRoot/restarted.txt") -and !(Test-Path "$testRoot/failed.txt") -and (Get-Date) -lt $deadline){Start-Sleep -Milliseconds 250}
 if(Test-Path "$testRoot/failed.txt"){throw (Get-Content "$testRoot/failed.txt" -Raw)}
 if(!(Test-Path "$testRoot/restarted.txt") -or (Get-Content "$testRoot/restarted.txt" -Raw) -ne '1.0.1|1.0.1.0'){throw "Update/restart did not complete with the new binary: $testRoot"}
 Write-Output "PASS real portable update 1.0.0 -> 1.0.1 and restart: $testRoot"
 $corruptRoot=Join-Path $testRoot 'corrupt'
 New-Item -ItemType Directory -Force "$corruptRoot/feed" | Out-Null
 Copy-Item "$testRoot/feed/*" "$corruptRoot/feed/"
 $package=Get-ChildItem "$corruptRoot/feed" -Filter '*-full.nupkg' | Select-Object -First 1
 $bytes=[IO.File]::ReadAllBytes($package.FullName)
 $bytes[100]=$bytes[100] -bxor 255
 [IO.File]::WriteAllBytes($package.FullName,$bytes)
 Expand-Archive -LiteralPath "$testRoot/baseline/LevelingUpdateTest-win-Portable.zip" -DestinationPath "$corruptRoot/installed"
 $env:LEVELING_UPDATE_TEST_ROOT=$corruptRoot
 $exe=Get-ChildItem "$corruptRoot/installed" -Filter UpdateLifecycle.exe -Recurse | Select-Object -First 1
 $process=Start-Process -FilePath $exe.FullName -WindowStyle Hidden -PassThru
 if(!$process.WaitForExit(60000)){throw 'Corrupt-package check timed out'}
 if(!(Test-Path "$corruptRoot/failed.txt") -or (Test-Path "$corruptRoot/restarted.txt") -or (Test-Path "$corruptRoot/downloaded.txt")){throw 'Corrupt package was not rejected before apply'}
 $failure=Get-Content "$corruptRoot/failed.txt" -Raw
 if($failure -notmatch 'Checksum|checksum|SHA256|hash'){throw "Unexpected rejection: $failure"}
 Write-Output 'PASS corrupted package rejected before installation/restart'
} finally {Remove-Item Env:LEVELING_UPDATE_TEST_ROOT -ErrorAction SilentlyContinue; Pop-Location}
