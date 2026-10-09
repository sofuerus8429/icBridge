param([string]$OutputDirectory = '', [switch]$SourceOnly, [switch]$SingleFile)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "dist/$version" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$portableDirectory = Join-Path $projectRoot "artifacts/$version/win-x64"
if ($SingleFile) { $portableDirectory = Join-Path $projectRoot "dist/$version" }
if (-not $SourceOnly -and -not (Test-Path -LiteralPath (Join-Path $portableDirectory 'MicBridge.exe'))) { throw 'Publish the Windows x64 application first, or use -SourceOnly.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Get-PackageFiles([string]$Directory, [string]$Prefix, [switch]$Source) {
    $items = Get-ChildItem -LiteralPath $Directory -Recurse -Force
    if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
        throw "Package input contains a reparse point: $Directory"
    }
    foreach ($item in $items | Where-Object { -not $_.PSIsContainer }) {
        $relative = [IO.Path]::GetRelativePath($Directory, $item.FullName).Replace('\', '/')
        if ($relative -match '(^|/)(bin|obj|TestResults|\.vs|\.idea)(/|$)') { continue }
        if ($item.Extension -in '.log', '.pdb', '.wav', '.zip', '.nupkg', '.snupkg', '.user', '.suo') { continue }
        [PSCustomObject]@{ Name = "$Prefix$relative"; Path = $item.FullName }
    }
}

function New-VerifiedArchive([string]$Destination, [object[]]$Files, [hashtable]$TextFiles) {
    if (Test-Path -LiteralPath $Destination) { throw "Archive already exists; choose a new output directory: $Destination" }
    $archiveStream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew)
    $archive = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create)
    $expectedHashes = @{}
    $utf8 = [Text.UTF8Encoding]::new($false)
    try {
        foreach ($file in $Files | Sort-Object Name) {
            if ($expectedHashes.ContainsKey($file.Name)) { throw "Duplicate archive path: $($file.Name)" }
            $expectedHashes[$file.Name] = (Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.Path, $file.Name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        foreach ($name in $TextFiles.Keys | Sort-Object) {
            if ($expectedHashes.ContainsKey($name)) { throw "Duplicate archive path: $name" }
            $bytes = $utf8.GetBytes($TextFiles[$name])
            $expectedHashes[$name] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
            $entryStream = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal).Open()
            try { $entryStream.Write($bytes, 0, $bytes.Length) } finally { $entryStream.Dispose() }
        }
    } finally { $archive.Dispose(); $archiveStream.Dispose() }

    # Read and hash every decompressed entry, including runtime DLLs and hidden
    # source-control configuration. A successful ZIP write alone is insufficient.
    $verifiedArchive = [IO.Compression.ZipFile]::OpenRead($Destination)
    [long]$unpackedBytes = 0
    try {
        if ($verifiedArchive.Entries.Count -ne $expectedHashes.Count) { throw 'Archive entry count does not match its inputs.' }
        foreach ($entry in $verifiedArchive.Entries) {
            $entryStream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream)) }
            finally { $entryStream.Dispose() }
            if ($hash -ne $expectedHashes[$entry.FullName]) { throw "Archive content mismatch: $($entry.FullName)" }
            $unpackedBytes += $entry.Length
        }
        [PSCustomObject]@{ File = [IO.Path]::GetFileName($Destination); Bytes = (Get-Item -LiteralPath $Destination).Length;
            Sha256 = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash;
            Entries = $verifiedArchive.Entries.Count; UnpackedBytes = $unpackedBytes; AllEntryHashesVerified = $true }
    } finally { $verifiedArchive.Dispose() }
}

