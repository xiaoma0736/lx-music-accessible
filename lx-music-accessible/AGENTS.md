## Agent skills

### Issue tracker

Specs and implementation tickets are tracked in GitHub Issues. See `docs/agents/issue-tracker.md`.

### Ticket execution

Each implementation ticket is owned end-to-end by exactly one dedicated sub-agent. The root agent only dispatches and tracks work. See `docs/agents/issue-tracker.md` for the mandatory execution contract.

Ticket closure uses the repository's local `verify.ps1` quality gate. This repository does not use a remote CI/CD workflow.

### Triage labels

Use the repository's canonical five-role triage vocabulary. See `docs/agents/triage-labels.md`.

### Domain docs

This repository uses a single-context domain documentation layout. See `docs/agents/domain.md`.
