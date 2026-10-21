# CodexGUI working memory

- `App.xaml.cs` handles startup, user installation, single-instance activation, and the pre-window update check. `AppUpdater.cs` verifies downloaded release archives and signatures.
- `MainWindow.xaml.cs` owns chat/project state, request queues, repository context, and the UI event loop. `Views/WorkspaceView.xaml` is the hot-reloaded layout.
- Normal chat turns run through `CodexAppServerTurnWorker` in a detached CodexGUI child process; file-backed JSONL output and control files under `%LOCALAPPDATA%\CodexGUI\workers\<runId>` preserve reattach, approvals, stop, and steer. Published workers run from a versioned copy under `%LOCALAPPDATA%\CodexGUI\worker-runtimes` so app updates can replace the install directory. `CodexCliWorker.ps1` remains for managed worktree starts/forks.
- `CodexAppServerClient.cs` starts a short-lived `codex app-server --stdio` process for request/response calls; `CodexAppServerTurnWorker.cs` handles bidirectional streamed turns and server-initiated requests.
- Project and chat state is serialized to `%LOCALAPPDATA%\CodexGUI\state.json`; Codex CLI remains the source of saved thread history.
- `.github/workflows/release.yml` publishes and prunes releases when `main` receives a push. Feature work belongs on a separate branch.
