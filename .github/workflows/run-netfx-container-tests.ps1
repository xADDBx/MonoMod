param(
    [Parameter(Mandatory = $true)]
    [string] $Image,
    [Parameter(Mandatory = $true)]
    [string] $Payload,
    [Parameter(Mandatory = $true)]
    [string] $Results,
    [Parameter(Mandatory = $true)]
    [ValidateSet('x64', 'x86')]
    [string] $Architecture
)

$ErrorActionPreference = 'Stop'
$powerShell = if ($Architecture -eq 'x86') {
    'C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
} else {
    'powershell.exe'
}

$container = (& docker create `
    --isolation=hyperv `
    --env MONOMOD_EXPECTED_FRAMEWORK_RELEASE=461814 `
    $Image `
    $powerShell -NoLogo -NoProfile -ExecutionPolicy Bypass `
        -File C:\payload\run-netfx-tests.ps1 -Architecture $Architecture).Trim()
if ($LASTEXITCODE -ne 0 -or !$container) {
    throw 'Could not create the test container.'
}

try {
    docker cp $Payload "${container}:C:\payload"
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not copy the test payload.'
    }

    docker start --attach $container
    $testExitCode = $LASTEXITCODE

    New-Item -ItemType Directory -Path $Results -Force | Out-Null
    foreach ($file in 'runtime.txt', 'status.txt', "$Architecture.log", "$Architecture.xml") {
        docker cp "${container}:C:\payload\$file" (Join-Path $Results $file)
    }

    if ($testExitCode -ne 0) {
        throw "The test container exited with code $testExitCode."
    }
}
finally {
    docker rm --force $container | Out-Null
}
