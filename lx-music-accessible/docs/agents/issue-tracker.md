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

- Each implementation issue is permanently owned by exactly one dedicated sub-agent from first read through closure.
- The ticket sub-agent performs the implementation, regression tests, self-review against both the issue specification and repository standards, fixes review findings, runs final validation, creates the final commit, records the commit and validation summary, and closes the issue.
- Final validation uses the repository's local `verify.ps1` gate. The ticket comment records exact local results and executed test count; no remote workflow or PR check is required or expected.
- A ticket sub-agent must not spawn another agent for implementation or review. If more work is needed in a later turn, resume the same canonical agent instead of creating a replacement.
- The root agent only selects tickets on the implementation frontier, starts or resumes the owning sub-agent, monitors status, and maintains the dependency graph. It does not edit implementation files, perform the ticket review, create the ticket commit, or close the issue on the sub-agent's behalf.
- The shared worktree permits only one active implementation ticket at a time. Start the next ticket only after the current ticket has committed its work and left the worktree in a handoff-safe state.
- Do not close a partially completed or blocked ticket. If required authority, human verification, or environment state is unavailable, keep the issue open and report the blocker.

## Skill operations

- When a skill says to publish to the issue tracker, create a GitHub issue.
- When a skill says to fetch a ticket, read its full body, comments, and labels.
- Do not close or modify a parent specification issue when publishing child tickets.
