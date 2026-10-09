param([ValidateSet('win-x64')][string]$Runtime = 'win-x64', [switch]$BundledRuntime)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$releaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$publishDirectory = Join-Path $projectRoot "artifacts/$releaseVersion/$Runtime-single"
$deliveryDirectory = Join-Path $projectRoot "dist/$releaseVersion"
if ($BundledRuntime) {
    $publishDirectory = Join-Path $projectRoot "artifacts/$releaseVersion/$Runtime-single-bundled"
    $deliveryDirectory = Join-Path $deliveryDirectory 'bundled-runtime'
}
$deliveryPath = Join-Path $deliveryDirectory 'MicBridge.exe'
Push-Location -LiteralPath $projectRoot
try {
    if ($BundledRuntime) {
    dotnet publish src/MicBridge.App -c Release -f net8.0-windows -r $Runtime --self-contained true -o $publishDirectory --nologo `
        -p:TargetFrameworks=net8.0-windows `
        "-p:RuntimeIdentifiers=$Runtime" `
        -p:RestoreLockedMode=true -p:RuntimeFrameworkVersion=8.0.31 -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed.' }
    } else {
        dotnet build src/MicBridge.App -c Release -f net48 --nologo -p:RestoreLockedMode=true `
            -p:DebugType=none -p:DebugSymbols=false -warnaserror
        if ($LASTEXITCODE -ne 0) { throw 'Lightweight single-file build failed.' }
        New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $projectRoot 'src/MicBridge.App/bin/Release/net48/MicBridge.exe') -Destination (Join-Path $publishDirectory 'MicBridge.exe') -Force
    }
    $files = @(Get-ChildItem -LiteralPath $publishDirectory -Force)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'MicBridge.exe' -or $files[0].PSIsContainer) {
        throw 'Single-file publish unexpectedly requires sidecar files.'
    }
    New-Item -ItemType Directory -Path $deliveryDirectory -Force | Out-Null
    Copy-Item -LiteralPath $files[0].FullName -Destination $deliveryPath -Force
    & (Join-Path $PSScriptRoot 'build-uninstaller.ps1') -OutputDirectory $deliveryDirectory
    $uninstallPath = Join-Path $deliveryDirectory 'Uninstall-MicBridge.exe'
    $hash = (Get-FileHash -LiteralPath $deliveryPath -Algorithm SHA256).Hash
    $manifest = [ordered]@{ Version = $releaseVersion; Runtime = $Runtime; File = 'MicBridge.exe';
        Bytes = (Get-Item -LiteralPath $deliveryPath).Length; Sha256 = $hash; SelfContained = [bool]$BundledRuntime;
        SidecarFilesRequired = $false; RuntimeVersion = $(if ($BundledRuntime) { '8.0.31' } else { 'Windows .NET Framework 4.8' });
        NativeComponentsExtractedAtStartup = [bool]$BundledRuntime; AllManagedDependenciesEmbedded = $true;
        MinimumWindows10Version = '1903'; ProcessCaptureMinimumWindowsBuild = 20348;
        EmbeddedDriver = 'VB-CABLE Pack45'; EmbeddedDriverOriginalSha256 = 'B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB';
        DriverInstalledAutomatically = $false; DriverInstallationRequiresAdministrator = $true;
        ExistingHardwareLoopbackSupported = $true; HardwareModeRequiresExtraVirtualDriver = $false;
        HardwareModeRequiresExistingCaptureEndpoint = $true; HardwareModeCapturesAllAudioOnAdapter = $true;
        OptionalUninstallLauncher = 'Uninstall-MicBridge.exe'; UninstallLauncherBytes = (Get-Item -LiteralPath $uninstallPath).Length;
        UninstallLauncherSha256 = (Get-FileHash -LiteralPath $uninstallPath -Algorithm SHA256).Hash;
        UninstallLauncherRequiresMainExecutable = $true }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $deliveryDirectory 'single-file-manifest.json') -Encoding utf8
    @("$($hash.ToLowerInvariant())  MicBridge.exe", "$($manifest.UninstallLauncherSha256.ToLowerInvariant())  Uninstall-MicBridge.exe") | Set-Content -LiteralPath (Join-Path $deliveryDirectory 'single-file-SHA256SUMS.txt') -Encoding utf8
    Write-Output "Single executable: $deliveryPath"
    $manifest | ConvertTo-Json
} finally { Pop-Location }
