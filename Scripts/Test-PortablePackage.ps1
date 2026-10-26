param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot '..\Build\bin\Portable\CodexGUI.exe'),
    [switch]$CopyApplicationDirectory
)

$ErrorActionPreference = 'Stop'
if (-not [string]::Equals($env:GITHUB_ACTIONS, 'true', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'This check starts CodexGUI.exe and may only run in GitHub Actions. Use a local build check instead.'
}

$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testDirectory = Join-Path $tempBase ('CodexGUI-package-check-' + [Guid]::NewGuid().ToString('N'))
$process = $null
try {
    [void][IO.Directory]::CreateDirectory($testDirectory)
    $isolatedExecutable = Join-Path $testDirectory ([IO.Path]::GetFileName($executable))
    if ($CopyApplicationDirectory) {
        foreach ($entry in Get-ChildItem -LiteralPath (Split-Path -Parent $executable) -Force) {
            Copy-Item -LiteralPath $entry.FullName -Destination $testDirectory -Recurse -Force
        }
        if (-not (Test-Path -LiteralPath (Join-Path $testDirectory 'coreclr.dll')) -or
            -not (Test-Path -LiteralPath (Join-Path $testDirectory 'PresentationFramework.dll'))) {
            throw 'The application directory does not contain the self-contained .NET and WPF runtime.'
        }
    }
    else {
        Copy-Item -LiteralPath $executable -Destination $isolatedExecutable
    }
    $emptyRuntime = Join-Path $testDirectory 'empty-runtime'
    [void][IO.Directory]::CreateDirectory($emptyRuntime)
    $extraction = Join-Path $testDirectory 'extracted-runtime'

    $start = [Diagnostics.ProcessStartInfo]::new($isolatedExecutable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $testDirectory
    $start.Environment['DOTNET_ROOT'] = $emptyRuntime
    $start.Environment['DOTNET_ROOT_X64'] = $emptyRuntime
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $extraction
    $hostTrace = Join-Path $testDirectory 'host-trace.txt'
    $start.Environment['DOTNET_HOST_TRACE'] = '1'
    $start.Environment['DOTNET_HOST_TRACEFILE'] = $hostTrace
    # This existing entry point exits before installation, login, user state or automations.
    # The deliberately absent request cannot start App Server or submit any prompt.
    $start.ArgumentList.Add('--app-server-worker')
    $start.ArgumentList.Add((Join-Path $testDirectory 'absent-request.json'))
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(30000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw 'The isolated package did not exit its managed startup probe.'
    }
    if ($process.ExitCode -ne 0) { throw "Managed startup failed with exit code $($process.ExitCode)." }
    if ($CopyApplicationDirectory) {
        $nativeWpf = Get-ChildItem -LiteralPath $testDirectory -Recurse -Filter wpfgfx_cor3.dll -File | Select-Object -First 1
        if ($null -eq $nativeWpf) { throw 'The application directory did not supply its native WPF graphics library.' }
        Write-Output 'PASS: multi-file self-contained app starts with empty DOTNET_ROOT and exits the managed probe without user state or App Server.'
    }
    else {
        # The Windows single-file host statically links CoreCLR; a separate coreclr.dll is not required.
        # https://github.com/dotnet/runtime/blob/main/src/native/corehost/apphost/static/CMakeLists.txt
        $trace = [IO.File]::ReadAllText($hostTrace)
        if ($trace -notmatch 'Using internal hostpolicy' -or
            $trace -notmatch "CoreCLR path = '', CoreCLR dir = ''" -or
            $trace -notmatch 'PresentationFramework\.dll found in bundle') {
            $trace -split "`n" | Where-Object { $_ -match 'HOSTPOLICY|CoreCLR path|PresentationFramework\.dll' } |
                Select-Object -First 6 | ForEach-Object { $_.Substring(0, [Math]::Min(220, $_.Length)) }
            throw 'The host trace does not confirm the embedded CoreCLR and WPF bundle.'
        }
        $nativeWpf = Get-ChildItem -LiteralPath $extraction -Recurse -Filter wpfgfx_cor3.dll -File | Select-Object -First 1
        if ($null -eq $nativeWpf) { throw 'The EXE did not supply its native WPF graphics library.' }
        Write-Output 'PASS: single EXE starts with empty DOTNET_ROOT, uses embedded CoreCLR and bundled WPF, and exits the managed probe without user state or App Server.'
    }
}
finally {
    if ($null -ne $process) { $process.Dispose() }
    $resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
    if (-not $resolvedTestDirectory.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedTestDirectory) -notlike 'CodexGUI-package-check-*') {
        throw 'Refusing cleanup outside the dedicated package test directory.'
    }
    if (Test-Path -LiteralPath $resolvedTestDirectory) {
        # Windows may briefly retain an EXE handle after the managed process exits.
        # This is an isolated, disposable CI directory: do not fail a successful
        # runtime probe solely because antivirus or the OS delays file deletion.
        $cleanupError = $null
        for ($attempt = 1; $attempt -le 20; $attempt++) {
            try {
                Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force -ErrorAction Stop
                $cleanupError = $null
                break
            }
            catch {
                $cleanupError = $_
                if ($attempt -lt 20) { Start-Sleep -Milliseconds 250 }
            }
        }
        if ($null -ne $cleanupError) {
            Write-Warning "Could not remove isolated package check directory: $($cleanupError.Exception.Message)"
        }
    }
}
