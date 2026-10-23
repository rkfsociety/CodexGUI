using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexGUI;

public partial class MainWindow
{
    private static void ValidateStoredState(StoredState state)
    {
        if (state.Projects is null || state.RemovedProjects is null || state.Threads is null || state.Actions is null
            || state.QueuedPrompts is null || state.Projects.Any(project => project is null)
            || state.RemovedProjects.Any(project => project is null)
            || state.Threads.Any(thread => thread is null)
            || state.Actions.Any(action => action is null)
            || state.QueuedPrompts.Any(prompt => prompt is null))
            throw new InvalidDataException("Файл состояния содержит повреждённые коллекции.");
    }

    private void LoadState()
    {
        var statePath = GetStatePath();
        if (File.Exists(statePath) || File.Exists(statePath + ".bak"))
        {
            try
            {
                var state = AtomicJsonFile.ReadWithBackup<StoredState>(statePath, ValidateStoredState);
                if (state is not null)
                {
                    _projects.AddRange(state.Projects.Where(project => !string.IsNullOrWhiteSpace(project.Id) && !string.IsNullOrWhiteSpace(project.Path)));
                    _removedProjects.AddRange(state.RemovedProjects.Where(project =>
                        !string.IsNullOrWhiteSpace(project.Id) &&
                        !string.IsNullOrWhiteSpace(project.Path) &&
                        !_projects.Any(active => active.Id == project.Id)));
                    _actions.AddRange(state.Actions
                        .Where(action => !string.IsNullOrWhiteSpace(action.Id)
                            && !string.IsNullOrWhiteSpace(action.Name)
                            && action.HasValidPayload)
                        .DistinctBy(action => action.Id, StringComparer.Ordinal));
                    foreach (var stored in state.Threads)
                    {
                        if (string.IsNullOrWhiteSpace(stored.Id) || string.IsNullOrWhiteSpace(stored.ProjectPath)) continue;
                        _threads.Add(new ChatThread
                        {
                            Id = stored.Id,
                            Title = string.IsNullOrWhiteSpace(stored.Title) ? "Новый чат" : stored.Title,
                            ProjectId = stored.ProjectId,
                            ProjectPath = stored.ProjectPath,
                            WorktreePath = stored.WorktreePath,
                            CodexSessionId = stored.CodexSessionId,
                            ForkFromSessionId = stored.ForkFromSessionId,
                            IsArchived = stored.IsArchived,
                            IsPinned = stored.IsPinned,
                            HasUnreadResponse = stored.HasUnreadResponse,
                            UseWorktree = stored.UseWorktree,
                            WorktreeSessionStarted = stored.WorktreeSessionStarted,
                            WorktreeSetupComplete = stored.WorktreeSetupComplete,
                            ContextTokens = stored.ContextTokens,
                            ContextWindowTokens = stored.ContextWindowTokens,
                            PendingGoal = stored.PendingGoal,
                            ActiveRun = stored.ActiveRun
                        });
                    }
                    _promptQueue.AddRange(state.QueuedPrompts.Where(prompt =>
                        !string.IsNullOrWhiteSpace(prompt.Id) &&
                        !string.IsNullOrWhiteSpace(prompt.ThreadId) &&
                        !string.IsNullOrWhiteSpace(prompt.Text) &&
                        _threads.Any(thread => thread.Id == prompt.ThreadId)));
                    _selectedProject = _projects.FirstOrDefault(project => project.Id == state.SelectedProjectId);
                    _selectedModel = string.IsNullOrWhiteSpace(state.SelectedModel) ? DefaultModel : state.SelectedModel;
                    _selectedEffort = string.IsNullOrWhiteSpace(state.SelectedEffort) ? DefaultEffort : state.SelectedEffort;
                }
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
            {
                try
                {
                    _stateSaveBlocked = true;
                    if (File.Exists(statePath))
                    {
                        var backup = AtomicJsonFile.BackupUnreadable(statePath);
                        _startupIssue = $"Не удалось прочитать список проектов: {error.Message} Резервная копия: {backup}. Запись state.json заблокирована.";
                    }
                    else
                    {
                        _startupIssue = $"Не удалось восстановить состояние из резервной копии: {error.Message} Файл state.json отсутствует. Запись заблокирована.";
                    }
                }
                catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
                {
                    _stateSaveBlocked = true;
                    _startupIssue = $"Не удалось прочитать состояние ({error.Message}) и сохранить его копию ({backupError.Message}). Запись state.json заблокирована.";
                }
            }
        }

        if (_projects.Count == 0)
        {
            var path = FindProjectRoot();
            _projects.Add(new ProjectEntry { Id = Guid.NewGuid().ToString("N"), Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), Path = path });
        }
        _selectedProject ??= _projects[0];
        _projectPath = _selectedProject.Path;
        _currentThread = _threads.LastOrDefault(thread => thread.ProjectId == _selectedProject.Id && !thread.IsArchived);
        if (_currentThread is { } currentThread) currentThread.HasUnreadResponse = false;
        RequestStateSave();
    }

    private readonly SerializedJsonWriter _stateWriter = new();

    private bool SaveState() => SaveStateAsync().GetAwaiter().GetResult();

    private void RequestStateSave() => _ = SaveStateAsync();

    private async Task<bool> SaveStateAsync()
    {
        if (_stateSaveBlocked) return false;
        try
        {
            var statePath = GetStatePath();
            var state = new StoredState
            {
                SelectedProjectId = _selectedProject?.Id,
                SelectedModel = _selectedModel,
                SelectedEffort = _selectedEffort,
                Projects = _projects,
                RemovedProjects = _removedProjects,
                QueuedPrompts = _promptQueue,
                Actions = _actions,
                Threads = _threads.Select(thread => new StoredThread
                {
                    Id = thread.Id,
                    Title = thread.Title,
                    ProjectId = thread.ProjectId,
                    ProjectPath = thread.ProjectPath,
                    WorktreePath = thread.WorktreePath,
                    CodexSessionId = thread.CodexSessionId,
                    ForkFromSessionId = thread.ForkFromSessionId,
                    PendingGoal = thread.PendingGoal,
                    IsArchived = thread.IsArchived,
                    IsPinned = thread.IsPinned,
                    HasUnreadResponse = thread.HasUnreadResponse,
                    UseWorktree = thread.UseWorktree,
                    WorktreeSessionStarted = thread.WorktreeSessionStarted,
                    WorktreeSetupComplete = thread.WorktreeSetupComplete,
                    ContextTokens = thread.ContextTokens,
                    ContextWindowTokens = thread.ContextWindowTokens,
                    ActiveRun = thread.ActiveRun
                }).ToList()
            };
            // Serialize a detached snapshot on the UI thread; no live collections cross the boundary.
            var snapshot = JsonSerializer.SerializeToElement(state, StateSerializerOptions);
            await _stateWriter.Enqueue(statePath, snapshot, StateSerializerOptions, statePath + ".bak")
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _startupIssue = "Не удалось сохранить состояние: " + error.Message;
            if (!_isClosing && !Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_isClosing) SetStatus("Состояние не сохранено", error.Message, isError: true);
                }));
            return false;
        }
    }

    private static string GetStatePath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexGUI", "state.json");

}
