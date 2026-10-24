using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexGUI;

internal static class Program
{
    private static readonly string Executable = Environment.ProcessPath!;
    private static int _passed;

    private static async Task<int> Main(string[] args)
    {
        // The same executable supplies a predictable stdio peer and competing writer processes.
        if (args.FirstOrDefault() == "--listen") return await FakeServerAsync();
        if (args.FirstOrDefault() == "--hold-inherited-stderr")
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            return 0;
        }
        if (args.FirstOrDefault() == "--fake-appserver-drop-after-initialize") return await FakeAppServerDropAfterInitializeAsync(args);
        if (args.FirstOrDefault() == "--fake-appserver-block-initialize") return await FakeAppServerBlockInitializeAsync(args);
        if (args.FirstOrDefault() == "--fake-appserver-terminal-control") return await FakeAppServerTerminalControlAsync(args);
        if (args.FirstOrDefault() == "--fake-appserver-flood") return await FakeAppServerFloodAsync();
        if (args.FirstOrDefault() == "--fake-appserver-ignore-shutdown") return await FakeAppServerIgnoreShutdownAsync();
        if (args.FirstOrDefault() == "write-actions") return WriteActions(args);

        var root = Path.Combine(Path.GetTempPath(), "CodexGUI-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Check("Workspace search finds names and text with correct line numbers", () => WorkspaceSearchAsync(root));
            await Check("Workspace search is bounded and honors cancellation", () => WorkspaceSearchLimitsAsync(root));
            await Check("Workspace map reads real folders, frameworks and package dependencies", () => WorkspaceMapAsync(root));
            await Check("Workspace map buttons select their actual paths and search UI returns results", () => WorkspaceControlsAsync(root));
            await Check("Borderless maximize bounds respect monitor work areas", MaximizedWorkAreaAsync);
            await Check("Local goal steps round-trip without breaking legacy thread state", WorkspaceGoalStateAsync);
            await Check("JSON recovery preserves corrupt primary and repairs it", () => RecoveryAsync(root));
            await Check("Unrecoverable JSON does not overwrite either snapshot", () => BrokenRecoveryAsync(root));
            await Check("Queued writes preserve order and independent snapshots", () => QueueAsync(root));
            await Check("A failed queued write is reported and does not block retries", () => QueueRetryAsync(root));
            await Check("Project action writers serialize revision check and replacement", () => ActionsAsync(root));
            await Check("Schedules recover the previous valid snapshot", () => SchedulesAsync(root));
            await Check("Unrecoverable schedules block saves until a successful reload", () => BrokenSchedulesAsync(root));
            await Check("Interval catch-up retains its original cadence", IntervalAsync);
            await Check("Monthly schedules skip nonexistent dates", MonthlyAsync);
            await Check("RRULE COUNT disables an exhausted schedule", RecurrenceAsync);
            await Check("RPC returns a normal result", () => RpcAsync(root, cancel: false));
            await Check("Operation cancellation closes the RPC child process", () => RpcAsync(root, cancel: true));
            await Check("Window lifetime cancellation closes the RPC child process", () => RpcAsync(root, cancel: true, lifetime: true));
            await Check("Short RPC session reuses one process across pages", () => RpcSessionPaginationAsync(root));
            await Check("Thread history rejects repeated pagination cursors", () => ThreadHistoryPaginationAsync(root));
            await Check("Cancelling a later history page stops its RPC process", () => ThreadHistoryCancellationAsync(root));
            await Check("Thread history accumulation respects its byte budget", ThreadHistoryBudgetAsync);
            await Check("Thread context usage reads the latest event and honors cancellation", () => ThreadContextUsageAsync(root));
            await Check("Malformed RPC response faults the request and closes its process", () => RpcMalformedResponseAsync(root));
            await Check("Oversized RPC response closes its child process", () => RpcOversizedResponseAsync(root));
            await Check("RPC ignores unrelated response ids and closes on EOF", () => RpcUnexpectedResponseEofAsync(root));
            await Check("RPC shutdown does not wait for inherited stderr handles", () => RpcInheritedStderrAsync(root));
            await Check("Every public RPC forwards operation cancellation", AllRpcCancellationAsync);
            await Check("Pre-cancelled RPC never starts a process", PreCancelledAsync);
            await Check("Worker recovery rejects invalid or reused process identities", () => WorkerIdentityRecoveryAsync(root));
            await Check("Worker cleanup cannot delete outside its run directory", () => WorkerCleanupConfinementAsync(root));
            await Check("Restored worker paths are rebuilt from the run id", () => StoredWorkerPathsAsync(root));
            await Check("Automation recovery requires a matching worker marker", () => AutomationWorkerIdentityAsync(root));
            await Check("Automation completion is bound to its worker run", AutomationCompletionIdentityAsync);
            await Check("Preparing automation recovery validates completion identity", () => PreparingAutomationRecoveryAsync(root));
            await Check("Automation summary bounds output while preserving final text", () => AutomationSummaryAsync(root));
            await Check("Automation output readers skip malformed lines", () => AutomationOutputRecoveryAsync(root));
            await Check("Malformed worker controls are reported and removed", () => MalformedControlFileAsync(root));
            await Check("Worker control responses validate ids and payloads", () => WorkerControlValidationAsync(root));
            await Check("Worker protocol line reader preserves boundaries and enforces its limit", BoundedProtocolLinesAsync);
            await Check("App-server initialization forwards cancellation and closes its child", AppServerConnectionCancellationAsync);
            await Check("Closing terminal host cancels pending app-server initialization", () => TerminalHostStartupCancellationAsync(root));
            await Check("Terminal host reconnects after app-server disconnect", () => TerminalHostReconnectAsync(root));
            await Check("Terminal terminate RPC times out and remains retryable", () => TerminalTerminateTimeoutAsync(root));
            await Check("Terminal output batches preserve recent text within a bound", TerminalOutputBatchBufferAsync);
            await Check("App-server disposal unblocks a full incoming queue", BoundedQueueShutdownAsync);
            await Check("App-server disposal kills an unresponsive child", AppServerShutdownTimeoutAsync);
            await Check("GitHub PR recognizes SSH remotes and rejects malformed slugs", GitHubRemotesAsync);
            await Check("Agent Git context is bounded and escapes multiline filenames", GitContextAsync);
            await Check("Agent Git preflight warns about conflicts and unsafe branch state", GitPreflightAsync);
            await Check("Git context records a validated HEAD baseline", GitHeadBaselineAsync);
            await Check("Agent Git context detects potentially sensitive files", GitSecretsAsync);
            await Check("Staged secret preflight preserves NUL-delimited filenames", StagedSecretsAsync);
            await Check("Local agent discovers project verification entry points without reading files", () => ProjectInventoryAsync(root));
            await Check("Agent Git context escapes terminal and Unicode formatting characters", GitContextEscapingAsync);
            await Check("Git status parsing handles large repositories and renamed paths", GitStatusScaleAsync);
            await Check("Git porcelain v2 detects unmerged entries", GitConflictParsingAsync);
            await Check("NUL-delimited Git numstat handles renames and special filenames", GitNumstatAsync);
            await Check("Untracked text lines are counted without loading large files", () => TextLineCountingAsync(root));
            await Check("Codex CLI output capture is bounded and drains excess output", CliOutputBoundAsync);
            Console.WriteLine($"PASS: {_passed} checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            // This directory was created by this invocation and contains only check fixtures.
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task ThreadContextUsageAsync(string root)
    {
        const string sessionId = "thread-context-usage";
        var sessionsRoot = Path.Combine(root, "context-usage-sessions");
        var sessionDirectory = Path.Combine(sessionsRoot, "2026", "10");
        Directory.CreateDirectory(sessionDirectory);
        var path = Path.Combine(sessionDirectory, $"rollout-2026-10-24-{sessionId}.jsonl");
        var lines = new[]
        {
            JsonSerializer.Serialize(new { type = "session_meta", payload = new { session_id = sessionId } }),
            JsonSerializer.Serialize(new
            {
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new { last_token_usage = new { total_tokens = 12 }, model_context_window = 100 }
                }
            }),
            JsonSerializer.Serialize(new
            {
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new { last_token_usage = new { total_tokens = 24 }, model_context_window = 120 }
                }
            })
        };
        File.WriteAllLines(path, lines);

        var usage = MainWindow.ReadThreadContextUsage(sessionsRoot, sessionId, CancellationToken.None);
        Require(usage is { Tokens: 24, Window: 120 }, "The latest token usage event was not returned");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = MainWindow.ReadThreadContextUsage(sessionsRoot, sessionId, cancellation.Token);
            throw new InvalidOperationException("A pre-cancelled context scan continued");
        }
        catch (OperationCanceledException) { }

