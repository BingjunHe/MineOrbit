param([switch]$Publish, [string]$PublishDirectory = "artifacts/MineOrbit-0.5.0-release")
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$localDotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (Test-Path -LiteralPath $localDotnet) { $env:DOTNET_ROOT = Split-Path -Parent $localDotnet }
& $dotnetExe restore MineOrbit.sln --packages .tools/packages
if ($LASTEXITCODE) { throw '依赖还原失败' }
& $dotnetExe build MineOrbit.sln --no-restore -c Release -m:1
if ($LASTEXITCODE) { throw '编译失败' }
& $dotnetExe test tests/MineOrbit.Tests --no-build --no-restore -c Release --logger 'trx;LogFileName=phase5.trx' --results-directory artifacts/test-results
if ($LASTEXITCODE) { throw '测试失败' }
if ($Publish) {
    & $dotnetExe publish src/MineOrbit.UI -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false --packages .tools/packages -o $PublishDirectory
    if ($LASTEXITCODE) { throw '发布失败' }
    New-Item -ItemType Directory -Force "$PublishDirectory/assets" | Out-Null
    Copy-Item -Path assets/* -Destination "$PublishDirectory/assets" -Force
    Copy-Item -LiteralPath README.md -Destination $PublishDirectory/README.md -Force
    Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination $PublishDirectory/THIRD-PARTY-NOTICES.md -Force
    New-Item -ItemType Directory -Force $PublishDirectory/licenses,$PublishDirectory/docs | Out-Null
    Copy-Item -Path licenses/* -Destination $PublishDirectory/licenses -Recurse -Force
    Copy-Item -Path docs/* -Destination $PublishDirectory/docs -Recurse -Force
    Compress-Archive -Path "$PublishDirectory/*" -DestinationPath "${PublishDirectory}.zip" -Force
    Get-FileHash "${PublishDirectory}.zip" -Algorithm SHA256 | Format-List
}
