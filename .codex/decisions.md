# Active technical decisions

## Use the pinned standalone App Server runtime

- Status: active.
- Decision: use the official `codex-app-server` Windows package pinned at `0.163.0`; drive turns, model discovery, history, review, approvals, login, and worktree forks through its JSON-RPC protocol. Use Git worktrees for local isolation. Keep CLI subcommands only for plugin marketplace operations not yet recommended for production app-server clients.
- Reason: OpenAI documents that Codex Desktop and IDE clients bundle or fetch a platform-specific App Server binary and keep it pinned to a tested version. This gives CodexGUI the same client/harness boundary without requiring the CLI TUI or CLI package for regular work.
- Constraint: verify the package SHA-256 and required files before extraction. Verify protocol requests against the pinned package schema and current official docs.
- Sources: [OpenAI harness architecture](https://openai.com/index/unlocking-the-codex-harness/), [official Codex package builder](https://github.com/openai/codex/blob/main/scripts/codex_package/README.md), [app-server docs](https://learn.chatgpt.com/docs/app-server).

## Run local scheduled tasks through the detached worker

- Status: active.
- Decision: keep minute-interval and daily/weekly/monthly schedules in CodexGUI's LocalAppData store and check due times while the desktop app is open. A standalone schedule may target several projects; create one independent run and worktree per project. Launch each run through the existing detached App Server worker; use `approvalPolicy=never` only with `workspaceWrite` and sandbox network access disabled, with a managed Git worktree enabled by default for isolation.
- Reason: scheduled tasks cannot wait for an interactive approval prompt. The bounded sandbox lets a run proceed unattended while limiting writes to its working directory; a managed worktree keeps the project checkout separate.
- Constraint: closing CodexGUI stops schedule polling, though already launched workers continue and are reconciled on next launch. Local schedules are not an OS background service. Existing-thread continuation is limited to one project because a Codex thread has one working directory. User-input requests receive empty answers and permission/MCP elicitations are cancelled in unattended runs.
- Source: `CodexAutomationStore.cs`, `CodexAppServerTurnWorker.cs`, `MainWindow.xaml.cs`, and user workflow in `README.md`.

## Allow scheduled tasks to resume an existing thread

- Status: active.
- Decision: an automation may target an existing thread via App Server `thread/resume`; retain the thread's original working directory and, when applicable, its already registered Git worktree. Do not create a new worktree for a resumed thread.
- Reason: users can carry conversation context into later scheduled runs while the App Server keeps the canonical thread history.
- Constraint: `thread/resume` does not move a thread to a new `cwd`; validate the saved project/worktree path before starting. Avoid concurrent automation or interactive runs in the same thread. Keep an unavailable saved thread selection visible in the editor instead of silently replacing it with a new thread.
- Source: `CodexAutomationStore.cs`, `CodexAutomationsWindow.cs`, `CodexAppServerTurnWorker.cs`, `MainWindow.xaml.cs`, and the App Server thread lifecycle docs.

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

## Preserve all child threads from multi-agent events

- Status: active.
- Decision: treat each App Server `collabToolCall` item as a state update that can reference multiple child threads. Aggregate the latest status by child thread ID, expose a current-chat agent list, and open each child through the existing thread-history flow.
- Reason: the pinned `0.163.0` payload contains `receiverThreadIds` and `agentsStates`; legacy `newThreadId` may identify only one receiver.
- Source: pinned `rust-v0.163.0` `CollabToolCallItem` schema and `CreateActivityMessage` / `CodexSubagentsWindow.cs`.

## Run interactive terminals through App Server

- Status: active.
- Decision: host PowerShell tabs through pinned `command/exec` PTY sessions. In the default Windows workspace sandbox, streaming PTY sessions are rejected by App Server; use `dangerFullAccess` only after explicit per-window consent, unless the project already has Full Access enabled.
- Reason: the pinned Windows App Server exposes a stable PTY command API, while the experimental `process/spawn` API is not present in the pinned `0.163.0` package.
- Constraint: shell commands run with the current Windows user's full file access after consent; no sandbox boundary remains for that terminal. Full-screen TUI rendering has not been verified.
- Source: official `0.163.0` command-exec schema, App Server docs, and a local PTY smoke test; implementation is in `CodexAppServerTerminalHost.cs` and `CodexTerminalWindow.cs`.

## Keep repository actions in the existing Git inspector

- Status: active.
- Decision: implement local stage, unstage, restore, commit, and push actions through `GitRepositoryInspector` using argument-list Git calls; never interpolate repository paths or commit text into a shell command. Diff hunks use separate staged/unstaged patches; restore and unstaged-hunk revert ask for confirmation, commit only records staged changes, and push never uses force.
- Reason: the project already requires Git for status and managed worktrees, so these controls do not need a new dependency or a parallel repository service.
- Constraint: serialize repository mutations against active Codex runs; use literal Git pathspecs for per-file actions. In an unborn repository, unstage with `git rm --cached` so working files remain intact because `git restore --staged` requires `HEAD`. Hunk actions are limited to supported text diffs. Untracked cleanup must preview `git clean -nd` and confirm before using `git clean -fd`; never use `-x`, so ignored files remain untouched. Pull-request creation remains a parity gap.
- Source: `GitRepositoryInspector.cs`, `MainWindow.xaml.cs`, and the official local-environments/Git controls guide.

## Store chat goals in App Server thread state

- Status: active.
- Decision: manage a chat's persistent goal with App Server `thread/goal/get`, `thread/goal/set`, and `thread/goal/clear`; label explicit UI and slash-command mutations with `origin: "user"` and do not duplicate goal state in CodexGUI's local chat metadata.
- Reason: the pinned protocol defines these stable methods as the same goal state exposed by Codex's `/goal` command, so other Codex clients can continue the goal.
- Constraint: only offer goal management after the chat has a persisted App Server thread ID. Keep the local button label as an ephemeral cache and re-read canonical state when opening the editor.
- Source: pinned `rust-v0.163.0` `ThreadGoal*` schemas and official App Server and slash-command docs; implementation is in `CodexAppServerClient.cs`, `CodexThreadGoalWindow.cs`, and `MainWindow.xaml.cs`.