        return Task.CompletedTask;
    }

    private static Task WorkerIdentityRecoveryAsync(string root)
    {
        const System.Reflection.BindingFlags nonPublic = System.Reflection.BindingFlags.NonPublic;
        var runType = typeof(MainWindow).GetNestedType("ActiveCliRun", nonPublic)
            ?? throw new InvalidOperationException("ActiveCliRun type was not found");
        var run = Activator.CreateInstance(runType)
            ?? throw new InvalidOperationException("ActiveCliRun could not be created");
        var runId = Guid.NewGuid().ToString("N");
        var startedPath = Path.Combine(root, "worker-identity", "started.json");
        Directory.CreateDirectory(Path.GetDirectoryName(startedPath)!);
        runType.GetProperty("RunId")!.SetValue(run, runId);
        runType.GetProperty("StartedPath")!.SetValue(run, startedPath);

        var process = Process.GetCurrentProcess();
        var processStartTicks = process.StartTime.ToUniversalTime().Ticks;
        var method = typeof(MainWindow).GetMethod("TryGetCliWorkerProcess", nonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("Worker process recovery method was not found");

        Process? InvokeRecovery(long startTicks)
        {
            File.WriteAllText(startedPath, JsonSerializer.Serialize(new
            {
                RunId = runId,
                ProcessId = Environment.ProcessId,
                StartTimeUtcTicks = startTicks
            }));
            return (Process?)method.Invoke(null, [run]);
        }

        using (var recovered = InvokeRecovery(processStartTicks))
            Require(recovered?.Id == Environment.ProcessId, "A matching worker identity was not recovered");
        using (var stale = InvokeRecovery(processStartTicks + TimeSpan.TicksPerSecond * 10))
            Require(stale is null, "A reused process id with a mismatched start time was accepted");
        using (var corrupt = InvokeRecovery(long.MinValue))
            Require(corrupt is null, "A negative process start timestamp was accepted");
        using (var outOfRange = InvokeRecovery(long.MaxValue))
            Require(outOfRange is null, "An out-of-range process start timestamp was accepted");

        File.Delete(startedPath);
        runType.GetProperty("WorkerProcessId")!.SetValue(run, Environment.ProcessId);
        runType.GetProperty("WorkerStartTimeUtcTicks")!.SetValue(run, long.MinValue);
        using (var corruptState = (Process?)method.Invoke(null, [run]))
            Require(corruptState is null, "A corrupt persisted process timestamp was accepted without a marker");
        return Task.CompletedTask;
    }

    private static Task WorkerCleanupConfinementAsync(string root)
    {
        const System.Reflection.BindingFlags nonPublicStatic = System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static;
        var runType = typeof(MainWindow).GetNestedType("ActiveCliRun", System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ActiveCliRun type was not found");
        var cleanup = typeof(MainWindow).GetMethod("CleanupCliRun", nonPublicStatic)
            ?? throw new InvalidOperationException("Worker cleanup method was not found");
        var untrustedDirectory = Path.Combine(root, "worker-cleanup-sentinel");
        Directory.CreateDirectory(untrustedDirectory);
        var sentinel = Path.Combine(untrustedDirectory, "keep.txt");
        File.WriteAllText(sentinel, "preserve");
        var run = Activator.CreateInstance(runType)
            ?? throw new InvalidOperationException("ActiveCliRun could not be created");
        runType.GetProperty("RunId")!.SetValue(run, Guid.NewGuid().ToString("N"));
        runType.GetProperty("RequestPath")!.SetValue(run, Path.Combine(untrustedDirectory, "request.json"));

        cleanup.Invoke(null, [run]);
        Require(File.Exists(sentinel), "Worker cleanup deleted data outside the local workers directory");

        runType.GetProperty("RunId")!.SetValue(run, "..");
        cleanup.Invoke(null, [run]);
        Require(File.Exists(sentinel), "Worker cleanup accepted an invalid run id");

        runType.GetProperty("RunId")!.SetValue(run, Guid.NewGuid().ToString("N"));
        runType.GetProperty("RequestPath")!.SetValue(run, "invalid\0path");
        cleanup.Invoke(null, [run]);
        Require(File.Exists(sentinel), "Worker cleanup did not tolerate a malformed saved path");
        return Task.CompletedTask;
    }

    private static Task StoredWorkerPathsAsync(string root)
    {
        const System.Reflection.BindingFlags nonPublicStatic = System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static;
        var runType = typeof(MainWindow).GetNestedType("ActiveCliRun", System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ActiveCliRun type was not found");
        var stateType = typeof(MainWindow).GetNestedType("StoredState", System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("StoredState type was not found");
        var threadType = typeof(MainWindow).GetNestedType("StoredThread", System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("StoredThread type was not found");
        var validate = typeof(MainWindow).GetMethod("ValidateStoredState", nonPublicStatic)
            ?? throw new InvalidOperationException("Stored state validator was not found");
        var run = Activator.CreateInstance(runType)
            ?? throw new InvalidOperationException("ActiveCliRun could not be created");
        var state = Activator.CreateInstance(stateType)
            ?? throw new InvalidOperationException("StoredState could not be created");
        var thread = Activator.CreateInstance(threadType)
            ?? throw new InvalidOperationException("StoredThread could not be created");
        var runId = Guid.NewGuid().ToString("N");
        runType.GetProperty("RunId")!.SetValue(run, runId);
        runType.GetProperty("RequestPath")!.SetValue(run, Path.Combine(root, "outside", "request.json"));
        runType.GetProperty("ScriptPath")!.SetValue(run, Path.Combine(root, "outside", "worker.ps1"));
        runType.GetProperty("StartedPath")!.SetValue(run, Path.Combine(root, "outside", "started.json"));
        runType.GetProperty("OutputPath")!.SetValue(run, Path.Combine(root, "outside", "output.jsonl"));
        runType.GetProperty("ErrorPath")!.SetValue(run, Path.Combine(root, "outside", "error.txt"));
        runType.GetProperty("CompletionPath")!.SetValue(run, Path.Combine(root, "outside", "completed.json"));

        threadType.GetProperty("ActiveRun")!.SetValue(thread, run);
        ((System.Collections.IList)stateType.GetProperty("Threads")!.GetValue(state)!).Add(thread);
        validate.Invoke(null, [state]);
        var runDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexGUI", "workers", runId);
        foreach (var (property, filename) in new[]
        {
            ("RequestPath", "request.json"),
            ("ScriptPath", "CodexCliWorker.ps1"),
            ("StartedPath", "started.json"),
            ("OutputPath", "stdout.jsonl"),
            ("ErrorPath", "stderr.txt"),
            ("CompletionPath", "completed.json")
        })
        {
            var actualPath = (string?)runType.GetProperty(property)!.GetValue(run);
            Require(string.Equals(actualPath, Path.Combine(runDirectory, filename), StringComparison.OrdinalIgnoreCase),
                $"Stored {property} was not rebuilt under the worker run directory");
        }

        var statePath = Path.Combine(root, "worker-state", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        runType.GetProperty("RunId")!.SetValue(run, "..");
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, stateType));
        runType.GetProperty("RunId")!.SetValue(run, runId);
        File.WriteAllText(statePath + ".bak", JsonSerializer.Serialize(state, stateType));
        var backupReader = typeof(AtomicJsonFile).GetMethod("ReadWithBackup", System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(stateType);
        var stateValidator = validate.CreateDelegate(typeof(Action<>).MakeGenericType(stateType));
        var recoveredState = backupReader.Invoke(null, [statePath, stateValidator])!;
        var recoveredThreads = (System.Collections.IList)stateType.GetProperty("Threads")!.GetValue(recoveredState)!;
        var recoveredThread = recoveredThreads[0]!;
        var recoveredRun = threadType.GetProperty("ActiveRun")!.GetValue(recoveredThread)!;
        Require(string.Equals((string?)runType.GetProperty("RequestPath")!.GetValue(recoveredRun),
                Path.Combine(runDirectory, "request.json"), StringComparison.OrdinalIgnoreCase),
            "Invalid primary worker state did not recover from the backup");
        return Task.CompletedTask;
    }

    private static Task AutomationWorkerIdentityAsync(string root)
    {
        const System.Reflection.BindingFlags nonPublicStatic = System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static;
        var method = typeof(MainWindow).GetMethod("TryRestoreAutomationWorkerIdentity", nonPublicStatic)
            ?? throw new InvalidOperationException("Automation worker recovery method was not found");
        var runId = Guid.NewGuid().ToString("N");
        var otherRunId = Guid.NewGuid().ToString("N");
        var runDirectory = Path.Combine(root, "automation-worker-identity");
        Directory.CreateDirectory(runDirectory);
        var startedPath = Path.Combine(runDirectory, "started.json");
        var startTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        var run = new CodexAutomationRun { WorkerRunId = runId };

        void WriteMarker(string markerRunId, long markerTicks = 0) => File.WriteAllText(startedPath,
            JsonSerializer.Serialize(new
            {
                RunId = markerRunId,
                ProcessId = Environment.ProcessId,
                StartTimeUtcTicks = markerTicks == 0 ? startTicks : markerTicks
            }));

        WriteMarker(otherRunId);
        var mismatched = (bool)method.Invoke(null, [run, runDirectory])!;
        Require(!mismatched && run.WorkerProcessId == 0,
            "Automation recovery accepted a marker for another worker run");

        WriteMarker(runId);
        var matching = (bool)method.Invoke(null, [run, runDirectory])!;
        Require(matching && run.WorkerProcessId == Environment.ProcessId && run.WorkerStartTimeUtcTicks == startTicks,
            "Automation recovery rejected a matching worker marker");

        run.WorkerProcessId = 0;
        WriteMarker(runId, long.MinValue);
        var invalidTimestamp = (bool)method.Invoke(null, [run, runDirectory])!;
        Require(!invalidTimestamp && run.WorkerProcessId == 0,
            "Automation recovery accepted an invalid worker timestamp");
        return Task.CompletedTask;
    }

    private static Task AutomationCompletionIdentityAsync()
    {
        const System.Reflection.BindingFlags nonPublicStatic = System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static;
        var validate = typeof(MainWindow).GetMethod("ValidateAutomationCompletionRunId", nonPublicStatic)
            ?? throw new InvalidOperationException("Automation completion validation method was not found");
        var runId = Guid.NewGuid().ToString("N");
        validate.Invoke(null, [new CodexAppServerWorkerCompletion { RunId = runId }, runId]);
        validate.Invoke(null, [new CodexAppServerWorkerCompletion(), runId]);
        try
        {
            validate.Invoke(null, [new CodexAppServerWorkerCompletion { RunId = Guid.NewGuid().ToString("N") }, runId]);
            throw new InvalidOperationException("Completion for a different worker run was accepted");
        }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is InvalidDataException) { }
        return Task.CompletedTask;
    }

    private static Task PreparingAutomationRecoveryAsync(string root)
    {
        const System.Reflection.BindingFlags nonPublicStatic = System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static;
        var method = typeof(MainWindow).GetMethod("CanResumePreparingAutomation", nonPublicStatic)
            ?? throw new InvalidOperationException("Automation preparation recovery method was not found");
        var runId = Guid.NewGuid().ToString("N");
        var otherRunId = Guid.NewGuid().ToString("N");
        var completionPath = Path.Combine(root, "preparing-automation", "completed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(completionPath)!);

        bool CanResume(bool markerMatches) => (bool)method.Invoke(null, [markerMatches, completionPath, runId])!;

        Require(CanResume(markerMatches: true), "A matching worker marker should recover a preparing run");
        File.WriteAllText(completionPath, JsonSerializer.Serialize(new CodexAppServerWorkerCompletion()));
        Require(!CanResume(markerMatches: false), "A legacy completion without a matching marker was trusted");
        File.WriteAllText(completionPath, JsonSerializer.Serialize(new CodexAppServerWorkerCompletion { RunId = otherRunId }));
        Require(!CanResume(markerMatches: false), "A completion for another run recovered the preparing automation");
        File.WriteAllText(completionPath, JsonSerializer.Serialize(new CodexAppServerWorkerCompletion { RunId = runId }));
        Require(CanResume(markerMatches: false), "A completion bound to the expected run was rejected");
        return Task.CompletedTask;
    }

    private static Task AutomationSummaryAsync(string root)
    {
        const System.Reflection.BindingFlags nonPublicStatic = System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static;
        var method = typeof(MainWindow).GetMethod("ReadAutomationSummary", nonPublicStatic)
            ?? throw new InvalidOperationException("Automation summary reader was not found");
        var outputPath = Path.Combine(root, "automation-summary", "stdout.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var delta = new string('d', 2000);
        File.WriteAllLines(outputPath,
        [
            JsonSerializer.Serialize(new { type = "response.output_text.delta", delta }),
            JsonSerializer.Serialize(new { type = "response.output_text.delta", delta })
        ]);
        var deltaSummary = (string)method.Invoke(null, [outputPath])!;
        Require(deltaSummary.Length == 1601 && deltaSummary.EndsWith('…')
            && deltaSummary[..1600] == new string('d', 1600),
            "Automation delta summary grew beyond its limit or was not marked as truncated");

        var finalText = new string('f', 2000);
        File.WriteAllLines(outputPath,
        [
            JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = "intermediate" }),
            JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = finalText } })
        ]);
        var finalSummary = (string)method.Invoke(null, [outputPath])!;
        Require(finalSummary.Length == 1601 && finalSummary.EndsWith('…')
            && finalSummary[..1600] == new string('f', 1600),
            "Automation final message did not take precedence in the bounded summary");
        return Task.CompletedTask;
    }

    private static Task AutomationOutputRecoveryAsync(string root)
    {
        const System.Reflection.BindingFlags nonPublicStatic = System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static;
        var summaryReader = typeof(MainWindow).GetMethod("ReadAutomationSummary", nonPublicStatic)
            ?? throw new InvalidOperationException("Automation summary reader was not found");
        var threadReader = typeof(MainWindow).GetMethod("ReadAutomationThreadId", nonPublicStatic)
            ?? throw new InvalidOperationException("Automation thread id reader was not found");
        var outputPath = Path.Combine(root, "automation-output-recovery", "stdout.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllLines(outputPath,
        [
            "{malformed json",
            JsonSerializer.Serialize(new { type = "thread.started", thread_id = "recovered-thread" }),
            "{still malformed",
            JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = "recovered final" } })
        ]);
        var threadId = (string?)threadReader.Invoke(null, [outputPath]);
        var summary = (string)summaryReader.Invoke(null, [outputPath])!;
        Require(threadId == "recovered-thread", "A malformed line hid a later thread.started event");
        Require(summary == "recovered final", "A malformed line hid a later assistant final message");
        return Task.CompletedTask;
    }

    private static async Task MalformedControlFileAsync(string root)
    {
        const System.Reflection.BindingFlags nonPublicStatic = System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static;
        var method = typeof(CodexAppServerTurnWorker).GetMethod("ReadControlFileAsync", nonPublicStatic)
            ?? throw new InvalidOperationException("Worker control reader was not found");
        var directory = Path.Combine(root, "malformed-worker-control");
        Directory.CreateDirectory(directory);
        var controlPath = Path.Combine(directory, "control.json");
        var outputPath = Path.Combine(directory, "stdout.jsonl");
        File.WriteAllText(controlPath, "{invalid json");

        var read = (Task<CodexAppServerWorkerControl?>)method.Invoke(null, [controlPath, outputPath])!;
        var control = await read;
        Require(control is null && !File.Exists(controlPath),
            "Malformed worker control was not removed after handling");
        var output = File.ReadAllText(outputPath);
        Require(output.Contains("\"type\":\"control.error\"", StringComparison.Ordinal)
            && output.Contains("\"action\":\"invalid\"", StringComparison.Ordinal),
            "Malformed worker control was not reported to the output log");
    }

    private static async Task WorkerControlValidationAsync(string root)
    {
        const System.Reflection.BindingFlags nonPublicStatic = System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static;
        var validate = typeof(CodexAppServerTurnWorker).GetMethod("GetControlValidationError", nonPublicStatic)
            ?? throw new InvalidOperationException("Worker control validator was not found");
        var reject = typeof(CodexAppServerTurnWorker).GetMethod("RejectControlFileAsync", nonPublicStatic)
            ?? throw new InvalidOperationException("Worker control rejection handler was not found");
        string? Error(CodexAppServerWorkerControl control) => (string?)validate.Invoke(null, [control, true]);

        var missingRequestId = new CodexAppServerWorkerControl { Action = "response", ResponseJson = "{}" };
        Require(Error(missingRequestId) is not null, "A response without RequestId was accepted");
        var malformedRequestId = new CodexAppServerWorkerControl { Action = "response", RequestId = "{bad", ResponseJson = "{}" };
        Require(Error(malformedRequestId) is not null, "A malformed RequestId was accepted");
        var objectRequestId = new CodexAppServerWorkerControl { Action = "response", RequestId = "{}", ResponseJson = "{}" };
        Require(Error(objectRequestId) is not null, "An object RequestId was accepted");
        var malformedResponse = new CodexAppServerWorkerControl { Action = "response", RequestId = "1", ResponseJson = "{bad" };
        Require(Error(malformedResponse) is not null, "A malformed ResponseJson was accepted");
        var validResponse = new CodexAppServerWorkerControl { Action = "response", RequestId = "1", ResponseJson = "{\"ok\":true}" };
        Require(Error(validResponse) is null, "A valid JSON-RPC response was rejected");

        var directory = Path.Combine(root, "invalid-worker-control");
        Directory.CreateDirectory(directory);
        var controlPath = Path.Combine(directory, "response.json");
        var outputPath = Path.Combine(directory, "stdout.jsonl");
        File.WriteAllText(controlPath, JsonSerializer.Serialize(malformedResponse));
        var rejection = (Task)reject.Invoke(null, [controlPath, outputPath, malformedResponse, Error(malformedResponse)!])!;
        await rejection;
        Require(!File.Exists(controlPath), "An invalid ResponseJson control was not removed");
        Require(File.ReadAllText(outputPath).Contains("\"type\":\"control.error\"", StringComparison.Ordinal),
            "An invalid ResponseJson control was not logged");
    }

    private static async Task BoundedProtocolLinesAsync()
    {
        var reader = new BoundedTextLineReader(new StringReader("first\nsecond\r\nthird"), 8);
        Require(await reader.ReadLineAsync() == "first", "The protocol reader lost the first line");
        Require(await reader.ReadLineAsync() == "second", "The protocol reader did not normalize CRLF");
        Require(await reader.ReadLineAsync() == "third", "The protocol reader lost an unterminated final line");
        Require(await reader.ReadLineAsync() is null, "The protocol reader did not report end of stream");

        const string maximumLengthLine = "12345678";
        var exactCrlfReader = new BoundedTextLineReader(new StringReader(maximumLengthLine + "\r\n"), 8);
        Require(await exactCrlfReader.ReadLineAsync() == maximumLengthLine,
            "A CRLF-terminated line at the character limit was rejected or altered");
        var splitCrlfReader = new BoundedTextLineReader(new ChunkedTextReader(maximumLengthLine + "\r\nnext\r\n", 9), 8);
        Require(await splitCrlfReader.ReadLineAsync() == maximumLengthLine,
            "A CRLF split across read buffers counted its carriage return against the limit");
        Require(await splitCrlfReader.ReadLineAsync() == "next",
            "The protocol reader lost the line following a split CRLF");
        var exactUnterminatedReader = new BoundedTextLineReader(new StringReader(maximumLengthLine), 8);
        Require(await exactUnterminatedReader.ReadLineAsync() == maximumLengthLine,
            "An unterminated final line at the character limit was rejected or altered");
        var repeatedCarriageReader = new BoundedTextLineReader(new StringReader("x\r\r\n"), 2);
        Require(await repeatedCarriageReader.ReadLineAsync() == "x\r",
            "The protocol reader removed a carriage return that was part of the line");
        var dataCarriageReader = new BoundedTextLineReader(new ChunkedTextReader("x\rZ\n", 2), 3);
        Require(await dataCarriageReader.ReadLineAsync() == "x\rZ",
            "A carriage return split from the following non-newline character was lost");

        var oversizedReader = new BoundedTextLineReader(new StringReader("123456789"), 8);
        try
        {
            await oversizedReader.ReadLineAsync();
            throw new InvalidOperationException("The protocol reader accepted a line over its limit");
        }
        catch (InvalidDataException) { }

        var oversizedSource = new RepeatingTextReader(totalCharacters: 1_000_000, maximumChunkSize: 4);
        var streamingOversizedReader = new BoundedTextLineReader(oversizedSource, 8);
        try
        {
            await streamingOversizedReader.ReadLineAsync();
            throw new InvalidOperationException("The streaming protocol reader accepted an oversized line");
        }
        catch (InvalidDataException) { }
        Require(oversizedSource.CharactersRead == 12,
            "The protocol reader accumulated an oversized line instead of stopping at the first excess block");

        var truncatedReader = new BoundedTextLineReader(new StringReader("123456789\r\nnext\r\n"), 8, truncateLongLines: true);
        Require(await truncatedReader.ReadLineAsync() == "1234567…", "The output reader did not bound and mark a long line");
        Require(await truncatedReader.ReadLineAsync() == "next", "The output reader did not drain the truncated line");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await new BoundedTextLineReader(new StringReader("pending"), 32).ReadLineAsync(cancellation.Token);
            throw new InvalidOperationException("The protocol reader ignored cancellation");
        }
        catch (OperationCanceledException) { }
    }

    private static async Task BoundedQueueShutdownAsync()
    {
        var startInfo = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--fake-appserver-flood");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the fake app-server peer");
        var processId = process.Id;
        var connection = new CodexAppServerConnection(process);
        try
        {
            var incomingField = typeof(CodexAppServerConnection).GetField("_incoming",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Incoming app-server queue was not found");
            var incoming = (System.Threading.Channels.Channel<JsonElement>)incomingField.GetValue(connection)!;
            Require(incoming.Reader.CanCount, "Incoming queue count is unavailable for the bounded channel");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (incoming.Reader.Count < CodexAppServerConnection.IncomingMessageQueueCapacity
                && DateTime.UtcNow < deadline && !connection.ReaderTask.IsCompleted)
                await Task.Delay(10);
            Require(incoming.Reader.Count == CodexAppServerConnection.IncomingMessageQueueCapacity,
                "Fake app-server did not fill the incoming message queue");

            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Require(connection.ReaderTask.IsCompleted, "Disposal left the stdout reader blocked on the full queue");
            Require(!IsProcessRunning(processId), "Disposal left the fake app-server process running");
        }
        finally
        {
            if (IsProcessRunning(processId))
            {
                try
                {
                    using var leftover = Process.GetProcessById(processId);
                    leftover.Kill(entireProcessTree: true);
                    await leftover.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }

        static bool IsProcessRunning(int id)
        {
            try
            {
                using var candidate = Process.GetProcessById(id);
                return !candidate.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }

    private static async Task AppServerConnectionCancellationAsync()
    {
        var startInfo = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--listen");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the fake app-server peer");
        var processId = process.Id;

        await using (var connection = new CodexAppServerConnection(process))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => connection.InitializeAsync(cancellation.Token));
        }

        try
        {
            using var leftover = Process.GetProcessById(processId);
            Require(leftover.HasExited, "Cancelled app-server initialization left its child running");
        }
        catch (ArgumentException) { }
    }

    private static async Task TerminalHostStartupCancellationAsync(string root)
    {
        var marker = Path.Combine(root, "terminal-initialize.pid");
        var host = new CodexAppServerTerminalHost(() =>
        {
            var startInfo = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--fake-appserver-block-initialize");
            startInfo.ArgumentList.Add(marker);
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the blocking app-server peer");
            try { return Task.FromResult(new CodexAppServerConnection(process)); }
            catch
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                process.Dispose();
                throw;
            }
        });
        var start = host.StartSessionAsync("terminal-startup-cancel", root, fullAccess: false);
        try
        {
            await WaitForFileAsync(marker);
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await ThrowsAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));

            var processId = int.Parse(await File.ReadAllTextAsync(marker));
            try
            {
                using var leftover = Process.GetProcessById(processId);
                Require(leftover.HasExited, "Closing the terminal host left its initializing App Server running");
            }
            catch (ArgumentException) { }
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task TerminalTerminateTimeoutAsync(string root)
    {
        var startedMarker = Path.Combine(root, "terminal-control-started.pid");
        var terminateMarker = Path.Combine(root, "terminal-control-terminate.pid");
        var host = new CodexAppServerTerminalHost(() =>
        {
            var startInfo = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--fake-appserver-terminal-control");
            startInfo.ArgumentList.Add(startedMarker);
            startInfo.ArgumentList.Add(terminateMarker);
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the fake terminal app-server peer");
            try { return Task.FromResult(new CodexAppServerConnection(process)); }
            catch
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                process.Dispose();
                throw;
            }
        }, TimeSpan.FromMilliseconds(150));

        try
        {
            const string processId = "terminal-control-timeout";
            await host.StartSessionAsync(processId, root, fullAccess: false);
            await WaitForFileAsync(startedMarker);

            var terminated = await host.TerminateSessionAsync(processId).WaitAsync(TimeSpan.FromSeconds(3));
            Require(!terminated, "A blocked terminal terminate RPC reported success");
            Require(File.Exists(terminateMarker), "The fake server did not receive the terminal terminate request");
            Require(await host.TerminateSessionAsync(processId), "A terminal terminate retry did not recover");
        }
        finally
        {
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task TerminalHostReconnectAsync(string root)
    {
        var disconnectedMarker = Path.Combine(root, "terminal-disconnected.pid");
        var reconnectedMarker = Path.Combine(root, "terminal-reconnected.pid");
        var terminateMarker = Path.Combine(root, "terminal-reconnected-terminate.pid");
        var connectionCount = 0;
        var host = new CodexAppServerTerminalHost(() =>
        {
            var startInfo = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (Interlocked.Increment(ref connectionCount) == 1)
            {
                startInfo.ArgumentList.Add("--fake-appserver-drop-after-initialize");
                startInfo.ArgumentList.Add(disconnectedMarker);
            }
            else
            {
                startInfo.ArgumentList.Add("--fake-appserver-terminal-control");
                startInfo.ArgumentList.Add(reconnectedMarker);
                startInfo.ArgumentList.Add(terminateMarker);
            }

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start a fake terminal app-server peer");
            try { return Task.FromResult(new CodexAppServerConnection(process)); }
            catch
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                process.Dispose();
                throw;
            }
        }, TimeSpan.FromMilliseconds(150));

        try
        {
            try { await host.StartSessionAsync("terminal-before-disconnect", root, fullAccess: false).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            await WaitForFileAsync(disconnectedMarker);
            var disconnectedProcessId = int.Parse(await File.ReadAllTextAsync(disconnectedMarker));
            try
            {
                using var disconnectedProcess = Process.GetProcessById(disconnectedProcessId);
                await disconnectedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException) { }

            using var retryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var retry = 0;
            while (!File.Exists(reconnectedMarker))
            {
                retryDeadline.Token.ThrowIfCancellationRequested();
                try
                {
                    await host.StartSessionAsync($"terminal-after-disconnect-{++retry}", root, fullAccess: false)
                        .WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (IOException) { }
                catch (InvalidOperationException) { }
                if (!File.Exists(reconnectedMarker)) await Task.Delay(25, retryDeadline.Token);
            }

            Require(Volatile.Read(ref connectionCount) >= 2, "Terminal host reused the lost App Server connection");
        }
        finally
        {
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static Task TerminalOutputBatchBufferAsync()
    {
        var buffer = new TerminalOutputBatchBuffer(maximumCharacters: 8, maximumChunks: 4);
        Require(buffer.Enqueue(new CodexTerminalOutput("shell", "1234")), "The first terminal chunk did not schedule a flush");
        Require(!buffer.Enqueue(new CodexTerminalOutput("shell", "5678")), "Terminal chunks did not coalesce into one pending flush");
        Require(!buffer.Enqueue(new CodexTerminalOutput("shell", "90")), "An overflowing terminal chunk scheduled a duplicate flush");
        var batch = buffer.Drain();
        Require(batch.TruncatedProcessId == "shell", "Overflow was not associated with its terminal session");
        Require(string.Concat(batch.Items.Select(item => item.Text)) == "567890", "The output buffer did not preserve the newest terminal text");
        Require(buffer.Enqueue(new CodexTerminalOutput("shell", "new")), "Draining did not reset flush scheduling");

        var oversizedBuffer = new TerminalOutputBatchBuffer(maximumCharacters: 4, maximumChunks: 2);
        Require(oversizedBuffer.Enqueue(new CodexTerminalOutput("shell", "abcdef")), "An oversized first chunk did not schedule a flush");
        var oversizedBatch = oversizedBuffer.Drain();
        Require(oversizedBatch.TruncatedProcessId == "shell"
            && string.Concat(oversizedBatch.Items.Select(item => item.Text)) == "cdef",
            "A single oversized chunk was not clipped to the configured limit");
        return Task.CompletedTask;
    }

    private static async Task<int> FakeAppServerFloodAsync()
    {
        try
        {
            const string message = "{\"method\":\"test/notification\",\"params\":{}}";
            for (var index = 0; index < 4096; index++)
                await Console.Out.WriteLineAsync(message);
            await Console.Out.FlushAsync();
            await Console.In.ReadToEndAsync();
        }
        catch (IOException) { }
        return 0;
    }

    private static async Task<int> FakeAppServerIgnoreShutdownAsync()
    {
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task AppServerShutdownTimeoutAsync()
    {
        var startInfo = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--fake-appserver-ignore-shutdown");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the unresponsive fake app-server peer");
        var processId = process.Id;
        var connection = new CodexAppServerConnection(process);
        try
        {
            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                using var leftover = Process.GetProcessById(processId);
                Require(leftover.HasExited, "DisposeAsync returned while the unresponsive app-server was still running");
            }
            catch (ArgumentException) { }
        }
        finally
        {
            try
            {
                using var leftover = Process.GetProcessById(processId);
                if (!leftover.HasExited)
                {
                    leftover.Kill(entireProcessTree: true);
                    await leftover.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
    }

    private static Task WorkspaceSearchAsync(string root)
    {
        var directory = Path.Combine(root, "workspace-search");
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        Directory.CreateDirectory(Path.Combine(directory, "node_modules"));
        File.WriteAllText(Path.Combine(directory, "src", "sample.cs"), "first\nNeedle on line two\nlast");
        File.WriteAllText(Path.Combine(directory, "needle.txt"), "name match");
        File.WriteAllText(Path.Combine(directory, "node_modules", "ignored.txt"), "needle");
        File.WriteAllBytes(Path.Combine(directory, "binary.dat"), [0, 110, 101, 101, 100, 108, 101]);
        var result = WorkspaceFileSearch.Find(directory, "NEEDLE", CancellationToken.None);
        Require(result.Hits.Count == 2, "Expected one content match and one filename match");
        Require(result.Hits.Any(hit => hit.Path.EndsWith("sample.cs") && hit.Line == 2), "Wrong content match line");
        Require(result.Hits.Any(hit => hit.Path.EndsWith("needle.txt")), "Filename search failed");
        Require(result.Skipped == 1 && !result.Limited, "Binary exclusion or limit reporting failed");
        return Task.CompletedTask;
    }

    private static Task WorkspaceSearchLimitsAsync(string root)
    {
        var directory = Path.Combine(root, "workspace-search-limits");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "matches.txt"), string.Join('\n', Enumerable.Repeat("needle", 250)));
        var result = WorkspaceFileSearch.Find(directory, "needle", CancellationToken.None);
        Require(result.Hits.Count == 200 && result.Limited, "Search result limit was not enforced");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try
        {
            WorkspaceFileSearch.Find(directory, "needle", cancellation.Token);
            throw new InvalidOperationException("Cancelled search completed normally");
        }
        catch (OperationCanceledException) { }
        return Task.CompletedTask;
    }

    private static Task WorkspaceMapAsync(string root)
    {
        var directory = Path.Combine(root, "workspace-map");
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        Directory.CreateDirectory(Path.Combine(directory, "node_modules"));
        File.WriteAllText(Path.Combine(directory, "src", "sample.cs"), "class Sample {}");
        File.WriteAllText(Path.Combine(directory, "app.csproj"), "<Project><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Example.Package\" Version=\"1.0\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(directory, "package.json"), "{\"dependencies\":{\"example-module\":\"1.0\"}}");
        var folders = WorkspaceProjectMap.Read(directory, 0);
        Require(folders.Count == 1 && folders[0].Label == "src", "Map includes ignored folders");
        var dependencies = WorkspaceProjectMap.Read(directory, 1);
        Require(dependencies.Any(node => node.Label == "Example.Package") && dependencies.Any(node => node.Label == "net10.0-windows")
            && dependencies.Any(node => node.Label == "example-module"), "Map lost declared dependencies");
        Require(WorkspaceProjectMap.Read(directory, 2).Any(node => node.Label == ".cs · 1"), "File-type layer missing");
        return Task.CompletedTask;
    }

    private static Task MaximizedWorkAreaAsync()
    {
        var secondaryMonitorWithTopTaskbar = MainWindow.CalculateMaximizedWorkArea(-1920, 0, -1920, 40, 0, 1080);
        Require(secondaryMonitorWithTopTaskbar == (0, 40, 1920, 1040),
            "Maximize bounds ignored the top taskbar on a monitor with negative coordinates");

        var primaryMonitorWithLeftTaskbar = MainWindow.CalculateMaximizedWorkArea(0, 0, 80, 0, 1920, 1080);
        Require(primaryMonitorWithLeftTaskbar == (80, 0, 1840, 1080),
            "Maximize bounds ignored the left taskbar");
        return Task.CompletedTask;
    }

    private static Task WorkspaceControlsAsync(string root)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            App? application = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                // Load real resources without Application.Run/OnStartup: no account, state, workers or schedules.
                application = new App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                application.InitializeComponent();
                using var source = typeof(MainWindow).Assembly.GetManifestResourceStream("CodexGUI.Views.WorkspaceView.xaml")!;
                using var reader = new StreamReader(source);
                var workspace = (System.Windows.Controls.Grid)System.Windows.Markup.XamlReader.Parse(reader.ReadToEnd());
                workspace.ColumnDefinitions[0].Width = new System.Windows.GridLength(0);
                workspace.ColumnDefinitions[1].Width = new System.Windows.GridLength(400);
                workspace.ColumnDefinitions[3].Width = new System.Windows.GridLength(398);
                var newChatButton = (System.Windows.Controls.Button)workspace.FindName("NewChatButton");
                Require(newChatButton.Content?.ToString()?.Contains("Новый чат", StringComparison.Ordinal) == true
                    && System.Windows.Controls.Grid.GetColumn(newChatButton) == 1
                    && System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(newChatButton),
                    "New chat button is not visible and interactive in the workspace header");
                var updateButton = (System.Windows.Controls.Button)workspace.FindName("UpdateButton");
                Require(System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(updateButton),
                    "Update button is not interactive in the custom title bar");
                var updateFrames = MainWindow.LoadUpdateReadyAnimationFrames();
                Require(updateFrames.Length == 8 && updateFrames.All(frame => frame.IsFrozen
                    && frame.PixelWidth == frame.PixelHeight && frame.PixelWidth > 0),
                    "Update animation frames are missing, unfrozen or not square");
                workspace.Background = WorkspaceBackdrop.Create();
                Require(workspace.Background.IsFrozen && ReferenceEquals(workspace.Background, WorkspaceBackdrop.Create()), "Background is not cached and frozen");
                var tree = (System.Windows.Controls.TreeView)workspace.FindName("FileTree");
                var folder = new System.Windows.Controls.TreeViewItem { Header = "src", IsExpanded = true };
                var file = new System.Windows.Controls.TreeViewItem { Header = "sample.cs" };
                folder.Items.Add(file); tree.Items.Add(folder);
                workspace.Measure(new System.Windows.Size(1585, 992));
                workspace.Arrange(new System.Windows.Rect(0, 0, 1585, 992)); workspace.UpdateLayout();
                folder.IsExpanded = false; workspace.UpdateLayout();
                var host = (System.Windows.Controls.ItemsPresenter)folder.Template.FindName("ItemsHost", folder);
                Require(host.Visibility == System.Windows.Visibility.Collapsed, "Collapsed tree folder still displays children");
                folder.IsExpanded = true; file.IsSelected = true; workspace.UpdateLayout();
                Require(host.Visibility == System.Windows.Visibility.Visible, "Expanded tree folder lost children");
                var selection = (System.Windows.Controls.Border)file.Template.FindName("Selection", file);
                Require(((System.Windows.Media.SolidColorBrush)selection.Background).Color == System.Windows.Media.Color.FromRgb(26, 43, 52), "Selected tree row lost the reference highlight");
                var canvas = new System.Windows.Controls.Canvas { Width = 300, Height = 230 };
                string? selected = null;
                var path = Path.Combine(root, "workspace-search", "src");
                WorkspaceMapView.Render(canvas, [new WorkspaceMapNode("src", path)], root, 1, value => selected = value);
                var node = canvas.Children.OfType<System.Windows.Controls.Button>().First();
                node.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Require(selected == path, "Map selected the wrong directory");
                canvas.Measure(new System.Windows.Size(300, 230));
                canvas.Arrange(new System.Windows.Rect(0, 0, 300, 230));
                Require(canvas.Children.OfType<System.Windows.Controls.Button>().All(button => button.ActualWidth > 0), "Map nodes have no layout");

                var window = new WorkspaceSearchWindow(Path.Combine(root, "workspace-search"), (_, _) => Task.CompletedTask);
                var dock = (System.Windows.Controls.DockPanel)window.Content;
                var header = (System.Windows.Controls.StackPanel)dock.Children[0];
                var input = (System.Windows.Controls.TextBox)header.Children[0];
                var search = (System.Windows.Controls.Button)header.Children[1];
                var status = (System.Windows.Controls.TextBlock)header.Children[2];
                input.Text = "needle";
                search.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                var started = Stopwatch.StartNew();
                timer.Tick += (_, _) => { if (status.Text != "Поиск…" || started.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false; };
                timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); timer.Stop();
                var list = (System.Windows.Controls.ListBox)dock.Children[1];
                Require(list.Items.Count == 2, "Search button did not display its results");
                window.Close();
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
            finally { application?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static Task WorkspaceGoalStateAsync()
    {
        var type = typeof(MainWindow).GetNestedType("StoredThread", System.Reflection.BindingFlags.NonPublic)!;
        var stored = JsonSerializer.Deserialize("{\"Id\":\"example\",\"LastActivityAt\":\"2026-10-24T12:00:00Z\",\"GoalSteps\":[{\"Text\":\"Implement chat\",\"IsComplete\":true}]}", type)!;
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(stored, type));
        var step = result.RootElement.GetProperty("GoalSteps")[0];
        Require(step.GetProperty("Text").GetString() == "Implement chat" && step.GetProperty("IsComplete").GetBoolean(), "Goal steps lost state");
        Require(result.RootElement.GetProperty("LastActivityAt").GetDateTimeOffset() == DateTimeOffset.Parse("2026-10-24T12:00:00Z"), "Recent-chat timestamp lost state");
        var legacy = JsonSerializer.Deserialize("{\"Id\":\"legacy\"}", type)!;
        using var legacyResult = JsonDocument.Parse(JsonSerializer.Serialize(legacy, type));
        Require(legacyResult.RootElement.GetProperty("GoalSteps").GetArrayLength() == 0, "Legacy state needs a migration");
        return Task.CompletedTask;
    }

    private static Task GitContextAsync()
    {
        var snapshot = new GitRepositorySnapshot(true, "C:\\project", "main", "origin/main", 2, 1, null,
            true, true,
            [
                new RepositoryFileStatus("src/changed.cs", 'M', '.', false),
                new RepositoryFileStatus("notes\nforged-status.txt", '?', '?', true),
                new RepositoryFileStatus("src/local.cs", '.', 'M', false)
            ], null);
        var context = GitRepositoryInspector.FormatAgentContext(snapshot, maximumFiles: 2);
        Require(context.Contains("Ветка: main", StringComparison.Ordinal), "Current branch was omitted");
        Require(context.Contains("в индексе: 1, изменены: 1, новые: 1", StringComparison.Ordinal), "Status counts are wrong");
        Require(context.Contains("notes\\nforged-status.txt", StringComparison.Ordinal)
            && !context.Contains("notes\nforged-status.txt", StringComparison.Ordinal),
            "Filename newline was not escaped");
        Require(!context.Contains("src/local.cs", StringComparison.Ordinal) && context.Contains("не показано файлов: 1", StringComparison.Ordinal),
            "Context file limit was not enforced");
        return Task.CompletedTask;
    }

    private static Task GitPreflightAsync()
    {
        var snapshot = new GitRepositorySnapshot(true, "C:\\project", "(detached)", null, 0, 3, null,
            false, true,
            [new RepositoryFileStatus("conflicted\\nname.cs", 'U', 'U', false, true)], null);
        var context = GitRepositoryInspector.FormatAgentContext(snapshot);
        Require(context.Contains("Рабочая папка: C:\\project", StringComparison.Ordinal), "Working directory was omitted");
        Require(context.Contains("detached HEAD", StringComparison.Ordinal), "Detached HEAD warning was omitted");
        Require(context.Contains("upstream не настроен", StringComparison.Ordinal), "Missing upstream warning was omitted");
        Require(context.Contains("неразрешённых Git-конфликтов: 1", StringComparison.Ordinal), "Conflict warning was omitted");
        Require(context.Contains("[конфликт]", StringComparison.Ordinal), "Conflicted file was not marked");

        var behind = snapshot with { Branch = "main", Upstream = "origin/main", HasHead = false };
        var behindContext = GitRepositoryInspector.FormatAgentContext(behind);
        Require(behindContext.Contains("отстаёт от upstream", StringComparison.Ordinal), "Behind warning was omitted");
        Require(behindContext.Contains("HEAD отсутствует", StringComparison.Ordinal), "Unborn HEAD warning was omitted");
        return Task.CompletedTask;
    }

    private static Task GitHeadBaselineAsync()
    {
        const string head = "0123456789abcdef0123456789abcdef01234567";
        var status = GitRepositoryInspector.ParseStatus("# branch.oid " + head + "\0# branch.head main\0");
        Require(status.HasHead && status.HeadCommit == head, "Git status lost the baseline revision");

        var snapshot = new GitRepositorySnapshot(true, @"C:\project", "main", "origin/main", 0, 0,
            null, true, true, [], null) { HeadCommit = status.HeadCommit };
        Require(GitRepositoryInspector.FormatAgentContext(snapshot).Contains("Исходный HEAD-коммит: " + head,
            StringComparison.Ordinal), "Agent preflight omitted the baseline revision");

        var unborn = GitRepositoryInspector.ParseStatus("# branch.oid (initial)\0");
        Require(!unborn.HasHead && unborn.HeadCommit is null, "Unborn HEAD was treated as a commit");
        var malformed = GitRepositoryInspector.ParseStatus("# branch.oid suspicious\ncontent\0");
        Require(malformed.HeadCommit is null, "Invalid object ID was exposed to the agent");
        return Task.CompletedTask;
    }

    private static Task GitSecretsAsync()
    {
        var snapshot = new GitRepositorySnapshot(true, "C:\\project", "main", "origin/main", 0, 0, null,
            true, true,
            [
                new RepositoryFileStatus(".env.local", 'A', '.', false),
                new RepositoryFileStatus("certs/private.pem", '?', '?', true),
                new RepositoryFileStatus(".env.example", '?', '?', true)
            ], null);
        var context = GitRepositoryInspector.FormatAgentContext(snapshot, maximumFiles: 1);
        Require(context.Contains("потенциально чувствительных файлов: 2 (в индексе: 1)", StringComparison.Ordinal),
            "Potential secret warning did not include staged and untracked files");
        Require(context.Contains("Проверены только имена файлов", StringComparison.Ordinal),
            "Filename-only detection limitations were not explained");
        Require(GitRepositoryInspector.IsPotentialSecretFile("keys/id_ed25519"), "Private key not recognized");
        Require(GitRepositoryInspector.IsPotentialSecretFile(@"config\.npmrc"), "Windows separator not recognized");
        Require(!GitRepositoryInspector.IsPotentialSecretFile(".env.sample"), "Safe sample flagged");
        Require(!GitRepositoryInspector.IsPotentialSecretFile("src/config.json"), "Ordinary config flagged");
        return Task.CompletedTask;
    }

    private static Task StagedSecretsAsync()
    {
        var output = ".env.local\0docs/notes.txt\0secrets/private\nkey.pem\0.env.example\0"
            + ".aws/credentials\0.docker/config.json\0";
        var found = GitRepositoryInspector.FindPotentialSecretsInNullDelimitedPaths(output);
        Require(found.Count == 4, "Unexpected sensitive file matches");
        Require(found[0] == ".env.local" && found[1] == "secrets/private\nkey.pem"
            && found[2] == ".aws/credentials" && found[3] == ".docker/config.json",
            "NUL-delimited sensitive file names were corrupted");
        Require(GitRepositoryInspector.IsPotentialSecretFile(".git-credentials"),
            "Git credentials were not recognized");
        return Task.CompletedTask;
    }

    private static Task ProjectInventoryAsync(string root)
    {
        var project = Path.Combine(root, "inventory");
        Directory.CreateDirectory(Path.Combine(project, "Build"));
        Directory.CreateDirectory(Path.Combine(project, "Tests"));
        File.WriteAllText(Path.Combine(project, "AGENTS.md"), "never expose this secret marker");
        File.WriteAllText(Path.Combine(project, "Build", "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(project, "Tests", "App.Tests.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(project, ".env"), "SECRET=never");
        var hints = LocalProjectInventory.FormatAgentHints(project);
        Require(hints.Contains("AGENTS.md", StringComparison.Ordinal)
            && hints.Contains("Build/App.csproj", StringComparison.Ordinal)
            && hints.Contains("Tests/App.Tests.csproj", StringComparison.Ordinal),
            "Project instructions or .NET entry points were omitted");
        Require(!hints.Contains("SECRET=", StringComparison.Ordinal)
            && !hints.Contains(".env", StringComparison.Ordinal)
            && !hints.Contains("never expose", StringComparison.Ordinal),
            "Project inventory exposed sensitive content or unrelated filenames");
        var limited = LocalProjectInventory.FormatAgentHints(project, maximumFiles: 1);
        Require(limited.Contains("список сокращён", StringComparison.Ordinal)
            && !limited.Contains("Build/App.csproj", StringComparison.Ordinal),
            "Project inventory size limit was not enforced");
        Require(LocalProjectInventory.FormatAgentHints(Path.Combine(root, "missing")) == "",
            "Nonexistent projects should not produce hints");
        return Task.CompletedTask;
    }

    private static Task GitContextEscapingAsync()
    {
        var snapshot = new GitRepositorySnapshot(true, "C:\\project", "main", "origin/main", 0, 0, null,
            true, true, [new RepositoryFileStatus("src/\u001b[31m\u202Efake.cs", '.', 'M', false)], null);
        var context = GitRepositoryInspector.FormatAgentContext(snapshot);
        Require(context.Contains(@"src/\u001B[31m\u202Efake.cs", StringComparison.Ordinal),
            "Escape and bidi formatting characters were not neutralized");
        Require(!context.Contains('\u001b') && !context.Contains('\u202E'),
            "Agent context retained terminal or bidi control characters");
        return Task.CompletedTask;
    }

    private static Task GitStatusScaleAsync()
    {
        var porcelain = new StringBuilder("# branch.head main\0# branch.upstream origin/main\0# branch.ab +5 -2\0");
        for (var index = 0; index < 8_000; index++)
            porcelain.Append("? dir/file-").Append(index).Append(".txt\0");
        porcelain.Append("2 R. N... 100644 100644 100644 abc def R100 renamed file.txt\0old file.txt\0");
        porcelain.Append("? trailing\nname.txt\0");
        var parsed = GitRepositoryInspector.ParseStatus(porcelain.ToString());
        Require(parsed.Branch == "main" && parsed.Upstream == "origin/main"
            && parsed.Ahead == 5 && parsed.Behind == 2, "Branch metadata was lost");
        Require(parsed.Changes.Count == 8_002, "Large porcelain input was not fully parsed");
        Require(parsed.Changes[^2].Path == "renamed file.txt" && !parsed.Changes[^2].IsUntracked,
            "Rename source was incorrectly counted as a change");
        Require(parsed.Changes[^1].Path == "trailing\nname.txt",
            "Git filename containing newline was damaged");
        return Task.CompletedTask;
    }

    private static Task GitConflictParsingAsync()
    {
        const string status = "# branch.head main\0u UU N... 100644 100644 100644 100644 abc def ghi conflict.cs\0";
        var parsed = GitRepositoryInspector.ParseStatus(status);
        Require(parsed.Changes.Count == 1 && parsed.Changes[0].Path == "conflict.cs"
            && parsed.Changes[0].IsConflicted, "Unmerged Git status was not identified");
        return Task.CompletedTask;
    }

    private static Task GitNumstatAsync()
    {
        var report = "3\t1\tsrc\n12\t15\tfake.cs\0"
            + "4\t2\t\0"
            + "30\t40\told-name-with-tabs\0"
            + "6\t7\tnew-name-with-tabs\0"
            + "-\t-\timage.png\0";
        // The first filename contains a newline and numeric-looking tab fields.
        var counts = GitRepositoryInspector.ParseNumstat(report);
        Require(counts == (7L, 3L), "Numstat counted embedded path data or renamed filenames");
        return Task.CompletedTask;
    }

    private static async Task TextLineCountingAsync(string root)
    {
        var path = Path.Combine(root, "line-counter.txt");
        await File.WriteAllTextAsync(path, new string('x', 20_000) + "\nlast line");
        Require(await GitRepositoryInspector.CountTextLinesAsync(path, CancellationToken.None) == 2,
            "Line count across read buffers was incorrect");
        await File.WriteAllTextAsync(path, "one\ntwo\n");
        Require(await GitRepositoryInspector.CountTextLinesAsync(path, CancellationToken.None) == 2,
            "Final newline was double-counted");
        await File.WriteAllBytesAsync(path, [65, 0, 66, 10]);
        Require(await GitRepositoryInspector.CountTextLinesAsync(path, CancellationToken.None) is null,
            "Binary file should not contribute text lines");
        await File.WriteAllTextAsync(path, "");
        Require(await GitRepositoryInspector.CountTextLinesAsync(path, CancellationToken.None) == 0,
            "Empty text file should have zero lines");
        await using (var oversized = new FileStream(path, FileMode.Create, FileAccess.Write))
            oversized.SetLength(4L * 1024 * 1024 + 1);
        Require(await GitRepositoryInspector.CountTextLinesAsync(path, CancellationToken.None) is null,
            "Oversized file should be skipped");
    }

    private static async Task CliOutputBoundAsync()
    {
        using var reader = new StringReader("abcdefghij\nnext line");
        var captured = await CodexCliService.ReadBoundedOutputAsync(reader, 8);
        Require(captured.ExceededLimit && captured.Text == "abcdefgh", "CLI output capture did not cap stored characters");
        Require(await reader.ReadToEndAsync() == "", "CLI output capture did not drain excess output");

        var shortOutput = await CodexCliService.ReadBoundedOutputAsync(new StringReader("short output"), 32);
        Require(!shortOutput.ExceededLimit && shortOutput.Text == "short output", "CLI output capture changed a short response");
    }

    private static Task GitHubRemotesAsync()
    {
        foreach (var remote in new[]
        {
            "https://github.com/rkfsociety/CodexGUI.git",
            "git@github.com:rkfsociety/CodexGUI.git",
            "ssh://git@github.com/rkfsociety/CodexGUI.git"
        })
        {
            Require(GitHubPullRequestService.TryGetRepositorySlug(remote, out var slug)
                && slug == "rkfsociety/CodexGUI", "GitHub origin was not recognized: " + remote);
        }

        foreach (var remote in new[]
        {
            "https://github.com.evil.example/rkfsociety/CodexGUI",
            "git@github.com:rkfsociety/../other",
            "https://github.com/rkfsociety/CodexGUI/extra",
            "git@github.com:rkfsociety/repo with spaces"
        })
        {
            Require(!GitHubPullRequestService.TryGetRepositorySlug(remote, out _),
                "Malformed GitHub origin was accepted: " + remote);
        }

        return Task.CompletedTask;
    }

    private static async Task Check(string name, Func<Task> check)
    {
        await check();
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static Task RecoveryAsync(string root)
    {
        var path = Path.Combine(root, "recovery.json");
        AtomicJsonFile.Write(path, new[] { "previous" });
        AtomicJsonFile.Write(path, new[] { "current" }, backupPath: path + ".bak");
        File.WriteAllText(path, "broken");
        var recovered = AtomicJsonFile.ReadWithBackup<string[]>(path, _ => { });
        Require(recovered.SequenceEqual(new[] { "previous" }), "Wrong snapshot recovered");
        Require(JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))![0] == "previous", "Primary was not repaired");
        var preserved = Directory.GetFiles(root, "recovery.json.unreadable-*.bak");
        Require(preserved.Length == 1 && File.ReadAllText(preserved[0]) == "broken", "Corrupt primary was not preserved");
        AtomicJsonFile.Write(path, new[] { "next" }, backupPath: path + ".bak");
        Require(JsonSerializer.Deserialize<string[]>(File.ReadAllText(path + ".bak"))![0] == "previous", "Repair did not protect the good backup");
        return Task.CompletedTask;
    }

    private static async Task BrokenRecoveryAsync(string root)
    {
        var path = Path.Combine(root, "broken.json");
        File.WriteAllText(path, "broken-primary");
        File.WriteAllText(path + ".bak", "broken-backup");
        await ThrowsAsync<InvalidDataException>(() =>
        {
            AtomicJsonFile.ReadWithBackup<string[]>(path, _ => { });
            return Task.CompletedTask;
        });
        Require(File.ReadAllText(path) == "broken-primary" && File.ReadAllText(path + ".bak") == "broken-backup", "Recovery modified unreadable snapshots");
    }

    private static async Task QueueAsync(string root)
    {
        var writer = new SerializedJsonWriter();
        var path = Path.Combine(root, "queued", "state.json");
        var values = new List<int> { 1 };
        var first = writer.Enqueue(path, JsonSerializer.SerializeToElement(values), backupPath: path + ".bak");
        values[0] = 2;
        var second = writer.Enqueue(path, JsonSerializer.SerializeToElement(values), backupPath: path + ".bak");
        values[0] = 3;
        await Task.WhenAll(first, second);
        Require(JsonSerializer.Deserialize<int[]>(File.ReadAllText(path))![0] == 2, "Latest snapshot was mutated or reordered");
        Require(JsonSerializer.Deserialize<int[]>(File.ReadAllText(path + ".bak"))![0] == 1, "First snapshot was mutated or reordered");
        Require(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "Temporary files leaked");
    }

    private static async Task QueueRetryAsync(string root)
    {
        var writer = new SerializedJsonWriter();
        var path = Path.Combine(root, "retry.json");
        Directory.CreateDirectory(path); // A directory cannot be replaced by a JSON file.
        await ThrowsAsync<UnauthorizedAccessException>(() => writer.Enqueue(path, JsonSerializer.SerializeToElement(1)));
        Directory.Delete(path);
        await writer.Enqueue(path, JsonSerializer.SerializeToElement(2));
        Require(File.ReadAllText(path) == "2", "Write queue remained poisoned");
    }

    private static async Task ActionsAsync(string root)
    {
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        CodexProjectActionStore.Save(project, new[] { Action("base") }, null);
        _ = CodexProjectActionStore.Load(project, out var revision);
        var configPath = Path.GetFullPath(Path.Combine(project, ".codex", "codexgui-actions.json"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configPath.ToUpperInvariant())));
        // Hold the production lock on a dedicated thread (Mutex ownership is thread-affine).
        using var lockHeld = new ManualResetEventSlim();
        using var releaseLock = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, @"Local\CodexGUI.ProjectActions." + hash);
            mutex.WaitOne();
            lockHeld.Set();
            releaseLock.Wait();
            mutex.ReleaseMutex();
        });
        Require(lockHeld.Wait(TimeSpan.FromSeconds(5)), "Lock holder did not start");
        var marker1 = Path.Combine(root, "writer1.ready");
        var marker2 = Path.Combine(root, "writer2.ready");
        using var first = Start("write-actions", project, revision!, "first", marker1);
        using var second = Start("write-actions", project, revision!, "second", marker2);
        try
        {
            await WaitForFileAsync(marker1);
            await WaitForFileAsync(marker2);
            // Each writer signals immediately before entering Save. Neither may bypass the held lock.
            await Task.Delay(150);
            Require(!first.HasExited && !second.HasExited, "A writer bypassed the cross-process lock");
        }
        finally { releaseLock.Set(); }
        await holder;
        await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        Require(new[] { first.ExitCode, second.ExitCode }.Order().SequenceEqual(new[] { 0, 3 }), "Expected one success and one revision conflict");
        var saved = CodexProjectActionStore.Load(project, out _);
        Require(saved.Count == 1 && saved[0].Name is "first" or "second", "Invalid saved action");
    }

    private static CodexActionDefinition Action(string name) => new() { Name = name, Prompt = "Prompt " + name };

    private static int WriteActions(string[] args)
    {
        File.WriteAllText(args[4], "ready");
        try { CodexProjectActionStore.Save(args[1], new[] { Action(args[3]) }, args[2]); return 0; }
        catch (IOException error) when (error.Message.Contains("изменился после открытия", StringComparison.Ordinal)) { return 3; }
    }

    private static Task SchedulesAsync(string root)
    {
        var path = Path.Combine(root, "schedules.json");
        CodexAutomationStore.SaveDefinitions(new[] { new CodexAutomationDefinition { Name = "previous" } }, path);
        CodexAutomationStore.SaveDefinitions(new[] { new CodexAutomationDefinition { Name = "current" } }, path);
        File.WriteAllText(path, "broken");
        Require(CodexAutomationStore.LoadDefinitions(path).Single().Name == "previous", "Schedule backup was not recovered");
        Require(File.ReadAllText(Directory.GetFiles(root, "schedules.json.unreadable-*.bak").Single()) == "broken", "Corrupt schedules not preserved");
        File.Delete(path);
        Require(CodexAutomationStore.LoadDefinitions(path).Single().Name == "previous", "Missing primary did not recover from backup");
        return Task.CompletedTask;
    }

    private static async Task BrokenSchedulesAsync(string root)
    {
        var path = Path.Combine(root, "bad-schedules.json");
        File.WriteAllText(path, "[null]");
        File.WriteAllText(path + ".bak", "bad-backup");
        await ThrowsAsync<InvalidDataException>(() => Task.FromResult(CodexAutomationStore.LoadDefinitions(path)));
        await ThrowsAsync<InvalidDataException>(() =>
        {
            CodexAutomationStore.SaveDefinitions(Array.Empty<CodexAutomationDefinition>(), path);
            return Task.CompletedTask;
        });
        Require(File.ReadAllText(path) == "[null]", "Blocked save overwrote damaged schedules");
        File.WriteAllText(path, "[]");
        _ = CodexAutomationStore.LoadDefinitions(path);
        CodexAutomationStore.SaveDefinitions(new[] { new CodexAutomationDefinition { Name = "repaired" } }, path);
        Require(CodexAutomationStore.LoadDefinitions(path).Single().Name == "repaired", "Successful reload did not unblock saving");
    }

    private static Task IntervalAsync()
    {
        var anchor = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var definition = new CodexAutomationDefinition { Cadence = "interval", IntervalMinutes = 15 };
        Require(CodexAutomationStore.CalculateNextRun(definition, anchor.AddMinutes(47), anchor) == anchor.AddMinutes(60), "Interval drifted");
        return Task.CompletedTask;
    }

    private static Task MonthlyAsync()
    {
        var localNow = new DateTimeOffset(new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Local));
        var definition = new CodexAutomationDefinition { Cadence = "monthly", DayOfMonth = 31, TimeOfDay = "09:00" };
        var next = CodexAutomationStore.CalculateNextRun(definition, localNow).ToLocalTime();
        Require(next.Month == 5 && next.Day == 31 && next.Hour == 9, "Monthly schedule did not skip April");
        return Task.CompletedTask;
    }

    private static Task RecurrenceAsync()
    {
        var start = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Local);
        var definition = new CodexAutomationDefinition { Cadence = "rrule", RecurrenceRule = "FREQ=DAILY;COUNT=1", RecurrenceStartLocal = start };
        CodexAutomationStore.CalculateNextOccurrence(definition, new DateTimeOffset(start.AddSeconds(-1)));
        var occurrence = definition.NextRunAtUtc;
        CodexAutomationStore.CalculateNextOccurrence(definition, occurrence, occurrence);
        Require(definition.RecurrenceExhausted && !definition.Enabled && definition.NextRunAtUtc == default, "Exhausted RRULE remained enabled");
        return Task.CompletedTask;
    }

    private static async Task RpcAsync(string root, bool cancel, bool lifetime = false)
    {
        var marker = Path.Combine(root, "rpc-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXGUI_CHECK_RPC_MARKER", marker);
        using var cancellation = new CancellationTokenSource();
        var client = new CodexAppServerClient(Executable, lifetime ? cancellation.Token : default);
        try
        {
            if (!cancel)
            {
                var response = await client.ReadAccountAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Require(response.GetProperty("ok").GetBoolean(), "RPC result lost");
                return;
            }
            var request = client.ReadRateLimitsAsync(lifetime ? default : cancellation.Token);
            await WaitForFileAsync(marker);
            var pid = int.Parse(await File.ReadAllTextAsync(marker));
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(10)));
            try
            {
                using var process = Process.GetProcessById(pid);
                Require(process.HasExited, "Cancelled RPC child is still running");
            }
            catch (ArgumentException) { } // The OS has already removed the exited process.
        }
        finally { Environment.SetEnvironmentVariable("CODEXGUI_CHECK_RPC_MARKER", null); }
    }

    private static async Task AllRpcCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new CodexAppServerClient("this-executable-must-not-be-started.exe");
        var methods = typeof(CodexAppServerClient).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly);
        foreach (var method in methods)
        {
            Require(method.GetParameters().Any(parameter => parameter.ParameterType == typeof(CancellationToken)), method.Name + " has no cancellation token");
            var arguments = method.GetParameters().Select(parameter =>
                parameter.ParameterType == typeof(CancellationToken) ? (object)cancellation.Token :
                parameter.ParameterType == typeof(string) ? "test" :
                parameter.ParameterType == typeof(bool) ? true :
                parameter.ParameterType == typeof(IReadOnlyList<string>) ? Array.Empty<string>() :
                parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
            await ThrowsAsync<OperationCanceledException>(async () =>
            {
                await (Task)method.Invoke(client, arguments)!;
            });
        }
    }

    private static async Task RpcSessionPaginationAsync(string root)
    {
        var marker = Path.Combine(root, "rpc-pages-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXGUI_CHECK_RPC_PAGES_MARKER", marker);
        try
        {
            var client = new CodexAppServerClient(Executable);
            var statuses = await client.ListMcpServerStatusesAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(statuses.Count == 2 && statuses[0].GetProperty("id").GetString() == "server-1"
                && statuses[1].GetProperty("id").GetString() == "server-2",
                "Paged session did not return both response pages");
            var requests = File.ReadAllLines(marker);
            Require(requests.Length == 2, "Paged session did not send exactly two list requests");
            var first = requests[0].Split('|');
            var second = requests[1].Split('|');
            Require(first.Length == 3 && second.Length == 3 && first[0] == second[0]
                && first[1] == "2" && second[1] == "3" && first[2] == "1" && second[2] == "2",
                "Page requests did not reuse one process and increasing request ids");
        }
        finally { Environment.SetEnvironmentVariable("CODEXGUI_CHECK_RPC_PAGES_MARKER", null); }
    }

    private static async Task RpcMalformedResponseAsync(string root)
    {
        var marker = Path.Combine(root, "rpc-malformed-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXGUI_CHECK_RPC_MALFORMED_MARKER", marker);
        try
        {
            var client = new CodexAppServerClient(Executable);
            await ThrowsAsync<JsonException>(() => client.ReadAccountAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await WaitForFileAsync(marker);
            var processId = int.Parse(await File.ReadAllTextAsync(marker));
            try
            {
                using var process = Process.GetProcessById(processId);
                Require(process.HasExited, "Malformed RPC response left its child process running");
            }
            catch (ArgumentException) { }
        }
        finally { Environment.SetEnvironmentVariable("CODEXGUI_CHECK_RPC_MALFORMED_MARKER", null); }
    }

    private static async Task RpcInheritedStderrAsync(string root)
    {
        var marker = Path.Combine(root, "rpc-inherited-stderr-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXGUI_CHECK_RPC_INHERITED_STDERR_MARKER", marker);
        int? childProcessId = null;
        try
        {
            var response = await new CodexAppServerClient(Executable).ReadAccountAsync()
                .WaitAsync(TimeSpan.FromSeconds(3));
            Require(response.GetProperty("ok").GetBoolean(), "RPC response was lost while draining stderr");
            await WaitForFileAsync(marker);
            childProcessId = int.Parse(await File.ReadAllTextAsync(marker));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXGUI_CHECK_RPC_INHERITED_STDERR_MARKER", null);
            if (childProcessId is null && File.Exists(marker))
                childProcessId = int.Parse(await File.ReadAllTextAsync(marker));
            if (childProcessId is { } processId)
            {
                try
                {
                    using var child = Process.GetProcessById(processId);
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (ArgumentException) { }
            }
        }
    }

    private static async Task RpcUnexpectedResponseEofAsync(string root)
    {
        const string modeKey = "CODEXGUI_CHECK_RPC_UNEXPECTED_RESPONSE_MODE";
        const string markerKey = "CODEXGUI_CHECK_RPC_UNEXPECTED_RESPONSE_MARKER";
        var marker = Path.Combine(root, "rpc-unexpected-response-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(modeKey, "wrong-id");
        Environment.SetEnvironmentVariable(markerKey, marker);
        try
        {
            await ThrowsAsync<EndOfStreamException>(() => new CodexAppServerClient(Executable)
                .ReadAccountAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await WaitForFileAsync(marker);
            var processId = int.Parse(await File.ReadAllTextAsync(marker));
            try
            {
                using var process = Process.GetProcessById(processId);
                Require(process.HasExited, "An unrelated response id followed by EOF left its RPC process running");
            }
            catch (ArgumentException) { }
        }
        finally
        {
            Environment.SetEnvironmentVariable(modeKey, null);
            Environment.SetEnvironmentVariable(markerKey, null);
        }
    }

    private static async Task RpcOversizedResponseAsync(string root)
    {
        const string markerKey = "CODEXGUI_CHECK_RPC_OVERSIZED_MARKER";
        const int maximumResponseCharacters = 128;
        var marker = Path.Combine(root, "rpc-oversized-response-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(markerKey, marker);
        try
        {
            await ThrowsAsync<InvalidDataException>(() => new CodexAppServerClient(
                Executable, maximumResponseLineCharacters: maximumResponseCharacters)
                .ReadAccountAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await WaitForFileAsync(marker);
            var processId = int.Parse(await File.ReadAllTextAsync(marker));
            try
            {
                using var process = Process.GetProcessById(processId);
                Require(process.HasExited, "An oversized RPC response left its child process running");
            }
            catch (ArgumentException) { }
        }
        finally { Environment.SetEnvironmentVariable(markerKey, null); }
    }

    private static async Task ThreadHistoryPaginationAsync(string root)
    {
        var modeKey = "CODEXGUI_CHECK_RPC_THREAD_CURSOR_MODE";
        var markerKey = "CODEXGUI_CHECK_RPC_THREAD_MARKER";
        var marker = Path.Combine(root, "rpc-thread-pages-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(modeKey, "pages");
        Environment.SetEnvironmentVariable(markerKey, marker);
        try
        {
            var client = new CodexAppServerClient(Executable);
            var thread = await client.ReadThreadAsync("thread-id").WaitAsync(TimeSpan.FromSeconds(10));
            var turns = thread.GetProperty("thread").GetProperty("turns").EnumerateArray().ToArray();
            Require(turns.Length == 2 && turns[0].GetProperty("id").GetString() == "turn-2"
                && turns[1].GetProperty("id").GetString() == "turn-1",
                "Thread history did not merge and reverse two valid pages");
            var pageRequests = File.ReadAllLines(marker);
            Require(pageRequests.Length == 2 && pageRequests[0] == "none" && pageRequests[1] == "page-two",
                "Thread history did not request the second page with its cursor");

            File.Delete(marker);
            Environment.SetEnvironmentVariable(modeKey, "repeat");
            await ThrowsAsync<InvalidDataException>(() => client.ReadThreadAsync("thread-id"));
            var repeatedRequests = File.ReadAllLines(marker);
            Require(repeatedRequests.Length == 2 && repeatedRequests.All(line => line == "repeated-cursor"),
                "Repeated thread history cursor was not rejected immediately");
        }
        finally
        {
            Environment.SetEnvironmentVariable(modeKey, null);
            Environment.SetEnvironmentVariable(markerKey, null);
        }
    }

    private static Task ThreadHistoryBudgetAsync()
    {
        using var page = JsonDocument.Parse("[{\"text\":\"one\"},{\"text\":\"two\"}]");
        var pages = new List<JsonElement>();
        long bytes = 0;
        var pageBytes = Encoding.UTF8.GetByteCount("[{\"text\":\"one\"},{\"text\":\"two\"}]");
        CodexAppServerClient.AppendThreadHistoryPage(pages, page.RootElement, ref bytes, pageBytes);
        Require(pages.Count == 1 && pages[0].GetArrayLength() == 2 && bytes == pageBytes,
            "A page within the thread history budget was rejected");

        var limitedPages = new List<JsonElement>();
        long limitedBytes = 0;
        try
        {
            CodexAppServerClient.AppendThreadHistoryPage(limitedPages, page.RootElement, ref limitedBytes, pageBytes - 1);
            throw new InvalidOperationException("Thread history exceeded its byte budget without an error");
        }
        catch (InvalidDataException) { }
        Require(limitedPages.Count == 0 && limitedBytes == 0,
            "History budget accounting retained an oversized page");
        return Task.CompletedTask;
    }

    private static async Task ThreadHistoryCancellationAsync(string root)
    {
        const string modeKey = "CODEXGUI_CHECK_RPC_THREAD_CURSOR_MODE";
        const string markerKey = "CODEXGUI_CHECK_RPC_THREAD_BLOCK_MARKER";
        var marker = Path.Combine(root, "rpc-thread-cancel-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(modeKey, "block-second");
        Environment.SetEnvironmentVariable(markerKey, marker);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var client = new CodexAppServerClient(Executable);
            var read = client.ReadThreadAsync("thread-id", cancellation.Token);
            await WaitForFileAsync(marker);
            var processId = int.Parse(await File.ReadAllTextAsync(marker));
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
            try
            {
                using var process = Process.GetProcessById(processId);
                Require(process.HasExited, "Cancelling paged thread history left its RPC process running");
            }
            catch (ArgumentException) { }
        }
        finally
        {
            Environment.SetEnvironmentVariable(modeKey, null);
            Environment.SetEnvironmentVariable(markerKey, null);
        }
    }

    private static async Task PreCancelledAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new CodexAppServerClient("this-executable-must-not-be-started.exe");
        await ThrowsAsync<OperationCanceledException>(() => client.ReadAccountAsync(cancellation.Token));
        client = new CodexAppServerClient("this-executable-must-not-be-started.exe", cancellation.Token);
        await ThrowsAsync<OperationCanceledException>(() => client.ReadAccountAsync());
    }

    private static async Task<int> FakeServerAsync()
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id)) continue;
            var method = root.GetProperty("method").GetString();
            if (method == "account/rateLimits/read")
            {
                var marker = Environment.GetEnvironmentVariable("CODEXGUI_CHECK_RPC_MARKER")!;
                File.WriteAllText(marker + ".tmp", Environment.ProcessId.ToString());
                File.Move(marker + ".tmp", marker);
                continue; // Remain blocked reading stdin until the cancelled client closes it.
            }
            if (method == "account/read" && Environment.GetEnvironmentVariable("CODEXGUI_CHECK_RPC_MALFORMED_MARKER") is { Length: > 0 } malformedMarker)
            {
                File.WriteAllText(malformedMarker, Environment.ProcessId.ToString());
                await Console.Out.WriteLineAsync("{invalid json");
                await Console.Out.FlushAsync();
                return 0;
            }
            if (method == "account/read" && Environment.GetEnvironmentVariable("CODEXGUI_CHECK_RPC_INHERITED_STDERR_MARKER") is { Length: > 0 } inheritedStderrMarker)
            {
                var childStart = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true };
                childStart.ArgumentList.Add("--hold-inherited-stderr");
                childStart.ArgumentList.Add(inheritedStderrMarker);
                using var child = Process.Start(childStart)
                    ?? throw new InvalidOperationException("Could not start inherited-stderr fake child");
                await File.WriteAllTextAsync(inheritedStderrMarker, child.Id.ToString());
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { ok = true } }));
                await Console.Out.FlushAsync();
                return 0;
            }
            if (method == "account/read" && Environment.GetEnvironmentVariable("CODEXGUI_CHECK_RPC_UNEXPECTED_RESPONSE_MODE") == "wrong-id")
            {
                var marker = Environment.GetEnvironmentVariable("CODEXGUI_CHECK_RPC_UNEXPECTED_RESPONSE_MARKER")!;
                await File.WriteAllTextAsync(marker, Environment.ProcessId.ToString());
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.GetInt32() + 100, result = new { ok = true } }));
                await Console.Out.FlushAsync();
                return 0;
            }
            if (method == "account/read" && Environment.GetEnvironmentVariable("CODEXGUI_CHECK_RPC_OVERSIZED_MARKER") is { Length: > 0 } oversizedMarker)
            {
                await File.WriteAllTextAsync(oversizedMarker, Environment.ProcessId.ToString());
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { text = new string('x', 512) } }));
                await Console.Out.FlushAsync();
                return 0;
            }
            if (method == "mcpServerStatus/list")
            {
                var parameters = root.GetProperty("params");
                var cursor = parameters.TryGetProperty("cursor", out var cursorValue)
                    && cursorValue.ValueKind == JsonValueKind.String ? cursorValue.GetString() : null;
                var page = cursor is null ? 1 : 2;
                if (Environment.GetEnvironmentVariable("CODEXGUI_CHECK_RPC_PAGES_MARKER") is { Length: > 0 } pagesMarker)
                    await File.AppendAllTextAsync(pagesMarker, $"{Environment.ProcessId}|{id.GetInt32()}|{page}{Environment.NewLine}");
                var result = new
                {
                    data = new[] { new { id = $"server-{page}" } },
                    nextCursor = page == 1 ? "page-two" : null
                };
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.GetInt32(), result }));
                await Console.Out.FlushAsync();
                continue;
            }
            if (method == "thread/read")
            {
                var result = new { thread = new { id = "thread-id", title = "Fake thread" } };
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.GetInt32(), result }));
                await Console.Out.FlushAsync();
                continue;
            }
            if (method == "thread/turns/list")
            {
                var parameters = root.GetProperty("params");
                var cursor = parameters.TryGetProperty("cursor", out var cursorValue)
                    && cursorValue.ValueKind == JsonValueKind.String ? cursorValue.GetString() : null;
                var repeated = Environment.GetEnvironmentVariable("CODEXGUI_CHECK_RPC_THREAD_CURSOR_MODE") == "repeat";
                var marker = Environment.GetEnvironmentVariable("CODEXGUI_CHECK_RPC_THREAD_MARKER");
                var page = cursor is null ? 1 : 2;
                if (page == 2 && Environment.GetEnvironmentVariable("CODEXGUI_CHECK_RPC_THREAD_CURSOR_MODE") == "block-second")
                {
                    var blockMarker = Environment.GetEnvironmentVariable("CODEXGUI_CHECK_RPC_THREAD_BLOCK_MARKER")!;
                    File.WriteAllText(blockMarker, Environment.ProcessId.ToString());
                    continue;
                }
                if (marker is { Length: > 0 })
                    await File.AppendAllTextAsync(marker, repeated ? $"repeated-cursor{Environment.NewLine}" : $"{cursor ?? "none"}{Environment.NewLine}");
                var result = new
                {
                    data = new[] { new { id = $"turn-{page}", items = Array.Empty<object>() } },
                    nextCursor = repeated ? "repeated-cursor" : page == 1 ? "page-two" : null
                };
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.GetInt32(), result }));
                await Console.Out.FlushAsync();
                continue;
            }
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { ok = true } }));
            await Console.Out.FlushAsync();
        }
        return 0;
    }

    private static async Task<int> FakeAppServerBlockInitializeAsync(string[] args)
    {
        var marker = args.ElementAtOrDefault(1)
            ?? throw new InvalidOperationException("The initialization marker path is required");
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("method", out var method) && method.GetString() == "initialize")
            {
                await File.WriteAllTextAsync(marker, Environment.ProcessId.ToString());
                await Console.In.ReadToEndAsync();
                return 0;
            }
        }
        return 0;
    }

    private static async Task<int> FakeAppServerDropAfterInitializeAsync(string[] args)
    {
        var marker = args.ElementAtOrDefault(1)
            ?? throw new InvalidOperationException("The disconnect marker path is required");
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var method = root.TryGetProperty("method", out var methodValue) ? methodValue.GetString() : null;
            if (method == "initialized")
            {
                await File.WriteAllTextAsync(marker + ".tmp", Environment.ProcessId.ToString());
                File.Move(marker + ".tmp", marker, overwrite: true);
                return 0;
            }
            if (!root.TryGetProperty("id", out var id)) continue;
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { ok = true } }));
            await Console.Out.FlushAsync();
        }
        return 0;
    }

    private static async Task<int> FakeAppServerTerminalControlAsync(string[] args)
    {
        var startedMarker = args.ElementAtOrDefault(1)
            ?? throw new InvalidOperationException("The terminal start marker path is required");
        var terminateMarker = args.ElementAtOrDefault(2)
            ?? throw new InvalidOperationException("The terminal terminate marker path is required");
        var terminateRequests = 0;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id)) continue;
            var method = root.TryGetProperty("method", out var methodValue) ? methodValue.GetString() : null;
            if (method == "command/exec")
            {
                await File.WriteAllTextAsync(startedMarker, Environment.ProcessId.ToString());
                continue;
            }
            if (method == "command/exec/terminate" && ++terminateRequests == 1)
            {
                await File.WriteAllTextAsync(terminateMarker, Environment.ProcessId.ToString());
                continue;
            }
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { ok = true } }));
            await Console.Out.FlushAsync();
        }
        return 0;
    }

    private static Process Start(params string[] args)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start check process");
    }

    private sealed class ChunkedTextReader(string text, int maximumChunkSize) : TextReader
    {
        private int _position;

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(Math.Min(buffer.Length, maximumChunkSize), text.Length - _position);
            text.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class RepeatingTextReader(int totalCharacters, int maximumChunkSize) : TextReader
    {
        public int CharactersRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(Math.Min(buffer.Length, maximumChunkSize), totalCharacters - CharactersRead);
            buffer.Span[..count].Fill('x');
            CharactersRead += count;
            return ValueTask.FromResult(count);
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(path)) await Task.Delay(20, deadline.Token);
    }
}
