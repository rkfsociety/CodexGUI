# Active technical decisions

## Use app-server for normal chat turns

- Status: active.
- Decision: drive ordinary new and resumed chats through Codex app-server JSON-RPC; keep CLI subcommands only where the app-server path does not yet cover the operation, such as managed-worktree bootstrap and CLI review.
- Reason: OpenAI documents app-server as the rich-client interface for persistent conversations, streamed events, approvals, and turn control. It is the same harness interface used by Codex clients, without running the CLI TUI.
- Constraint: the current app package still requires an installed `codex.exe` runtime. The documented `codex app-server` CLI command is currently labeled experimental, so protocol requests must be verified against the installed runtime schema and current official docs.
- Sources: [OpenAI harness architecture](https://openai.com/index/unlocking-the-codex-harness/), [app-server docs](https://learn.chatgpt.com/docs/app-server).
