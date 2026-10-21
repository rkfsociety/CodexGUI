# CodexGUI project instructions

## Project facts

- CodexGUI is a Windows WPF desktop client targeting `net10.0-windows`. Normal streamed chat uses Codex app-server; the current distribution still resolves its runtime from an installed `codex.exe`.
- The main window and event orchestration live in `MainWindow.xaml.cs`; the hot-reloaded workspace layout is `Views/WorkspaceView.xaml`.
- CLI-backed operations such as managed worktree creation and review, plus chat queues and thread metadata, are persisted under `%LOCALAPPDATA%\CodexGUI`.
- The release workflow in `.github/workflows/release.yml` runs on pushes to `main`, publishes a GitHub release, and deletes older versioned releases and tags.

## Engineering rules

- Preserve the installed CLI configuration, session history, worktree paths, queued prompts, and active-run recovery when changing the execution pipeline.
- Confirm app-server methods and payloads from the installed CLI protocol schema or official OpenAI sources; do not invent RPC methods.
- Reuse the installed CLI and app-server. Do not add a production dependency without the user's explicit approval.
- Use a `codex/*` branch while implementing and reviewing a change. The active Codex feature-parity goal authorizes release publication and production actions; push or merge to `main` after the version, staged scope, and release workflow effects have been verified.
- Before committing, inspect the staged diff and run `git diff --cached --check`. Keep commits limited to this task.
- Build with `dotnet build --no-restore`; run focused verification appropriate to the behavior changed and report any unverified runtime paths.
- Store durable agent-specific project facts in `.codex/`; keep unfinished goal state in `.codex/tasks/` and remove it when the goal is complete.
