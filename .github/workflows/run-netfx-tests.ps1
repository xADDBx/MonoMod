param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('x64', 'x86')]
    [string] $Architecture
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$runtimeLog = Join-Path $root 'runtime.txt'
$statusLog = Join-Path $root 'status.txt'
$frameworkKey = 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'
$release = (Get-ItemProperty -LiteralPath $frameworkKey).Release
$frameworkDirectory = if ([Environment]::Is64BitProcess) { 'Framework64' } else { 'Framework' }
$clr = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
    "$env:WINDIR\Microsoft.NET\$frameworkDirectory\v4.0.30319\clr.dll"
)
$nativeImage = [System.Diagnostics.Process]::GetCurrentProcess().Modules |
    Where-Object ModuleName -EQ 'mscorlib.ni.dll' |
    Select-Object -First 1
$expectedRelease = [int]$env:MONOMOD_EXPECTED_FRAMEWORK_RELEASE

@(
    "Release=$release"
    "CLR=$($clr.FileVersion)"
    "64-bit process=$([Environment]::Is64BitProcess)"
    "mscorlib native image=$($nativeImage.FileName)"
) | Tee-Object -FilePath $runtimeLog | ForEach-Object { Write-Host $_ }

if ($release -ne $expectedRelease) {
    throw "Expected .NET Framework release $expectedRelease, found $release."
}
if ($null -eq $nativeImage) {
    throw 'The process did not load mscorlib.ni.dll.'
}

function Invoke-Xunit {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Architecture,
        [Parameter(Mandatory = $true)]
        [string] $Runner
    )

    $log = Join-Path $root "$Architecture.log"
    $xml = Join-Path $root "$Architecture.xml"
    $assembly = Join-Path $root 'tests\MonoMod.UnitTest.dll'

    Write-Host "[netfx] Starting $Architecture tests at $(Get-Date -Format o)"
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $Runner $assembly -nologo -nocolor -noshadow -parallel none -verbose -xml $xml 2>&1 |
            Tee-Object -FilePath $log |
            ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    Write-Host "[netfx] $Architecture tests exited with $exitCode at $(Get-Date -Format o)"
    return $exitCode
}

$runner = if ($Architecture -eq 'x86') {
    Join-Path $root 'xunit\xunit.console.x86.exe'
} else {
    Join-Path $root 'xunit\xunit.console.exe'
}
$testExit = Invoke-Xunit $Architecture $runner

@(
    'runtime=0'
    "$Architecture=$testExit"
) | Set-Content -LiteralPath $statusLog

if ($testExit -ne 0) {
    exit 1
}
