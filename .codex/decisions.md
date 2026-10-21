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
- Constraint: closing CodexGUI stops schedule polling, though already launched workers continue and are reconciled on next launch. Local schedules are not an OS background service. User-input requests receive empty answers and permission/MCP elicitations are cancelled in unattended runs.
- Source: `CodexAutomationStore.cs`, `CodexAppServerTurnWorker.cs`, `MainWindow.xaml.cs`, and user workflow in `README.md`.

## Handle App Server interactive requests in the client

- Status: active.
- Decision: route `item/tool/requestUserInput`, `mcpServer/elicitation/request`, and `item/permissions/requestApproval` through dedicated user dialogs and the existing worker response-control channel. Grant only selected permission categories; default to turn scope and require an explicit click before opening an MCP URL.
- Constraint: worker initialization opts into `experimentalApi` for tool input and `mcpServerOpenaiFormElicitation` for MCP forms. Unattended workers cancel MCP elicitations and grant no additional permissions.
- Source: the pinned App Server `0.163.0` protocol schema and the App Server documentation; implementation lives in `CodexAppServerRequestDialog.cs`, `CodexAppServerTurnWorker.cs`, and `MainWindow.xaml.cs`.

## Keep diff comment threads local until attached to a chat prompt

- Status: active.
- Decision: render syntax-colored diff lines and let users draft separate line-anchored comment threads and replies in the reviewer. Add the collected context to the chat composer only when requested; keep these drafts in memory until attached.
- Reason: the pinned App Server exposes `review/start` but no production comment-thread RPC; the existing workflow sends selected diff context through the normal chat turn.
- Source: `DiffReviewerWindow.cs`, `MainWindow.xaml.cs`, and `README.md`.
