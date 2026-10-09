param([string]$ReportDirectory = '', [switch]$HardwareLoopback, [switch]$SkipEndurance)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$executable = Join-Path $projectRoot "dist/$version/MicBridge.exe"
if (-not (Test-Path -LiteralPath $executable)) { throw 'Publish the single executable before verification.' }
if (-not $ReportDirectory) { $ReportDirectory = Join-Path $projectRoot "artifacts/$version/reports" }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$stages = [Collections.Generic.List[string]]::new()

function Invoke-Verification([string]$Command, [string]$ReportName, [string]$Program = $verificationExecutable) {
    $reportPath = Join-Path $ReportDirectory "$ReportName.json"
    $startInfo = [Diagnostics.ProcessStartInfo]::new($Program)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.ArgumentList.Add($Command)
    $startInfo.ArgumentList.Add($reportPath)
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            if (Test-Path -LiteralPath $reportPath) { Get-Content -LiteralPath $reportPath | Write-Output }
            throw "Verification failed: $Command (exit $($process.ExitCode))"
        }
        $stages.Add($Command)
        Write-Output "$Command passed"
    } finally { $process.Dispose() }
}

# Keep the runtime copy by itself; its report is written to the reports folder.
$isolatedDirectory = Join-Path $projectRoot "artifacts/$version/isolated"
New-Item -ItemType Directory -Path $isolatedDirectory -Force | Out-Null
$verificationExecutable = Join-Path $isolatedDirectory 'MicBridge.exe'
Copy-Item -LiteralPath $executable -Destination $verificationExecutable -Force
Invoke-Verification '--verify-single-file' 'runtime'
Invoke-Verification '--verify-console' 'console'
Invoke-Verification '--verify-settings' 'settings'
Invoke-Verification '--verify-bundled-driver' 'driver'
Invoke-Verification '--verify-uninstall' 'uninstall'

# These commands share real audio endpoints: always run them in sequence.
Invoke-Verification '--verify-simple-audio' 'audio'
Invoke-Verification '--verify-file-formats' 'file-formats'
Invoke-Verification '--verify-process-audio' 'process-audio'
Invoke-Verification '--verify-process-load' 'process-load'
Invoke-Verification '--verify-startup-cancellation' 'startup-cancellation'
Invoke-Verification '--verify-microphone-lifecycle' 'microphone-lifecycle'
if ($HardwareLoopback) {
    Invoke-Verification '--verify-hardware-preflight' 'hardware-preflight'
    Invoke-Verification '--verify-hardware-audio' 'hardware-audio'
}
if (-not $SkipEndurance) { Invoke-Verification '--verify-endurance' 'endurance' }
Invoke-Verification '--verify-performance' 'performance'
[ordered]@{ Version = $version; Passed = $true; Stages = @($stages); ExecutableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash;
    IsolatedExecutableUsedForAllStages = $true; HardwareLoopbackChecked = [bool]$HardwareLoopback;
    EnduranceChecked = -not $SkipEndurance; CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O') } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'release-verification.json') -Encoding utf8
