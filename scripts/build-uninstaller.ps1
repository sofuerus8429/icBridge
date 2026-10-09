param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$releaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "dist/$releaseVersion" }
$compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework compiler is unavailable.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$destination = Join-Path $OutputDirectory 'Uninstall-MicBridge.exe'
$source = Join-Path $projectRoot 'tools/MicBridge.UninstallLauncher/Program.cs'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warnaserror+ "/out:$destination" "/win32icon:$projectRoot/assets/branding/micbridge.ico" /reference:System.Windows.Forms.dll $source
if ($LASTEXITCODE -ne 0) { throw 'Uninstall launcher compilation failed.' }
Write-Output "Uninstall launcher: $destination"
