param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64', [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$versionProperties = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)
$releaseVersion = $versionProperties.Project.PropertyGroup.Version
$publishDirectory = Join-Path $projectRoot "artifacts/$releaseVersion/$Runtime"
Push-Location -LiteralPath $projectRoot
try {
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    $runtimeVersion = '8.0.31'
    $publishArgs = @('publish', 'src/MicBridge.App', '-f', 'net8.0-windows', '-c', 'Release', '-r', $Runtime, '--self-contained', $selfContained, '-o', $publishDirectory, '--nologo', '-p:RestoreLockedMode=true')
    if (-not $FrameworkDependent) { $publishArgs += "-p:RuntimeFrameworkVersion=$runtimeVersion" }
    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.md,LICENSE,THIRD_PARTY_NOTICES.md -Destination $publishDirectory
    Copy-Item -LiteralPath docs -Destination $publishDirectory -Recurse -Force
    if (-not $FrameworkDependent) {
        $nugetPackageRoot = ((dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
        $licenseDirectory = Join-Path $publishDirectory 'licenses'
        New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
        $runtimePack = Join-Path $nugetPackageRoot "microsoft.netcore.app.runtime.$Runtime/$runtimeVersion"
        $desktopPack = Join-Path $nugetPackageRoot "microsoft.windowsdesktop.app.runtime.$Runtime/$runtimeVersion"
        Copy-Item -LiteralPath (Join-Path $runtimePack 'LICENSE.TXT') -Destination (Join-Path $licenseDirectory 'dotnet-LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $runtimePack 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenseDirectory 'dotnet-THIRD-PARTY-NOTICES.txt')
        Copy-Item -LiteralPath (Join-Path $desktopPack 'LICENSE') -Destination (Join-Path $licenseDirectory 'windowsdesktop-LICENSE.txt')
    }
    $zipPath = Join-Path $projectRoot "artifacts/MicBridge-$releaseVersion-$Runtime.zip"
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -Force
    Write-Host "Portable app: $publishDirectory/MicBridge.exe"
    Write-Host "Archive: $zipPath"
} finally { Pop-Location }
