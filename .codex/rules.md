# Codex-specific project rules

- Use the pinned official Codex App Server package as the compatibility boundary for regular chat. Check its version-specific schema before writing protocol payloads; use the installed CLI only for remaining CLI-only commands.
- Normal streamed chats and new worktree forks use `CodexAppServerTurnWorker`; keep its file-backed run log/control contract so turns survive closing the main window and can be reattached. `Scripts/CodexCliWorker.ps1` remains for legacy-run recovery.
- Keep published worker executables in the versioned local runtime cache, outside the install directory, so an active turn cannot hold up app self-update. Retain old cache versions while active runs refer to them.
- `CodexAppServerClient` is still a short-lived request/response client. Use the dedicated turn worker for server-initiated approvals and streamed notifications.
- The app-server protocol is versioned with the downloaded runtime. Verify RPC methods and payloads against its generated schema and current official docs; prefer the pinned standalone package over the experimental generic CLI `app-server` subcommand in released builds.
- The packaged App Server version and archive SHA-256 are pinned in `AppServer/CodexAppServerRuntime.cs`. Bump both together from official release metadata, then smoke-test the extracted standalone binary before publishing.
- Keep `main` as the only local and remote branch. Work directly on `main`; never create feature, task, review, or other branches. Merge existing branch work into `main` and remove obsolete branch refs after preserving their commits. Feature releases on `main` publish and prune versioned GitHub releases; verify the release version and workflow effects before pushing.
