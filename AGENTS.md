# CodexGUI project instructions

## Project facts

- CodexGUI is a Windows WPF desktop client targeting `net10.0-windows`. It downloads a pinned official Codex App Server package into `%LOCALAPPDATA%\CodexGUI\app-server-runtimes` on first launch; normal chats do not require an installed Codex CLI.
- The main window and event orchestration live in `MainWindow.xaml.cs`; the hot-reloaded workspace layout is `Views/WorkspaceView.xaml`.
- CLI-backed plugin management remains an optional integration; chat queues, worktree paths, and thread metadata are persisted under `%LOCALAPPDATA%\CodexGUI`.
- The release workflow in `.github/workflows/release.yml` runs on pushes to `main`, publishes a GitHub release, and deletes older versioned releases and tags.

## Engineering rules

- Preserve the shared Codex configuration and history, Git worktree paths, queued prompts, and active-run recovery when changing the execution pipeline.
- Confirm app-server methods and payloads from the pinned package schema or official OpenAI sources; do not invent RPC methods.
- Use the pinned official App Server package for normal turns. Keep CLI-only operations optional; do not add unrelated production dependencies without the user's explicit approval.
- Use a `codex/*` branch while implementing and reviewing a change. The active Codex feature-parity goal authorizes release publication and production actions; push or merge to `main` after the version, staged scope, and release workflow effects have been verified.
- Before committing, inspect the staged diff and run `git diff --cached --check`. Keep commits limited to this task.
- Build with `dotnet build --no-restore`; run focused verification appropriate to the behavior changed and report any unverified runtime paths.
- Store durable agent-specific project facts in `.codex/`; keep unfinished goal state in `.codex/tasks/` and remove it when the goal is complete.
