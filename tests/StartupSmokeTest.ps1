param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\src\PomodoroClock.App\bin\Debug\net8.0-windows\win-x64\番茄时钟.exe'),
    [int]$ObservationMilliseconds = 3000
)

$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$process = Start-Process -FilePath $executablePath -PassThru -WindowStyle Hidden

try {
    if ($process.WaitForExit($ObservationMilliseconds)) {
        throw "Startup smoke test failed: the application exited early with code $($process.ExitCode)."
    }

    Write-Output "Startup smoke test passed: process $($process.Id) remained alive for $ObservationMilliseconds ms."
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id
        $process.WaitForExit()
    }
}
