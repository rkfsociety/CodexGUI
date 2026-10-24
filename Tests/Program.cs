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
        if (args.FirstOrDefault() == "write-actions") return WriteActions(args);

        var root = Path.Combine(Path.GetTempPath(), "CodexGUI-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Check("Workspace search finds names and text with correct line numbers", () => WorkspaceSearchAsync(root));
            await Check("Workspace search is bounded and honors cancellation", () => WorkspaceSearchLimitsAsync(root));
            await Check("Workspace map reads real folders, frameworks and package dependencies", () => WorkspaceMapAsync(root));
            await Check("Workspace map buttons select their actual paths and search UI returns results", () => WorkspaceControlsAsync(root));
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
            await Check("Every public RPC forwards operation cancellation", AllRpcCancellationAsync);
            await Check("Pre-cancelled RPC never starts a process", PreCancelledAsync);
            await Check("Worker recovery rejects invalid or reused process identities", () => WorkerIdentityRecoveryAsync(root));
            await Check("Worker cleanup cannot delete outside its run directory", () => WorkerCleanupConfinementAsync(root));
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
            if (root.GetProperty("method").GetString() == "account/rateLimits/read")
            {
                var marker = Environment.GetEnvironmentVariable("CODEXGUI_CHECK_RPC_MARKER")!;
                File.WriteAllText(marker + ".tmp", Environment.ProcessId.ToString());
                File.Move(marker + ".tmp", marker);
                continue; // Remain blocked reading stdin until the cancelled client closes it.
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

    private static async Task WaitForFileAsync(string path)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(path)) await Task.Delay(20, deadline.Token);
    }
}
