using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Controls;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace CodexGUI;

public partial class MainWindow
{
    private void PersistAutomationDefinitions()
    {
        CodexAutomationStore.SaveDefinitions(_automationDefinitions);
        UpdateAutomationTimerInterval();
        _automationWindow?.UpdateLists();
    }

    private void UpdateAutomationTimerInterval()
    {
        var now = DateTimeOffset.UtcNow;
        var nextRuleRun = _automationDefinitions
            .Where(definition => definition.Enabled && !definition.RecurrenceExhausted
                && definition.Cadence == "rrule" && !IsAutomationBusy(definition)
                && definition.NextRunAtUtc > now)
            .Select(definition => definition.NextRunAtUtc)
            .OrderBy(nextRun => nextRun)
            .FirstOrDefault();
        var interval = TimeSpan.FromSeconds(30);
        if (nextRuleRun != default)
        {
            var untilRule = nextRuleRun - now;
            if (untilRule < TimeSpan.FromMilliseconds(100)) untilRule = TimeSpan.FromMilliseconds(100);
            if (untilRule < interval) interval = untilRule;
        }
        _automationTimer.Interval = interval;
    }

    private void MarkAutomationRunReviewed(CodexAutomationRun run)
    {
        try { CodexAutomationStore.MarkRunReviewed(run); }
        catch (Exception error)
        {
            run.Reviewed = false;
            CodexMessageDialog.Show(this, error.Message, "Не удалось сохранить отметку проверки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        _automationWindow?.UpdateLists();
    }

    private void OpenAutomationRun(CodexAutomationRun run)
    {
        if (string.IsNullOrWhiteSpace(run.ThreadId)) return;
        var existing = _threads.FirstOrDefault(thread => thread.CodexSessionId == run.ThreadId);
        if (existing is not null)
        {
            SelectThread(existing);
            if (run.Status is not ("running" or "preparing"))
                _ = RestoreThreadHistoryAsync(existing, force: true);
            return;
        }

        var thread = new ChatThread
        {
            Title = $"{run.AutomationName} · {run.StartedAtUtc.ToLocalTime():dd.MM HH:mm}",
            ProjectId = run.ProjectId,
            ProjectPath = run.ProjectPath,
            WorktreePath = run.WorktreePath,
            CodexSessionId = run.ThreadId
        };
        _threads.Add(thread);
        _showArchivedChats = false;
        SelectThread(thread);
        RequestStateSave();
    }

    private async Task ProcessAutomationsAsync()
    {
        if (_isProcessingAutomationTick || _isClosing) return;
        _isProcessingAutomationTick = true;
        var changed = false;
        try
        {
            foreach (var run in _automationRuns.Where(item => item.Status == "preparing").ToArray())
            {
                if (IsProcessIdentityRunning(run.CoordinatorProcessId, run.CoordinatorStartTimeUtcTicks)) continue;
                var runDirectory = run.WorkerRunId is { Length: > 0 } workerRunId && Guid.TryParseExact(workerRunId, "N", out _)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexGUI", "workers", workerRunId)
                    : "";
                if (runDirectory.Length > 0 && (TryRestoreAutomationWorkerIdentity(run, runDirectory)
                    || File.Exists(Path.Combine(runDirectory, "completed.json"))))
                {
                    run.Status = "running";
                    CodexAutomationStore.SaveRun(run);
                    changed = true;
                    continue;
                }
                run.Status = "failed";
                run.Error = "CodexGUI завершился до запуска фоновой задачи.";
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
                CodexAutomationStore.SaveRun(run);
                changed = true;
            }

            foreach (var run in _automationRuns.Where(item => item.Status == "running").ToArray())
                changed |= await RefreshAutomationRunAsync(run);

            var now = DateTimeOffset.UtcNow;
            foreach (var definition in _automationDefinitions.Where(item => item.Enabled && item.NextRunAtUtc <= now).ToArray())
            {
                if (IsAutomationBusy(definition)) continue;
                CodexAutomationStore.CalculateNextOccurrence(definition, now, definition.NextRunAtUtc);
                PersistAutomationDefinitions();
                await StartAutomationAsync(definition, scheduledOccurrence: true);
                changed = true;
            }

            UpdateAutomationTimerInterval();
            if (changed) _automationWindow?.UpdateLists();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            _startupIssue = "Ошибка планировщика Codex: " + error.Message;
            if (Control<TextBlock>("ConnectionDetail") is { } detail) detail.Text = _startupIssue;
        }
        finally
        {
            _isProcessingAutomationTick = false;
        }
    }

    private bool IsAutomationBusy(CodexAutomationDefinition definition)
    {
        var existingThreadId = definition.ExistingThreadId;
        return _automationRuns.Any(run => (run.Status is "running" or "preparing")
            && (run.AutomationId == definition.Id
                || existingThreadId is { Length: > 0 } && run.ThreadId == existingThreadId))
            || (existingThreadId is { Length: > 0 }
                && _threads.Any(thread => thread.CodexSessionId == existingThreadId && thread.ActiveRun is not null));
    }

    private async Task StartAutomationAsync(CodexAutomationDefinition definition, bool manualRun = false,
        bool scheduledOccurrence = false)
    {
        if (!definition.Enabled && !manualRun && !scheduledOccurrence)
            return;
        if (IsAutomationBusy(definition))
        {
            if (manualRun) throw new InvalidOperationException("Эта автоматизация или выбранный чат уже выполняется.");
            return;
        }

        var projects = ResolveAutomationProjects(definition);
        if (projects.Count == 0)
            throw new InvalidDataException("В расписании не выбран ни один проект.");
        if (definition.ExistingThreadId is { Length: > 0 } && projects.Count != 1)
            throw new InvalidDataException("Продолжение существующего чата поддерживается для одного проекта.");

        foreach (var project in projects)
            await StartAutomationForProjectAsync(definition, project);
    }

    private IReadOnlyList<CodexAutomationProject> ResolveAutomationProjects(CodexAutomationDefinition definition)
        => CodexAutomationStore.ResolveAutomationProjects(definition,
            _projects.Select(project => new CodexAutomationProject(project.Id, project.Name, project.Path)).ToArray());

    private async Task StartAutomationForProjectAsync(CodexAutomationDefinition definition, CodexAutomationProject project)
    {
        var workerRunId = Guid.NewGuid().ToString("N");
        using var coordinatorProcess = Process.GetCurrentProcess();
        var run = new CodexAutomationRun
        {
            AutomationId = definition.Id,
            AutomationName = definition.Name,
            ProjectId = project.Id,
            ProjectPath = project.Path,
            ThreadId = definition.ExistingThreadId,
            WorkerRunId = workerRunId,
            CoordinatorProcessId = coordinatorProcess.Id,
            CoordinatorStartTimeUtcTicks = coordinatorProcess.StartTime.ToUniversalTime().Ticks,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = "preparing"
        };
        _automationRuns.Add(run);
        CodexAutomationStore.SaveRun(run);
        _automationWindow?.UpdateLists();

        try
        {
            if (!Directory.Exists(project.Path))
                throw new DirectoryNotFoundException($"Папка проекта больше не существует: {project.Path}");

            var workingDirectory = Path.GetFullPath(project.Path);
            if (definition.ExistingThreadId is { Length: > 0 })
            {
                var existingThreadDirectory = Path.GetFullPath(
                    string.IsNullOrWhiteSpace(definition.ExistingThreadWorkingDirectory)
                        ? project.Path
                        : definition.ExistingThreadWorkingDirectory);
                if (definition.ExistingThreadWorktreePath is { Length: > 0 } storedWorktree)
                {
                    var fullWorktree = Path.GetFullPath(storedWorktree);
                    if (!string.Equals(existingThreadDirectory, fullWorktree, StringComparison.OrdinalIgnoreCase)
                        || !Directory.Exists(fullWorktree)
                        || !await GitRepositoryInspector.IsRegisteredWorktreeAsync(project.Path, fullWorktree, CancellationToken.None))
                        throw new InvalidDataException("Выбранный чат ссылается на недоступный Git worktree.");
                    run.WorktreePath = fullWorktree;
                }
                else if (!string.Equals(existingThreadDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Папка сохранённого чата изменилась; выберите чат заново в настройках расписания.");
                workingDirectory = existingThreadDirectory;
            }
            else if (definition.UseWorktree)
            {
                if (!Guid.TryParseExact(project.Id, "N", out var projectId))
                    throw new InvalidDataException("Не удалось проверить идентификатор проекта для изолированной копии.");
                var worktreePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CodexGUI", "worktrees", projectId.ToString("N"), "automations", run.Id);
                var repository = await GitRepositoryInspector.ReadAsync(workingDirectory, CancellationToken.None);
                if (!repository.IsRepository)
                    throw new InvalidOperationException(repository.Error ?? "Изоляция расписания требует Git-репозиторий; снимите флажок worktree, чтобы работать прямо в папке проекта.");
                run.WorktreePath = await GitRepositoryInspector.CreateDetachedWorktreeAsync(repository.Root, worktreePath, CancellationToken.None);
                workingDirectory = run.WorktreePath;
            }

            var runDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexGUI", "workers", workerRunId);
            Directory.CreateDirectory(runDirectory);
            var activeRun = new ActiveCliRun
            {
                RunId = workerRunId,
                Prompt = definition.Prompt,
                Model = definition.Model,
                Effort = definition.Effort,
                WorkingDirectory = workingDirectory,
                RequestPath = Path.Combine(runDirectory, "request.json"),
                ScriptPath = Path.Combine(runDirectory, "CodexCliWorker.ps1"),
                StartedPath = Path.Combine(runDirectory, "started.json"),
                OutputPath = Path.Combine(runDirectory, "stdout.jsonl"),
                ErrorPath = Path.Combine(runDirectory, "stderr.txt"),
                CompletionPath = Path.Combine(runDirectory, "completed.json"),
                AppServerMode = true
            };
            var request = new CodexAppServerWorkerRequest
            {
                RunId = workerRunId,
                ExecutablePath = CodexAppServerRuntime.ResolveExecutable(),
                WorkingDirectory = workingDirectory,
                Prompt = definition.Prompt,
                Model = definition.Model,
                Effort = definition.Effort,
                Unattended = true,
                ThreadId = definition.ExistingThreadId,
                StartedPath = activeRun.StartedPath,
                OutputPath = activeRun.OutputPath,
                ErrorPath = activeRun.ErrorPath,
                CompletionPath = activeRun.CompletionPath
            };
            var startInfo = CreateAppServerWorkerStartInfo(activeRun);
            run.WorkerRuntimeDirectory = activeRun.WorkerRuntimeDirectory;
            run.AppServerRuntimeDirectory = CodexAppServerRuntime.IsInstalled
                ? CodexAppServerRuntime.RuntimeDirectory
                : null;
            CodexAutomationStore.SaveRun(run);
            await File.WriteAllTextAsync(activeRun.RequestPath, JsonSerializer.Serialize(request));
            run.Status = "running";
            CodexAutomationStore.SaveRun(run);
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Не удалось запустить фоновую автоматизацию Codex.");
            run.WorkerProcessId = process.Id;
            run.WorkerStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            process.Dispose();
            CodexAutomationStore.SaveRun(run);
        }
        catch (Exception error)
        {
            run.Status = "failed";
            run.Error = error.Message;
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            CodexAutomationStore.SaveRun(run);
            _automationWindow?.UpdateLists();
        }
    }

    private async Task<bool> RefreshAutomationRunAsync(CodexAutomationRun run)
    {
        if (run.WorkerRunId is not { Length: > 0 } workerRunId || !Guid.TryParseExact(workerRunId, "N", out _))
        {
            run.Status = "failed";
            run.Error = "Не удалось восстановить идентификатор фонового процесса.";
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            CodexAutomationStore.SaveRun(run);
            return true;
        }

        var runDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexGUI", "workers", workerRunId);
        if (run.WorkerProcessId <= 0 && TryRestoreAutomationWorkerIdentity(run, runDirectory))
            CodexAutomationStore.SaveRun(run);
        var completionPath = Path.Combine(runDirectory, "completed.json");
        if (!File.Exists(completionPath))
        {
            if (run.StartedAtUtc < DateTimeOffset.UtcNow.AddMinutes(-2) && !IsAutomationWorkerRunning(run))
            {
                run.Status = "failed";
                run.Error = "Фоновый процесс завершился без результата.";
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
                CodexAutomationStore.SaveRun(run);
                return true;
            }
            return false;
        }

        CodexAppServerWorkerCompletion completion;
        try
        {
            completion = JsonSerializer.Deserialize<CodexAppServerWorkerCompletion>(await File.ReadAllTextAsync(completionPath))
                ?? throw new InvalidDataException("Пустой результат фонового процесса.");
            ValidateAutomationCompletionRunId(completion, workerRunId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            System.Diagnostics.Trace.TraceWarning("Не удалось прочитать завершение автоматизации {0}: {1}", run.Id, error.Message);
            if (run.StartedAtUtc < DateTimeOffset.UtcNow.AddMinutes(-2) && !IsAutomationWorkerRunning(run))
            {
                run.Status = "failed";
                run.Error = "Фоновый процесс завершился, но результат оказался повреждён.";
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
                CodexAutomationStore.SaveRun(run);
                return true;
            }
            return false;
        }

        run.ThreadId = completion.SessionId ?? ReadAutomationThreadId(Path.Combine(runDirectory, "stdout.jsonl"));
        run.Status = completion.ExitCode == 0 ? "completed" : "failed";
        run.Error = completion.Error;
        if (run.Status == "failed" && string.IsNullOrWhiteSpace(run.Error))
        {
            var errorPath = Path.Combine(runDirectory, "stderr.txt");
            if (File.Exists(errorPath)) run.Error = await File.ReadAllTextAsync(errorPath);
        }
        run.Summary = ReadAutomationSummary(Path.Combine(runDirectory, "stdout.jsonl"));
        run.CompletedAtUtc = DateTimeOffset.UtcNow;
        CodexAutomationStore.SaveRun(run);
        return true;
    }

    private static bool IsAutomationWorkerRunning(CodexAutomationRun run)
        => IsProcessIdentityRunning(run.WorkerProcessId, run.WorkerStartTimeUtcTicks);

    private static void ValidateAutomationCompletionRunId(CodexAppServerWorkerCompletion completion, string expectedRunId)
    {
        if (!string.IsNullOrWhiteSpace(completion.RunId)
            && !string.Equals(completion.RunId, expectedRunId, StringComparison.Ordinal))
            throw new InvalidDataException("Файл завершения принадлежит другой фоновой задаче.");
    }

    private static bool TryRestoreAutomationWorkerIdentity(CodexAutomationRun run, string runDirectory)
    {
        var startedPath = Path.Combine(runDirectory, "started.json");
        if (!File.Exists(startedPath)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(startedPath));
            var started = document.RootElement;
            if (GetString(started, "RunId") != run.WorkerRunId
                || !started.TryGetProperty("ProcessId", out var processId) || !processId.TryGetInt32(out var id) || id <= 0
                || !started.TryGetProperty("StartTimeUtcTicks", out var startTime)
                || !startTime.TryGetInt64(out var ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
                return false;
            run.WorkerProcessId = id;
            run.WorkerStartTimeUtcTicks = ticks;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.TraceWarning("Не удалось восстановить фоновый процесс автоматизации {0}: {1}", run.Id, error.Message);
            return false;
        }
    }

    private static bool IsProcessIdentityRunning(int processId, long startTimeUtcTicks)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return startTimeUtcTicks == 0
                || process.StartTime.ToUniversalTime().Ticks == startTimeUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static string? ReadAutomationThreadId(string outputPath)
    {
        if (!File.Exists(outputPath)) return null;
        try
        {
            foreach (var line in File.ReadLines(outputPath))
            {
                using var document = JsonDocument.Parse(line);
                if (GetString(document.RootElement, "type") == "thread.started"
                    && GetString(document.RootElement, "thread_id") is { Length: > 0 } threadId)
                    return threadId;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.TraceWarning("Не удалось прочитать id чата автоматизации: {0}", error.Message);
        }
        return null;
    }

    private static string ReadAutomationSummary(string outputPath)
    {
        if (!File.Exists(outputPath)) return "";
        var deltas = new StringBuilder();
        var finalMessage = new StringBuilder();
        try
        {
            foreach (var line in File.ReadLines(outputPath))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var eventType = GetString(root, "type");
                if (eventType == "response.output_text.delta" && GetString(root, "delta") is { } delta)
                    deltas.Append(delta);
                else if (eventType == "item.completed" && root.TryGetProperty("item", out var item)
                    && (GetString(item, "type") is "agent_message" or "agentMessage")
                    && GetString(item, "text") is { } text)
                {
                    finalMessage.Clear();
                    finalMessage.Append(text);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.TraceWarning("Не удалось прочитать сводку автоматизации: {0}", error.Message);
        }
        var summary = finalMessage.Length > 0 ? finalMessage.ToString() : deltas.ToString();
        summary = summary.Trim();
        return summary.Length <= 1600 ? summary : summary[..1600] + "…";
    }

}
