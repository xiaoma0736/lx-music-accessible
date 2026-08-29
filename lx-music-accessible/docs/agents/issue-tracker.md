# Issue tracker: GitHub

Issues and specs for this repository live in GitHub Issues. Use the `gh` CLI for tracker operations and infer the repository from the configured git remote.

## Conventions

- Create one GitHub issue for each spec or implementation ticket.
- Read an issue body, comments, and labels before acting on it.
- Apply and remove labels with `gh issue edit`.
- Publish completed specs and agent-ready tickets with the `ready-for-agent` label.
- Pull requests are not a request or triage surface for this repository.

## Blocking relationships

Publish blocking edges with GitHub's native issue dependencies when the endpoint is available. Dependency API calls use the blocker's numeric database ID, not its issue number or node ID. If native dependencies are unavailable, add a `Blocked by: #<issue>` line to the ticket body.

A ticket is on the implementation frontier when all of its blockers are closed and the ticket is otherwise unclaimed.

## Ticket execution ownership

- Before the first implementation ticket starts, the root agent creates one dedicated development branch from the branch that is currently checked out. Every ticket in the approved series is implemented and committed on that development branch.
- Each implementation issue is permanently owned by exactly one dedicated sub-agent from first read through closure.
- The ticket sub-agent performs the implementation, regression tests, self-review against both the issue specification and repository standards, fixes review findings, runs final validation, creates the final commit, records the commit and validation summary, and closes the issue.
- Final validation uses the repository's local `verify.ps1` gate. The ticket comment records exact local results and executed test count; no remote workflow or PR check is required or expected.
- A ticket sub-agent must not spawn another agent for implementation or review. If more work is needed in a later turn, resume the same canonical agent instead of creating a replacement.
- The root agent only selects tickets on the implementation frontier, starts or resumes the owning sub-agent, monitors status, and maintains the dependency graph. It does not edit implementation files, perform the ticket review, create the ticket commit, or close the issue on the sub-agent's behalf.
- The shared worktree permits only one active implementation ticket and one ticket-owning sub-agent at a time. The owning sub-agent must commit, report validation, and close its issue before the root agent creates the dedicated sub-agent for the next ticket.
- When several tickets are technically on the frontier, execute them in the numbered order approved with their specification.
- Do not close a partially completed or blocked ticket. If required authority, human verification, or environment state is unavailable, keep the issue open and report the blocker.

## Implementation skill discipline

- The ticket-owning sub-agent reads and uses every available skill triggered by the ticket before changing code.
- Use `ponytail` at its default full intensity for implementation and self-review: reuse existing seams and native WPF behavior, keep the diff minimal, and add no speculative framework or dependency. `ponytail` is mandatory but not exclusive; use accessibility, diagnosis, testing, review, and other available skills whenever their trigger conditions apply.
- Accessibility, validation, error handling, data safety, issue acceptance criteria, and the local `verify.ps1` gate are hard requirements and are never simplified away.

## Scope decisions

- T11 / #12 is cancelled as `NOT_PLANNED`, not completed. Do not use it as evidence that installer accessibility was implemented.
- The authoritative automated-versus-human accessibility boundary and pre-release checklist live in `tasks/comprehensive-remediation-spec-2026-08-23.md` under **Testing Decisions**.

## Skill operations

- When a skill says to publish to the issue tracker, create a GitHub issue.
- When a skill says to fetch a ticket, read its full body, comments, and labels.
- Do not close or modify a parent specification issue when publishing child tickets.
