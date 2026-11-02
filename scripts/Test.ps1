$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
 foreach($project in @('tests/Updates/Updates.Tests.csproj','src/Leveling/tests/managed/Leveling.CoreRegression.csproj','src/Leveling/tests/desktop-input-service/InputSession.Tests.csproj')) {
  dotnet run --project $project -c Release
  if($LASTEXITCODE -ne 0){throw "Tests failed: $project"}
 }
} finally {Pop-Location}
