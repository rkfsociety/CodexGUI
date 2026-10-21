# Codex-specific project rules

- Use the installed Codex CLI/app-server protocol as the compatibility boundary. Check its schema before writing protocol payloads.
- Normal streamed chats use `CodexAppServerTurnWorker`; keep its file-backed run log/control contract so turns survive closing the main window and can be reattached. `CodexCliWorker.ps1` remains the managed-worktree bootstrap path.
- Keep published worker executables in the versioned local runtime cache, outside the install directory, so an active turn cannot hold up app self-update. Retain old cache versions while active runs refer to them.
- `CodexAppServerClient` is still a short-lived request/response client. Use the dedicated turn worker for server-initiated approvals and streamed notifications.
- The app-server protocol is versioned with the installed Codex runtime. Verify RPC methods and payloads against its generated schema and current official docs; upstream currently labels the CLI app-server command experimental.
- Feature releases on `main` publish and prune versioned GitHub releases. The active Codex feature-parity goal authorizes final production publication after the release version and workflow effects are verified; keep interim feature work on a branch.