$reports = @('netease-virtual-mic-recording.json', 'simple-audio-portable.json', 'simple-ui-verification.json',
    'console-ui-verification.json', 'console-audio-verification.json', 'console-portable-verification.json', 'console-process-verification.json',
    "$version/reports/single-file-runtime.json", "$version/reports/single-file-console.json", "$version/reports/single-file-audio.json", "$version/reports/single-file-process.json", "$version/reports/bundled-driver.json", "$version/reports/uninstall-verification.json",
    "$version/reports/runtime.json", "$version/reports/console.json", "$version/reports/audio.json", "$version/reports/driver.json", "$version/reports/uninstall.json", "$version/reports/process-audio.json", "$version/reports/performance.json", "$version/reports/launcher.json", "$version/reports/picker.json",
    "$version/reports/hardware-audio.json", "$version/reports/netease-hardware-final.json", "$version/reports/device-notifications.json", "$version/reports/microphone-lifecycle.json", "$version/reports/dsp-baseline-final.json", "$version/reports/dsp-optimized-final.json",
    "$version/reports/performance-baseline-current.json", "$version/reports/performance-final.json", "$version/reports/hardware-preflight.json", "$version/reports/build-tests.json",
    "$version/reports/endurance.json", "$version/reports/limiter-baseline.json", "$version/reports/limiter-optimized.json",
    "$version/reports/qa-lock.json", "$version/reports/release-verification.json", "$version/reports/startup-cancellation.json", "$version/reports/settings.json",
    "$version/reports/file-formats.json", "$version/reports/resampling-baseline.json", "$version/reports/resampling-optimized.json",
    "$version/reports/dependencies-baseline.json", "$version/reports/dependencies-optimized.json", "$version/reports/file-formats-isolated.json", "$version/reports/process-load.json")
$portableResult = $null
if (-not $SourceOnly) {
$portableFiles = @(Get-PackageFiles $portableDirectory '')
if ($SingleFile) { $portableFiles = @('MicBridge.exe', 'Uninstall-MicBridge.exe') | ForEach-Object { [PSCustomObject]@{ Name = $_; Path = Join-Path $portableDirectory $_ } } }
foreach ($document in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'docs/verification.md')) {
    $matchingFile = $portableFiles | Where-Object Name -eq $document
    if ($matchingFile) { $matchingFile.Path = Join-Path $projectRoot $document }
}
foreach ($report in $(if ($SingleFile) { @() } else { $reports })) {
    $path = Join-Path $projectRoot "artifacts/$report"
    if (Test-Path -LiteralPath $path) { $portableFiles += [PSCustomObject]@{ Name = "reports/$report"; Path = $path } }
}
$portableGuide = @'
MicBridge 音桥 — Windows x64 便携版

1. 完整解压 ZIP 到一个文件夹，双击 MicBridge.exe。已包含 .NET 运行时。
2. 主界面左上选择真实麦克风，右上输出选择 CABLE Input。
3. 主界面选择本地文件或程序进程。采集网易云时，选择 cloudmusic 的主窗口进程。
4. 点击播放/启动混音，调整原麦克风、音乐和总输出三个竖向推子。
   勾选某路 GAIN 后，该推子最高可到 300%；细节选项在高级设置。
5. 需要使用混音的程序，输入设备选择 CABLE Output。

本机已安装 VB-CABLE；其他电脑首次打开音桥时，可从内置安装界面安装驱动，批准管理员请求并按提示重启。
驱动官网：https://vb-audio.com/Cable/

