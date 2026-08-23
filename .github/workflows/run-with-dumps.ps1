param (
    [Parameter(Mandatory=$true, Position=0)]
    [string]$Exe,
    [switch]$WaitForExit,
    [switch]$PassMonoSignals,
    [int]$TimeoutSeconds = 300,
    [Parameter(Mandatory=$false, Position=1, ValueFromRemainingArguments=$True)]
    [string[]]$Args = @()
)

$ErrorActionPreference = 'Stop';

$workspace = $env:WORKSPACE;
if ($null -eq $workspace)
{
    Write-Error "WORKSPACE not set!";
}

$dumpsPath = $env:DUMPS_PATH;
if ($null -eq $dumpsPath)
{
    Write-Error "DUMPS_PATH not set!";
}

# make sure the dir exists
New-Item -Type Directory $dumpsPath -Force | Out-Null;
$lldbHelpers = Join-Path $workspace '.github' 'lldb';

if ($IsWindows)
{
    # on Windows, we need to configure some registry keys before invoking
    $key = "HKLM:\\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";
    New-Item -Path $key -ErrorAction SilentlyContinue;
    New-ItemProperty -Path $key -Name 'DumpType' -PropertyType 'DWord' -Value 2 -Force;
    New-ItemProperty -Path $key -Name 'DumpCount' -PropertyType 'DWord' -Value 10 -Force;
    New-ItemProperty -Path $key -Name 'DumpFolder' -PropertyType 'String' -Value $dumpsPath -Force;

    # then we can execute the program
    if ($WaitForExit)
    {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new($Exe);
        $startInfo.UseShellExecute = $false;
        $startInfo.WorkingDirectory = Split-Path -Parent $Exe;
        foreach ($arg in $Args)
        {
            $startInfo.ArgumentList.Add($arg);
        }
        $process = [System.Diagnostics.Process]::Start($startInfo);
        if (!$process.WaitForExit($TimeoutSeconds * 1000))
        {
            try
            {
                &procdump -accepteula -ma $process.Id (Join-Path $dumpsPath 'dump_hang.dmp');
            }
            finally
            {
                if (!$process.HasExited)
                {
                    $process.Kill($true);
                    $process.WaitForExit();
                }
            }
            exit 124;
        }
        exit $process.ExitCode;
    }
    else
    {
        &$Exe @Args;
        exit $LastExitCode;
    }
}
elseif ($IsLinux -or $IsMacOS)
{
    $timeoutSignal = 'XCPU';
    $monoSignalOptions = '';
    if ($PassMonoSignals)
    {
        $timeoutSignal = 'XFSZ';
        $monoSignalOptions = '-o "process handle -s false -p true -n false SIGPWR SIGXCPU"';
    }

    # on Linux, we need to set the core_pattern and run the app with a ulimit -c unlimited
    &bash -c @"
set -eo pipefail;
ulimit -c unlimited;
set +e;
# on MacOS, SIGXCPU doesn't coredump by default. Thus, we use LLDB unattended to perform the dump 
# because we run our Linux stuff in containers, we can't set the core_pattern. Thus, we'll do the same thing we *must* do on MacOS and use LLDB to generate dumps when crashing
lldb -x -b \
    -O "command alias sdmp process save-core -s full -pminidump '$(Join-Path $dumpsPath 'dump_crash.core')'"\
    -s "$(Join-Path $lldbHelpers 'setup.lldb')" $monoSignalOptions \
    -s "$(Join-Path $lldbHelpers 'run.lldb')" \
    -K "$(Join-Path $lldbHelpers 'crash.lldb')" \
    -s "$(Join-Path $lldbHelpers 'teardown.lldb')" -- timeout -s $timeoutSignal -k 60 $TimeoutSeconds "$Exe" "`$@";
exit `$?;
"@ -- @Args;
    exit $LastExitCode;

}
else
{
    Write-Error "Unknown operating system; not proceeding"
}
