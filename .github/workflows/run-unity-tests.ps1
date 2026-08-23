$ErrorActionPreference = 'Stop'

# Daggerfall Unity provides a downloadable Unity 2019.4 Mono player without requiring a Unity license.
$arch = $env:UNITY_ARCH
if ($IsLinux -and $arch -eq 'x64')
{
    $hostUri = 'https://github.com/Interkarma/daggerfall-unity/releases/download/v1.1.1-cve-2025/dfu_linux_64bit-v1.1.1.zip'
    $hostHash = 'BE3C476AE92972F42448757719266AD82C04868EB7B6F3A3C90FCFCF1490849A'
    $playerName = 'DaggerfallUnity.x86_64'
    $doorstopUri = 'https://github.com/NeighTools/UnityDoorstop/releases/download/v4.5.0/doorstop_linux_release_4.5.0.zip'
    $doorstopHash = '732DD123F0B7E1165329633B9B998FA91C14046DD37AF6F63A318937EBAD47D4'
}
elseif ($IsWindows -and $arch -in 'x86', 'x64')
{
    $hostUri = "https://github.com/Interkarma/daggerfall-unity/releases/download/v1.1.1-cve-2025/dfu_windows_$($arch -eq 'x86' ? '32' : '64')bit-v1.1.1.zip"
    $hostHash = $arch -eq 'x86' `
        ? '757B648B754E1A28C889C0EE473BD05DA18E690A7F9FD6E8D2B4AB1CA36DD23A' `
        : 'B4EB2620CDF31F973ED1105D06BE8A31F21796915D65C253A93EEB02CB1D64BD'
    $playerName = 'DaggerfallUnity.exe'
    $doorstopUri = 'https://github.com/NeighTools/UnityDoorstop/releases/download/v4.5.0/doorstop_win_release_4.5.0.zip'
    $doorstopHash = '7BB953E8D883C8BDE76CED96F6D0E45660AD6E0151880D8AB5856BF4F532B147'
}
else
{
    throw "No Unity Player test host is configured for $([Environment]::OSVersion.Platform) $arch."
}

$downloadRoot = Join-Path $env:RUNNER_TEMP 'monomod-unity-test'
$hostArchive = Join-Path $downloadRoot 'host.zip'
$doorstopArchive = Join-Path $downloadRoot 'doorstop.zip'
$hostRoot = Join-Path $downloadRoot 'host'
$doorstopRoot = Join-Path $downloadRoot 'doorstop'
$payloadRoot = Join-Path $downloadRoot 'payload'

New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null

function Get-CheckedArchive([string] $Uri, [string] $Path, [string] $Hash)
{
    Invoke-WebRequest -Uri $Uri -OutFile $Path
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    if ($actualHash -ne $Hash)
    {
        throw "Hash mismatch for $Path. Expected $Hash, got $actualHash."
    }
}

Get-CheckedArchive $hostUri $hostArchive $hostHash
Get-CheckedArchive $doorstopUri $doorstopArchive $doorstopHash

[IO.Compression.ZipFile]::ExtractToDirectory($hostArchive, $hostRoot)
[IO.Compression.ZipFile]::ExtractToDirectory($doorstopArchive, $doorstopRoot)

$player = Join-Path $hostRoot $playerName
$testAssetsRoot = Join-Path $env:WORKSPACE 'release_net472'
$unityAssetsRoot = Join-Path $env:WORKSPACE 'unity-test-assets' 'release_net472'
$facadeRoot = Join-Path $unityAssetsRoot 'Facades'
New-Item -ItemType Directory -Path $payloadRoot | Out-Null
Get-ChildItem -LiteralPath $testAssetsRoot | Copy-Item -Destination $payloadRoot -Recurse
Get-ChildItem -LiteralPath $unityAssetsRoot -File | Copy-Item -Destination $payloadRoot
Get-ChildItem -LiteralPath $facadeRoot -File | Copy-Item -Destination $payloadRoot
$payload = Join-Path $payloadRoot 'MonoMod.UnityTest.dll'
$results = Join-Path $env:WORKSPACE 'unity-test-results.xml'

foreach ($path in $player, $payload, (Join-Path $payloadRoot 'xunit.console.exe'), (Join-Path $payloadRoot 'Microsoft.CSharp.dll'), (Join-Path $payloadRoot 'Mono.CSharp.dll'))
{
    if (-not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "Required Unity test file not found: $path"
    }
}

if ($IsLinux)
{
    $doorstop = Join-Path $doorstopRoot 'x64' 'libdoorstop.so'
    if (-not (Test-Path -LiteralPath $doorstop -PathType Leaf))
    {
        throw "Required Unity test file not found: $doorstop"
    }

    & chmod +x $player
    if ($LASTEXITCODE -ne 0)
    {
        throw "chmod failed with exit code $LASTEXITCODE."
    }

    $doorstopLibraryPath = Join-Path $doorstopRoot 'x64'
    if ($env:LD_LIBRARY_PATH)
    {
        $doorstopLibraryPath += ":$env:LD_LIBRARY_PATH"
    }
    & "$PSScriptRoot/run-with-dumps.ps1" -PassMonoSignals -TimeoutSeconds 180 '/usr/bin/env' `
        'DOORSTOP_ENABLED=1' `
        "DOORSTOP_TARGET_ASSEMBLY=$payload" `
        "DOORSTOP_MONO_DLL_SEARCH_PATH_OVERRIDE=$payloadRoot" `
        "LD_LIBRARY_PATH=$doorstopLibraryPath" `
        "LD_PRELOAD=$doorstop" `
        "MONOMOD_UNITY_TEST_RESULTS=$results" `
        $player '-batchmode' '-nographics' '-logFile' '-'
}
else
{
    $doorstop = Join-Path $doorstopRoot $arch 'winhttp.dll'
    if (-not (Test-Path -LiteralPath $doorstop -PathType Leaf))
    {
        throw "Required Unity test file not found: $doorstop"
    }

    Copy-Item -LiteralPath $doorstop -Destination (Join-Path $hostRoot 'winhttp.dll')
    $env:MONOMOD_UNITY_TEST_RESULTS = $results
    & "$PSScriptRoot/run-with-dumps.ps1" -Exe $player -WaitForExit -TimeoutSeconds 180 -Args @(
        '--doorstop-enabled', 'true',
        '--doorstop-target-assembly', $payload,
        '--doorstop-mono-dll-search-path-override', $payloadRoot,
        '-batchmode', '-nographics', '-logFile', '-'
    )
}
$exitCode = $LASTEXITCODE

if (-not (Test-Path -LiteralPath $results))
{
    throw "The Unity Player exited with code $exitCode without writing test results."
}

[xml]$testResults = Get-Content -Raw -LiteralPath $results
$assemblies = @($testResults.assemblies.assembly)
$total = ($assemblies | ForEach-Object { [int]$_.GetAttribute('total') } | Measure-Object -Sum).Sum
if ($total -eq 0)
{
    throw 'The Unity Player did not discover any tests.'
}

exit $exitCode
