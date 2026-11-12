param(
    [Parameter(Mandatory)][int]$ServerProcessId,
    [Parameter(Mandatory)][int]$BrowserProcessId,
    [Parameter(Mandatory)][string]$OutputPath,
    [int]$DurationSeconds = 30
)
$ErrorActionPreference = 'Stop'
$previous = @{}
$samples = [System.Collections.Generic.List[object]]::new()
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$lastSampleTime = 0.0
while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
    $table = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name)
    $groups = @{}
    foreach ($root in @($ServerProcessId, $BrowserProcessId)) {
        $selected = [System.Collections.Generic.HashSet[int]]::new()
        [void]$selected.Add($root)
        do {
            $added = $false
            foreach ($row in $table) {
                if ($selected.Contains([int]$row.ParentProcessId) -and $selected.Add([int]$row.ProcessId)) { $added = $true }
            }
        } while ($added)
        foreach ($id in $selected) {
            $groups[$id] = if ($root -eq $BrowserProcessId) { 'browser' } elseif ($id -eq $ServerProcessId) { 'server' } else { 'ffmpeg' }
        }
    }
    $now = $clock.Elapsed.TotalSeconds
    $usage = @{ server = 0.0; ffmpeg = 0.0; browser = 0.0 }
    foreach ($id in $groups.Keys) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if (!$process) { continue }
        $cpu = $process.TotalProcessorTime.TotalSeconds
        if ($previous.ContainsKey($id)) { $usage[$groups[$id]] += [Math]::Max(0.0, [double]($cpu - $previous[$id])) }
        $previous[$id] = $cpu
    }
    if ($lastSampleTime -gt 0) {
        $elapsed = $now - $lastSampleTime
        $samples.Add([pscustomobject]@{ seconds = $elapsed; server = $usage.server; ffmpeg = $usage.ffmpeg; browser = $usage.browser })
    }
    $lastSampleTime = $now
    Start-Sleep -Milliseconds 1000
}
$elapsed = ($samples | Measure-Object seconds -Sum).Sum
$result = @{ elapsedSeconds = $elapsed; logicalProcessors = [Environment]::ProcessorCount }
foreach ($group in @('server', 'ffmpeg', 'browser')) {
    $result[$group + 'Cores'] = ($samples | Measure-Object $group -Sum).Sum / $elapsed
}
$result | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8
