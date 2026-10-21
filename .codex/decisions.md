# Active technical decisions

## Use the pinned standalone App Server runtime

- Status: active.
- Decision: use the official `codex-app-server` Windows package pinned at `0.163.0`; drive turns, model discovery, history, review, approvals, login, and worktree forks through its JSON-RPC protocol. Use Git worktrees for local isolation. Keep CLI subcommands only for plugin marketplace operations not yet recommended for production app-server clients.
- Reason: OpenAI documents that Codex Desktop and IDE clients bundle or fetch a platform-specific App Server binary and keep it pinned to a tested version. This gives CodexGUI the same client/harness boundary without requiring the CLI TUI or CLI package for regular work.
- Constraint: verify the package SHA-256 and required files before extraction. Verify protocol requests against the pinned package schema and current official docs.
- Sources: [OpenAI harness architecture](https://openai.com/index/unlocking-the-codex-harness/), [official Codex package builder](https://github.com/openai/codex/blob/main/scripts/codex_package/README.md), [app-server docs](https://learn.chatgpt.com/docs/app-server).

## Run local scheduled tasks through the detached worker

- Status: active.
- Decision: keep daily/weekly schedules in CodexGUI's LocalAppData store and check due times while the desktop app is open. Launch each run through the existing detached App Server worker; use `approvalPolicy=never` only with `workspaceWrite` and sandbox network access disabled, with a managed Git worktree enabled by default for isolation.
- Reason: scheduled tasks cannot wait for an interactive approval prompt. The bounded sandbox lets a run proceed unattended while limiting writes to its working directory; a managed worktree keeps the project checkout separate.
- Constraint: closing CodexGUI stops schedule polling, though already launched workers continue and are reconciled on next launch. Local schedules are not an OS background service.
- Source: `CodexAutomationStore.cs`, `CodexAppServerTurnWorker.cs`, `MainWindow.xaml.cs`, and user workflow in `README.md`.
