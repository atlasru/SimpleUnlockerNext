# Run with PowerShell: powershell -NoProfile -ExecutionPolicy Bypass -File .\Diagnose-Startup.ps1
# No administrator privileges, network requests or automatic system changes.
$ErrorActionPreference = 'Continue'
$exe = Join-Path $PSScriptRoot 'Unlocker.Desktop.exe'
$report = Join-Path $PSScriptRoot 'startup-diagnostics.txt'
$started = Get-Date
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('SimpleUnlocker Next startup diagnostic')
$lines.Add('Collected: ' + $started.ToString('o'))
$lines.Add('OS: ' + [Environment]::OSVersion.Version.ToString())
$lines.Add('Architecture: ' + [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)
$lines.Add('Executable present: ' + (Test-Path -LiteralPath $exe -PathType Leaf))
$logs = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SimpleUnlockerNext\Logs\startup.log'
if (Test-Path -LiteralPath $exe -PathType Leaf) {
    try {
        $process = Start-Process -FilePath $exe -WorkingDirectory $PSScriptRoot -PassThru
        $lines.Add('Started PID: ' + $process.Id)
        Start-Sleep -Seconds 8
        $process.Refresh()
        if ($process.HasExited) {
            $lines.Add('Exited early; exit code: ' + $process.ExitCode)
        } else {
            $lines.Add('Process still alive after 8 seconds.')
        }
    } catch {
        $lines.Add('Start-Process error: ' + $_.Exception.ToString())
    }
}
if (Test-Path -LiteralPath $logs -PathType Leaf) {
    $lines.Add('--- Application startup log (last 80 lines) ---')
    Get-Content -LiteralPath $logs -Tail 80 | ForEach-Object { $lines.Add($_) }
} else {
    $lines.Add('No application startup log: process may have failed before managed App construction.')
}
$lines.Add('--- Recent relevant Windows Application events ---')
try {
    Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=$started.AddMinutes(-2); Id=1000,1001,1026 } -ErrorAction Stop |
        Where-Object { $_.Message -match 'Unlocker\.Desktop|Microsoft\.UI\.Xaml|WindowsAppRuntime|WindowsAppSDK|WinRT\.Runtime' } |
        Select-Object -First 12 | ForEach-Object {
            $lines.Add(('Time: {0:o}; Provider: {1}; Event ID: {2}' -f $_.TimeCreated,$_.ProviderName,$_.Id))
            $lines.Add($_.Message)
        }
} catch { $lines.Add('Windows Event Log unavailable: ' + $_.Exception.Message) }
$lines | Set-Content -LiteralPath $report -Encoding UTF8
Write-Host ('Diagnostic written: ' + $report)
Write-Host 'Review the file before sharing: Windows event descriptions may include local paths or user names.'
