# CodexGUI project instructions

## Project facts

- CodexGUI is a Windows WPF desktop client targeting `net10.0-windows`. It downloads a pinned official Codex App Server package into `%LOCALAPPDATA%\CodexGUI\app-server-runtimes` on first launch; normal chats do not require an installed Codex CLI.
- The application entry point and shared WPF resources live in `Application/`; the main window and event orchestration live in `UI/Windows/MainWindow.xaml.cs`, with state and automation partials beside it. The hot-reloaded workspace layout is `Views/WorkspaceView.xaml`.
- Supporting source is organized under `AppServer/`, `Automations/`, `Git/`, `Infrastructure/`, `Integrations/`, and `UI/`; embedded PowerShell scripts and the development launcher are under `Scripts/`. The application manifest is under `Properties/`.
- CLI-backed plugin management remains an optional integration; chat queues, worktree paths, and thread metadata are persisted under `%LOCALAPPDATA%\CodexGUI`.
- The release workflow in `.github/workflows/release.yml` runs on pushes to `main`, publishes a GitHub release, and deletes older versioned releases and tags.

## Engineering rules

- Preserve the shared Codex configuration and history, Git worktree paths, queued prompts, and active-run recovery when changing the execution pipeline.
- Confirm app-server methods and payloads from the pinned package schema or official OpenAI sources; do not invent RPC methods.
- Use the pinned official App Server package for normal turns. Keep CLI-only operations optional; do not add unrelated production dependencies without the user's explicit approval.
- Keep `main` as the only local and remote branch. Work directly on `main`; never create feature, task, review, or other branches. Merge existing branch work into `main` and remove obsolete branch refs after preserving their commits. Push only after verifying the version, staged scope, and release workflow effects.
- Before committing, inspect the staged diff and run `git diff --cached --check`. Keep commits limited to this task.
- Build with `dotnet build --no-restore`; run focused verification appropriate to the behavior changed and report any unverified runtime paths.
- Store durable agent-specific project facts in `.codex/`; keep unfinished goal state in `.codex/tasks/` and remove it when the goal is complete.
