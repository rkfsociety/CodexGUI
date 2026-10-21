# Codex-specific project rules

- Use the pinned official Codex App Server package as the compatibility boundary for regular chat. Check its version-specific schema before writing protocol payloads; use the installed CLI only for remaining CLI-only commands.
- Normal streamed chats and new worktree forks use `CodexAppServerTurnWorker`; keep its file-backed run log/control contract so turns survive closing the main window and can be reattached. `CodexCliWorker.ps1` remains for legacy-run recovery.
- Keep published worker executables in the versioned local runtime cache, outside the install directory, so an active turn cannot hold up app self-update. Retain old cache versions while active runs refer to them.
- `CodexAppServerClient` is still a short-lived request/response client. Use the dedicated turn worker for server-initiated approvals and streamed notifications.
- The app-server protocol is versioned with the downloaded runtime. Verify RPC methods and payloads against its generated schema and current official docs; prefer the pinned standalone package over the experimental generic CLI `app-server` subcommand in released builds.
- The packaged App Server version and archive SHA-256 are pinned in `CodexAppServerRuntime.cs`. Bump both together from official release metadata, then smoke-test the extracted standalone binary before publishing.
- Feature releases on `main` publish and prune versioned GitHub releases. The active Codex feature-parity goal authorizes final production publication after the release version and workflow effects are verified; keep interim feature work on a branch.
