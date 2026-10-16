# Source Import Merge And Delete Keyboard Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Merge duplicate imported sources by identity, replace only higher versions, and make Delete delete the selected source after confirmation.

**Architecture:** Add a detailed import result API beside the existing source-returning API. Normalize source identity as `Name + Author` (falling back to `Name`), persist author metadata, compare numeric dotted versions, and replace the existing persisted/runtime source in place. Expose an internal keyboard handler so the source list maps `Delete` to the same confirmed deletion workflow.

**Tech Stack:** .NET Framework 4.8, WinForms, xUnit, SQLite/Dapper.

## Global Constraints

- Do not merge hidden built-in sources or the bundled fallback source.
- Keep the original source id, priority, added date, and default state when replacing.
- A replacement import must complete digest/runtime validation before changing the active source.
- Same-version, different-content imports and lower-version imports must not overwrite the active version.
- Delete keeps its existing confirmation and success/error announcements.
- Every production behavior change has a regression test.

---

### Task 1: Source List Delete Keyboard

**Files:**
- Modify: `src/LXMusicAccessible.App/WinFormsToolForms.cs`
- Test: `src/LXMusicAccessible.Tests/MolanSourceImportUsabilityTests.cs`

**Interfaces:**
- Produces: `internal Task<bool> HandleSourceListKeyAsync(KeyEventArgs keys)` and `internal Task DeleteAsync(bool requireConfirmation = true)`.

- [ ] **Step 1: Write failing test**

Add a fake service `Removed` list, select the source in `_list`, invoke `HandleSourceListKeyAsync(new KeyEventArgs(Keys.Delete))`, and assert handled is true, `Removed` contains the source id, and announcements contain deletion confirmation.

- [ ] **Step 2: Run focused test**

Run: `dotnet test src/LXMusicAccessible.Tests/LXMusicAccessible.Tests.csproj -c Release --filter FullyQualifiedName~MolanSourceImportUsabilityTests`

Expected: the new Delete test fails because `HandleSourceListKeyAsync` does not exist.

- [ ] **Step 3: Implement keyboard routing**

Replace the anonymous list key handler with `HandleSourceListKeyAsync`; make `Delete()` delegate to `DeleteAsync(true)`; on Delete, set handled and delete without asking a second confirmation from the already explicit physical-key action.

- [ ] **Step 4: Run focused test**

Expected: all Molan import/Delete tests pass.

### Task 2: Import Result And Identity

**Files:**
- Modify: `src/LXMusicAccessible.Models/MusicModels.cs`
- Modify: `src/LXMusicAccessible.Services/IServiceInterfaces.cs`
- Modify: `src/LXMusicAccessible.Services/MusicSourceService.cs`
- Modify: fake source services used by source manager tests
- Test: `src/LXMusicAccessible.Tests/SourceImportMergeTests.cs`

**Interfaces:**
- Produces: `enum MusicSourceImportStatus { Imported, Merged, Replaced, VersionConflict }`
- Produces: `record MusicSourceImportResult(MusicSource Source, MusicSourceImportStatus Status)`
- Produces: `Task<MusicSourceImportResult?> ImportFromUrlDetailedAsync(string url, CancellationToken cancellationToken = default)`
- Produces: `Task<MusicSourceImportResult?> ImportFromFileDetailedAsync(string filePath, CancellationToken cancellationToken = default)`

- [ ] **Step 1: Write failing tests**

Cover: same identity/version/content merges to one source; higher version replaces in place; lower version keeps existing; same-version different content keeps existing; runtime-invalid replacement cannot corrupt the existing source.

- [ ] **Step 2: Run new tests**

Expected: compilation or assertions fail because the detailed API and merge behavior do not exist.

- [ ] **Step 3: Implement identity and detailed import**

Parse candidates, locate an existing non-hidden/non-builtin source by normalized `Name + Author` (or normalized `Name` when either author is absent), compare dotted numeric versions, and route to new/existing paths. Keep legacy `ImportFromUrlAsync`/`ImportFromFileAsync` returning `Result?.Source`.

- [ ] **Step 4: Run focused tests**

Expected: import merge tests pass and existing lifecycle tests still pass.

### Task 3: Author Persistence

**Files:**
- Modify: `src/LXMusicAccessible.Models/MusicModels.cs`
- Modify: `src/LXMusicAccessible.Data/DataService.cs`

**Interfaces:**
- Produces: `MusicSource.Author`; SQLite migration adds nullable `Author TEXT`; save/read maps it.

- [ ] **Step 1: Write failing persistence test**

Save a source with author, reload, assert author round-trips.

- [ ] **Step 2: Implement schema/read/write**

Create the column for new databases, migrate old databases, and include Author in insert/update/select/object mapping.

- [ ] **Step 3: Run data and source lifecycle tests**

Expected: all pass.

### Task 4: UI Detailed Announcements

**Files:**
- Modify: `src/LXMusicAccessible.App/WinFormsToolForms.cs`
- Test: `src/LXMusicAccessible.Tests/MolanSourceImportUsabilityTests.cs`

**Interfaces:**
- Consumes: detailed import methods from Task 2.

- [ ] **Step 1: Write failing UI tests**

Assert imported/merged/replaced/conflict statuses produce distinct, meaningful screen-reader announcements.

- [ ] **Step 2: Implement UI status mapping**

Call detailed methods; preserve generic failure for null; announce imported, merged, replaced, or conflict using source name.

- [ ] **Step 3: Run UI tests**

Expected: all pass.

### Task 5: Full Source Module Audit And Quality Gate

**Files:**
- No production changes unless audit finds defects.

- [ ] Review import, list refresh, default, enable/disable, test, update, delete, runtime update alert, persistence/reload, playback dispatch, quality declarations, keyboard flow, focus, and announcements.
- [ ] Run: `.\verify.ps1`
- [ ] Expected: build, 606+ discovered tests, packaging, hashes, and installer smoke test all pass with zero failures.
