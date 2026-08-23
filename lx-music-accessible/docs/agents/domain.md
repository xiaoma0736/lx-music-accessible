# Domain documentation

This repository uses a single-context domain documentation layout.

## Before exploring or implementing

- Read `CONTEXT.md` at the repository root when it exists.
- Read ADRs under `docs/adr/` that affect the area being changed.
- If these files do not exist, continue without treating their absence as a blocker.

## Vocabulary

Use terminology defined by `CONTEXT.md` in specifications, tickets, tests, architecture decisions, and code. Avoid introducing synonyms for established domain concepts. If a required concept has no agreed term, record the gap for domain-modeling rather than silently inventing competing vocabulary.

## Architecture decisions

Surface conflicts with an existing ADR explicitly. Do not silently override an accepted decision.
