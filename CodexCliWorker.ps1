param([Parameter(Mandatory = $true)][string]$RequestPath)

$ErrorActionPreference = 'Stop'
$request = $null
$completionPath = $null
$completion = @{ ExitCode = -1; Error = $null }
$mutex = $null
$ownsMutex = $false

function Quote-WindowsArgument([string]$Value) {
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $slashCount = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]92) {
            $slashCount++
            continue
        }
        if ($character -eq [char]34) {
            if ($slashCount -gt 0) { [void]$builder.Append(('\' * ($slashCount * 2))) }
            [void]$builder.Append('\"')
            $slashCount = 0
            continue
        }
        if ($slashCount -gt 0) {
            [void]$builder.Append(('\' * $slashCount))
            $slashCount = 0
        }
        [void]$builder.Append($character)
    }
    if ($slashCount -gt 0) { [void]$builder.Append(('\' * ($slashCount * 2))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

try {
    $request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
    $completionPath = [string]$request.CompletionPath
    $createdNew = $false
    $mutex = [System.Threading.Mutex]::new($false, "Local\CodexGUI.CliWorker.$($request.RunId)", [ref]$createdNew)
    try { $ownsMutex = $mutex.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { exit 0 }
    try { Remove-Item -LiteralPath $RequestPath -Force -ErrorAction SilentlyContinue } catch { }

    New-Item -ItemType Directory -Path (Split-Path -Parent $request.OutputPath) -Force | Out-Null
    $started = @{
        RunId = [string]$request.RunId
        ProcessId = $PID
        StartTimeUtcTicks = (Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks
    }
    [System.IO.File]::WriteAllText([string]$request.StartedPath, ($started | ConvertTo-Json -Compress), [System.Text.UTF8Encoding]::new($false))
    $arguments = @($request.Arguments | ForEach-Object { Quote-WindowsArgument ([string]$_) }) -join ' '

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = [string]$request.ExecutablePath
    $startInfo.Arguments = $arguments
    $startInfo.WorkingDirectory = [string]$request.WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw 'Could not start Codex CLI.' }
    $outputFile = [System.IO.File]::Open([string]$request.OutputPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
    $errorFile = [System.IO.File]::Open([string]$request.ErrorPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
    $outputCopy = $process.StandardOutput.BaseStream.CopyToAsync($outputFile)
    $errorCopy = $process.StandardError.BaseStream.CopyToAsync($errorFile)
    $process.WaitForExit()
    $copiesCompleted = [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]@($outputCopy, $errorCopy), 3000)
    if ($copiesCompleted) {
        $outputCopy.GetAwaiter().GetResult()
        $errorCopy.GetAwaiter().GetResult()
    }
    else {
        $process.StandardOutput.Close()
        $process.StandardError.Close()
        try { $outputCopy.Wait(1000) } catch { }
        try { $errorCopy.Wait(1000) } catch { }
    }
    $completion.ExitCode = $process.ExitCode
    $outputFile.Dispose()
    $errorFile.Dispose()
    $process.Dispose()
    foreach ($line in [System.IO.File]::ReadLines([string]$request.OutputPath, [System.Text.Encoding]::UTF8)) {
        try {
            $event = ConvertFrom-Json -InputObject $line -ErrorAction Stop
            if ($event.type -eq 'thread.started' -and -not [string]::IsNullOrWhiteSpace($event.thread_id)) {
                $completion.SessionId = [string]$event.thread_id
                break
            }
        }
        catch { }
    }
}
catch {
    $completion.ExitCode = -1
    $completion.Error = $_.Exception.Message
}
finally {
    if ($ownsMutex -and -not [string]::IsNullOrWhiteSpace($completionPath)) {
        $temporaryPath = $completionPath + '.tmp'
        [System.IO.File]::WriteAllText($temporaryPath, ($completion | ConvertTo-Json -Compress), [System.Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $completionPath -Force
    }
    if ($ownsMutex -and $null -ne $mutex) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
}