网易云 → 音桥 → CABLE Input → CABLE Output 实际录回已在 v0.1.2 验证通过。
本版的增益模式、电平、音源切换和实际本地播放检查见 reports/。
报告位于 reports/，详细使用说明位于 docs/user-guide.md。
本包包含应用、运行时、内置官方驱动包、文档和报告，不包含歌曲录音或调试日志。
'@
if ($SingleFile) {
$portableGuide = @'
MicBridge 音桥 — 精简单文件版

双击 MicBridge.exe 即可使用。Windows 11 / Windows 10 1903 及以上自带 .NET Framework 4.8。
可选 Uninstall-MicBridge.exe 需与主 EXE 放一起；高级设置中也有卸载入口。

免装额外驱动：高级设置勾选“使用声卡立体声混音”，启用已禁用的录音端。首次播放会用低音量提示音自动验证输出，必要时选择可录回的端口；高级设置中也可手动检测或取消。游戏麦克风选择立体声混音。
此模式只适用于支持硬件回环的声卡，且会包含该声卡上的其他播放声音。为独立调音，游戏和音乐程序请输出到另一块声卡/USB 耳机；不要开启 Windows“侦听此设备”。
如果没有立体声混音，使用 EXE 内置的官方虚拟驱动。安装需管理员批准，按提示重启。音桥输出选 CABLE Input，游戏输入选 CABLE Output；请勿禁用 CABLE Output。
Windows 默认播放建议选择真实耳机/扬声器；设为 CABLE Input 会让其他程序直接进入虚拟麦克风，绕过音桥的音量和静音。主界面会提醒，软件不会自动修改默认设备。

主界面选择原麦克风、音乐本地文件或进程、输出设备。三个竖向推子调节原麦克风、音乐、总输出，每路 GAIN 最高 300%。其他选项在高级设置。
卸载驱动请使用官方窗口，卸载后按提示重启；仅清除设置不会删除音乐文件。
源码、完整文档和本机验证报告位于另一个 source.zip。
'@
}
$portableResult = New-VerifiedArchive (Join-Path $OutputDirectory "MicBridge-$version-win-x64.zip") $portableFiles @{ '使用说明.txt' = $portableGuide }
}

$sourcePrefix = "MicBridge-$version/"
$sourceFiles = @()
foreach ($file in Get-ChildItem -LiteralPath $projectRoot -Force -File) {
    if ($file.Extension -in '.log', '.pdb', '.wav', '.zip', '.user', '.suo') { continue }
    $sourceFiles += [PSCustomObject]@{ Name = "$sourcePrefix$($file.Name)"; Path = $file.FullName }
}
foreach ($directory in @('.github', 'assets', 'docs', 'scripts', 'src', 'tests', 'tools')) {
    $sourceFiles += @(Get-PackageFiles (Join-Path $projectRoot $directory) "$sourcePrefix$directory/" -Source)
}
$driverPackage = 'assets/drivers/vb-cable/VBCABLE_Driver_Pack45.zip'
if ((Get-FileHash -LiteralPath (Join-Path $projectRoot $driverPackage) -Algorithm SHA256).Hash -ne 'B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB') {
    throw 'The bundled driver ZIP does not match the original official package.'
}
$sourceFiles += [PSCustomObject]@{ Name = "$sourcePrefix$driverPackage"; Path = Join-Path $projectRoot $driverPackage }
foreach ($report in $reports) {
    $path = Join-Path $projectRoot "artifacts/$report"
    if (Test-Path -LiteralPath $path) { $sourceFiles += [PSCustomObject]@{ Name = "${sourcePrefix}artifacts/$report"; Path = $path } }
}
$sourceResult = New-VerifiedArchive (Join-Path $OutputDirectory "MicBridge-$version-source.zip") $sourceFiles @{}
$results = @($sourceResult)
if ($portableResult) { $results = @($portableResult, $sourceResult) }
$checksums = ($results | ForEach-Object { "$($_.Sha256.ToLowerInvariant())  $($_.File)" }) -join "`n"
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS.txt'), "$checksums`n", [Text.UTF8Encoding]::new($false))
$pendingPaths = @('artifacts/driver-setup', 'artifacts/netease-virtual-mic-recording.wav') |
    Where-Object { Test-Path -LiteralPath (Join-Path $projectRoot $_) }
$manifest = [ordered]@{ Version = $version; Packages = $results; ExcludedFromPackages = @('old releases', 'temporary driver staging', 'recordings', '.git', 'bin', 'obj', 'logs', 'PDB symbols');
    OriginalTemporaryCleanupComplete = @($pendingPaths).Count -eq 0; OriginalTemporaryPathsRemaining = @($pendingPaths) }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$results | ConvertTo-Json -Depth 5
