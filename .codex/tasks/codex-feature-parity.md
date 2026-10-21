# Codex feature parity

Status: in progress. Objective: match Codex desktop workflows supported by the installed Codex CLI and app-server, without publishing releases from feature work.

Completed and published on `main`: pre-window update check and install (`0.0.010`); per-chat managed worktrees.

Current branch: `codex/app-parity-thread-fork`. This branch adds a context-menu action that forks a persisted Codex session through app-server `thread/fork`, restores the copied history, and preserves the source chat. The installed app-server schema includes `ThreadForkParams` and `ThreadForkResponse`; local build passed. Runtime fork behavior still needs a safe end-to-end check with a disposable session. The same branch also adds a review action for uncommitted changes using the installed CLI’s `codex review --uncommitted` command, a per-file diff viewer, and a way to add selected diff lines to the prompt; local build passed, runtime review behavior is unverified.

Known gaps to investigate against current official Codex features: syntax-highlighted, line-anchored review comments; interactive approval and turn steering; scheduled automations; and broader multi-agent workflows. Keep feature work on non-main branches; do not trigger release automation during parity work.
