# Quality Probe, Playback Detail, And Album Download Design

Date: 2026-10-12

## Goal

Let screen-reader users see and choose the qualities that are actually playable for the selected song, correct playback detail source/quality reporting, and place album downloads in a folder named after the album.

## Scope

### In scope

1. A shared quality catalog that maps canonical and source-specific quality IDs to Chinese labels.
2. On-demand probing of the selected song's candidate qualities.
3. Quality submenus in the main playback menu and song context menu.
4. Correct playback detail fields for quality and source.
5. Whole-album download with an album-name subdirectory.

### Out of scope

- Automatic background probing while browsing or changing focus.
- Downloading every quality for one song.
- Changing the configured default play/download qualities.
- Bypassing provider authorization or downloading qualities that fail validation.

## Quality Catalog

`QualityCatalog` becomes the single mapping point for quality IDs and user-facing labels. It must include the existing canonical IDs:

- `128k` — 标准
- `192k` — 较高
- `320k` — 极高
- `flac` — 无损 FLAC
- `flac24bit` — Hi-Res 无损
- `hires` — Hi-Res
- `atmos` — 杜比全景声
- `atmos_plus` — 杜比全景声+
- `master` — 母带
- `jyeffect` — 高清环绕声
- `sky` — 沉浸环绕声
- `dolby` — 杜比
- `jymaster` — 珍品/超清母带
- `viper` — 蝰蛇音质

Unknown provider IDs remain visible as `原始ID` rather than being discarded. The catalog also exposes a stable display order from highest fidelity to standard quality.

## Availability Model

A quality has one of these states:

- `Unknown` — not yet probed.
- `Probing` — URL or media validation is running.
- `Playable` — the source returned a URL and the media check passed.
- `Unavailable` — URL resolution failed or the media check rejected it.
- `Failed` — probing failed because of a network or provider error; the user may retry.

The cache key is `source|songId`. Results are in-memory only. A failed result may be retried from the menu and expires immediately when the app restarts. Successful results expire after 10 minutes because provider URLs are temporary.

## Candidate Interfaces

`MusicSourceService` gains an exact candidate enumerator so probing cannot accidentally use cross-source fallback:

- Enumerate only enabled, non-hidden source interfaces that explicitly support the song's platform.
- For each interface, return its declared qualities for that platform and its display name/priority.
- Include enabled built-in interfaces when they support the platform.
- Do not accept an arbitrary interface that happens to support the platform but was not selected as a candidate.
- Add an exact play-URL resolver that targets one interface ID and quality; existing global fallback playback behavior remains unchanged.

For each song, the same canonical quality may be available from more than one interface. The menu aggregates them by quality label; the first successful interface is included in the accessible status, for example `无损 FLAC，墨澜音源，可播放`.

## Probe Workflow

`QualityProbeWorkflow` performs an on-demand probe for one `MusicInfo`:

1. Ask `MusicSourceService` for enabled interface candidates that explicitly support the song's platform.
2. Expand and normalize all interface-declared qualities through `QualityCatalog`.
3. Add the active song's declared quality when present.
4. Remove duplicate quality labels and order from highest fidelity to standard.
5. Probe interface candidates in priority order, then qualities from highest fidelity to standard.
6. For each candidate, call the exact interface resolver with that exact quality.
7. If a URL is returned, make a bounded Range request and inspect response status, content type, length, magic bytes, and FLAC STREAMINFO where possible.
8. Mark the quality `Playable` only when URL resolution and media validation succeed. Record the successful interface. Mark unvalidated candidates `Unavailable` after all candidates for that quality fail.
9. Continue until every quality label has a state or probing is cancelled.

The probe uses a timeout per candidate, observes the caller's cancellation token, and runs through the existing background-work host. It does not download the complete file.

## Menu Behavior

### Main playback menu

The playback menu contains a new submenu named `当前歌曲音质`.

- Before probing: items show `探测中` and are disabled.
- While probing: items show `探测中` and are disabled.
- After probing: playable items are enabled and read, for example, `无损 FLAC，可播放`; unavailable items are disabled.
- Selecting an enabled item plays the current song at that quality.
- The submenu refreshes safely when the active song changes.

### Song context menu

Every song context menu contains `下载音质`.

- It probes the focused song only when the menu opens.
- Selecting an available quality starts a download at that quality.
- Other selected songs are not affected; this submenu applies to the focused song.
- The existing default-quality download command remains available.

Announcements avoid duplicate speech. A menu that is still probing does not repeatedly announce progress.

## Playback Detail Corrections

The playback detail dialog must distinguish declared and actual information:

- `音质` — the source metadata quality for the song.
- `当前播放音质` — the detected quality of the active stream, only when the detail song matches the active song by both `Source` and `Id`.
- `音源` — a localized display name such as `网易云`, `QQ音乐`, `酷狗`, `酷我`, or `咪咕`; unknown source IDs are shown unchanged.

The stale actual-quality bug is fixed by comparing both source and ID, not ID alone.

## Album Download

When the album track list is active:

- A new `下载整张专辑` command appears in the song context menu.
- The workflow first uses loaded tracks; if the album has more pages, it loads remaining pages before starting the download.
- The download request uses `Subdirectory` with the album title.
- Album title is passed through existing safe filename sanitization and directory creation.
- The busy message identifies the album, for example `已开始批量下载专辑《专辑名》：12 首歌曲`.
- Failure to load remaining pages cancels the album download with an accessible error instead of silently downloading a partial album.

## Technical Design

- Add a shared quality catalog in the models or services layer so WinForms and workflows use one mapping.
- Add `QualityProbeWorkflow` and a small result record in the services layer.
- Add the workflow to application composition and the shell services seam.
- Use WinForms `ContextMenu` dynamic submenu updates on the UI thread; background probes must marshal menu updates through the form.
- Keep the default quality settings unchanged.
- Use the existing download request `Subdirectory` field; no persistence format changes are required.

## Error And Accessibility Rules

- Menu labels and state changes must be perceivable by screen readers.
- Probe failures use a retryable state rather than pretending a quality is unavailable.
- Download and playback selections require explicit menu activation.
- Focus is not moved by asynchronous probe completion.
- Errors are announced once per activity ID.

## Testing

Required deterministic tests:

1. Quality catalog maps all supported IDs, preserves unknown IDs, and orders qualities correctly.
2. Probe workflow enumerates exact enabled interface candidates, skips invalid candidates, and marks only validated candidates playable.
3. Probe cache prevents repeated work for the same song and source.
4. Probe cancellation and candidate failures do not crash the workflow.
5. Main playback and song context menus expose states and actions without moving focus.
6. Song detail uses localized source text and does not attach stale actual quality across source changes.
7. Whole-album download loads remaining pages and passes the sanitized album name as the subdirectory.
8. Existing default-quality download and playback behavior remains unchanged.

## Acceptance Criteria

- A screen-reader user can open either menu and hear which qualities are playable.
- Selecting a playable quality uses that exact requested quality.
- A song's detail no longer shows another song's actual quality or an untranslated source ID.
- Downloading an album creates `<download root>/<album name>/` and contains that album's tracks.
- Browsing lists and moving focus do not trigger network probes.

## Risks And Mitigations

- Providers may return generic URLs for every requested quality. The workflow therefore validates media bytes instead of trusting URL success alone.
- Encrypted or provider-specific containers may not expose complete metadata. The workflow labels them from the requested/declared quality and does not claim a higher unverified quality.
- Probing every candidate can be slow. High-to-low probing, bounded requests, caching, and on-demand invocation limit the impact.
