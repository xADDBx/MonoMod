$ErrorActionPreference = 'Stop'

# The Unity player omits framework facades and the Mono C# binder required by the dynamic tests.
$packages = @(
    @('mono-devel.deb', 'https://archive.ubuntu.com/ubuntu/pool/universe/m/mono/mono-devel_4.6.2.7+dfsg-1ubuntu1_all.deb', '5D16CAB24E95828711AEAEF900DB9651BC8FAD071BA26699484222BA57FB8664'),
    @('microsoft-csharp.deb', 'https://archive.ubuntu.com/ubuntu/pool/universe/m/mono/libmono-microsoft-csharp4.0-cil_4.6.2.7+dfsg-1ubuntu1_all.deb', 'E313C0B66458C3B1A15B2BD68C98AD9C4C012AEC5F4F875C2F55DE6A41BBDF3D'),
    @('mono-csharp.deb', 'https://archive.ubuntu.com/ubuntu/pool/universe/m/mono/libmono-csharp4.0c-cil_4.6.2.7+dfsg-1ubuntu1_all.deb', '7030E2DB2A6E0D7A51A70E26D76ACB48D71E1B75194BE8C1EEC826CC61D86FAB')
)
$downloadRoot = Join-Path $env:RUNNER_TEMP 'monomod-unity-libraries'
$extractRoot = Join-Path $downloadRoot 'mono'
$outputRoot = Join-Path $env:GITHUB_WORKSPACE 'artifacts/bin/MonoMod.UnityTest/release_net472/Facades'
New-Item -ItemType Directory -Path $downloadRoot, $extractRoot, $outputRoot -Force | Out-Null

foreach ($package in $packages)
{
    $path = Join-Path $downloadRoot $package[0]
    Invoke-WebRequest -Uri $package[1] -OutFile $path
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
    if ($actualHash -ne $package[2])
    {
        throw "Hash mismatch for $($package[0]). Expected $($package[2]), got $actualHash."
    }
    & dpkg-deb -x $path $extractRoot
    if ($LASTEXITCODE -ne 0)
    {
        throw "dpkg-deb failed for $($package[0]) with exit code $LASTEXITCODE."
    }
}

$facadeRoot = Join-Path $extractRoot 'usr/lib/mono/4.5/Facades'
$facades = @(
    'System.Collections',
    'System.Collections.Concurrent',
    'System.Diagnostics.Debug',
    'System.Diagnostics.Tools',
    'System.Globalization',
    'System.Linq',
    'System.ObjectModel',
    'System.Reflection',
    'System.Reflection.Extensions',
    'System.Runtime',
    'System.Runtime.Extensions',
    'System.Text.RegularExpressions',
    'System.Threading.Tasks'
)
foreach ($facade in $facades)
{
    Copy-Item -LiteralPath (Join-Path $facadeRoot "$facade.dll") -Destination $outputRoot
}
Copy-Item -LiteralPath (Join-Path $extractRoot 'usr/lib/mono/gac/Microsoft.CSharp/4.0.0.0__b03f5f7f11d50a3a/Microsoft.CSharp.dll') -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $extractRoot 'usr/lib/mono/gac/Mono.CSharp/4.0.0.0__0738eb9f132ed756/Mono.CSharp.dll') -Destination $outputRoot
