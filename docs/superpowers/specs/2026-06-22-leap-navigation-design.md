# Leap Navigation — Design Spec (Console Column File Manager)

**Date**: 2026-06-22  
**Adapted from**: rwf `2026-06-22-leap-navigation-design.md`  
**Status**: Design approved, ready for implementation planning

---

## Overview

Leap Navigation is a keyboard-only mode for navigating to any file or directory in the current column without using arrow keys or a separate search dialog. Inspired by nnn's Navigate-As-You-Type, but with a novel **buffer-as-path-trace** model that lets the user backtrack through auto-entered directories by pressing Backspace.

**Goals:**
- Enter, filter, and navigate to any item without leaving the keyboard home row
- Buffer always reflects where you are — no hidden state
- IME-free Japanese filename matching via Migemo (optional, already integrated)
- Works at 50,000-entry scale without blocking the UI

### Key difference from existing Search mode (`/`)

Search mode highlights matches inside the active column but does **not** hide non-matching entries. Leap mode **hides** non-matching entries entirely and navigates across directory boundaries. The two modes serve different purposes and coexist.

---

## Entry / Exit

| Key | Action |
|-----|--------|
| `F3` | Enter leap mode. Cursor and directory do not move. F3 is currently unbound — safe to use. |
| `F3` (again) | Exit leap mode, cursor stays on current item (confirm position without action). |
| `Esc` | Cancel leap — cursor returns to pre-leap position, pre-leap directory. |

On entry, the LEAP bar appears at the bottom of the screen (replacing the normal status bar, same as search mode does with `BuildSearchStatusBar`). The active column's entry directory is stored as the **leap root** (used by `Ctrl+K`).

> **Note on `Esc`:** In normal mode, `Esc` quits the app. This is safe to repurpose inside leap mode — the existing search mode already captures `Esc` in `HandleSearchKeyAsync` before it reaches the quit check. Leap mode follows the same pattern via `HandleLeapKeyAsync`.

---

## Buffer Model — "/" as Depth Separator

The buffer is a single string that accumulates input across directory traversals. When entering a directory (auto-enter or `→`), a `/` is **automatically appended**. The buffer therefore encodes the full navigation path:

```
mapm/we
^^^^^^  ^
trail   local filter (after last "/")
```

- **Trail** (`mapm/`): navigation history. Grayed out in the LEAP bar.
- **Separator** (`/`): depth marker. Dimly colored.
- **Local filter** (`we`): active filter in the current directory. Yellow.

The local filter is the part after the last `/`. It is applied to the active column's entries. The trail is display-only — it is not re-applied to entries.

### Entering a directory

Auto-enter fires when exactly one match exists and it is a directory. A `/` is appended to the buffer, local filter becomes empty, and all entries of the new directory are shown.

`→` (right arrow) also appends `/` when entering a directory manually. This navigates the **active column** into the subdirectory (same as `EnterAsync()` in normal mode, but within the active column only — the right-side columns rebuild as usual via `RebuildRightSideAsync`).

### Backspace behavior

Backspace removes one character from the right of the buffer. If the rightmost character is `/`, the user exits the current directory back to the parent, and the buffer now ends with the character that triggered the auto-entry:

```
mapm/  →  ⌫  →  mapm   (exit mapmodels/, back in parent, filter = "mapm")
mapm   →  ⌫  →  map    (re-filter parent, 2 matches appear)
```

No separate "go back" action needed — Backspace always unwinds exactly one character of the trace.

---

## Filtering Behavior

Leap uses **filter mode**: entries in the active column that do not match the local filter are **hidden entirely**. Only matching entries are visible.

This is different from search mode, which keeps all entries visible and only highlights matches. During leap mode, `DrawColumnToLines()` receives a `LeapState` and renders only the visible subset, building a `visibleIndices: List<int>` mapping from rendered row to `column.Entries` index.

- On empty local filter: all entries shown.
- Cursor is always on the first matching entry (i.e., `column.Selected = visibleIndices[0]`).
- `↑`/`↓` (or `Ctrl+P`/`Ctrl+N`) cycle through visible (matching) entries only.

### Debounce and zero-match handling

All keystrokes are accepted into the buffer immediately with no synchronous check. The full filter pipeline (prefix + substring + Migemo) fires only after `NavigationDebounceMs` of typing silence (`const int NavigationDebounceMs = 300` — reuse the existing constant, slightly higher than the spec's 150ms default).

**Why no sync check:** a synchronous prefix/substring check before debounce would incorrectly reject valid Migemo inputs (e.g. typing `"ni"` to reach `"日本"` has no ASCII match, so it would be wrongly blocked). Migemo must run before any rejection decision.

**Zero-match after debounce:** controlled by `LeapNoMatchFeedback` constant (two modes):

**Mode A — `LeapFeedback.TaskPanel` (auto-trim):**
- Buffer is auto-trimmed back to `lastValidBuffer` (last state that produced ≥ 1 match).
- Status bar area briefly shows what was removed, e.g. `Leap: no match — removed "x"`.
- Column continues showing the last valid filter result; no empty-list flash.
- User does not need to backspace manually.

**Mode B — `LeapFeedback.Inline` (keep input, show inline):**
- Buffer is not trimmed. Invalid input stays in the buffer.
- LEAP bar appends ` (no match)` after the query in a dimmed mid-gray color.
- No task panel message.
- User backtracks with Backspace when ready.

```
Mode B LEAP bar appearance:
LEAP  mapm/tex (no match)
      ^^^^^^^^ ^^^^^^^^^^^^
      yellow   dim mid-gray
```

Both modes are shippable. Intent is to A/B test in real use.

**Migemo fallback:** if `MigemoProvider.IsAvailable` is false or produces no matches for a segment, the result set is simply the prefix+substring matches. Migemo absence is never an error — it degrades silently. (This already matches how `SearchHelper` handles Migemo today.)

### AND segments (space as separator)

A space in the local filter splits the query into multiple segments, all of which must match (AND logic):

```
"map tex"  →  matches entries containing both "map" AND "tex"
```

Matching is **order-independent**: each segment is checked as a substring anywhere in the filename, independently. Both `"tex2map"` and `"map2tex"` match `"map tex"`.

Only **space** is the segment separator — `_`, `-`, `.` are treated as literal characters and matched normally.

To search for a literal space in a filename, escape it with a backslash: `\ ` (backslash-space).

An empty trailing segment (e.g. `"map "` after typing space but before the next word) is ignored — treated the same as `"map"`.

### Match priority

Results from all three tiers are unioned — all matching entries from all tiers are shown. The cursor is positioned on the highest-priority match:

1. Prefix match (cursor goes here first)
2. Substring match
3. Migemo match (romaji → Japanese, if `MigemoProvider.IsAvailable`; cursor falls here only if no prefix/substring match exists)

---

## Navigation Keys in Leap Mode

| Key | Behavior |
|-----|----------|
| `→` (Right) | **Directory**: enter, append `/` to buffer (calls same logic as `EnterAsync` but restricted to active column). **File**: pin cursor to file, exit leap mode without opening. |
| `←` (Left) | Go to parent directory. Strips local filter + `/` from buffer in one action (e.g. `mapm/we` → `mapm`). Equivalent to `Parent()` but with buffer bookkeeping. |
| `↑` / `↓` | Move cursor among **visible** (filtered) entries only. |
| `Ctrl+P` / `Ctrl+N` | Same as `↑` / `↓` (Emacs/readline style). |
| `Enter` | **Directory**: same as `→`. **File**: open file via `OpenFileAsync()`, exit leap mode. |
| `Esc` | Cancel leap. Cursor and directory restored to pre-leap state (same pattern as `ExitSearchMode(restoreCursor: true)`). |
| `F3` | Exit leap. Cursor stays on current item (same pattern as `ExitSearchMode(restoreCursor: false)`). |
| `Ctrl+U` | Clear local filter (everything after last `/`). Stay in current directory; all entries shown. |
| `Ctrl+K` | Clear entire buffer. Return to leap-root directory (where `F3` was pressed). All entries shown unfiltered. |

---

## Single Match Behavior

| Match type | Result |
|------------|--------|
| 1 directory | **Auto-enter**: navigate into it, append `/` to buffer. |
| 1 file | **Select only**: cursor pins to file. No auto-open. User presses `Enter` to open or `→` to select without opening. |

---

## Visual Design — LEAP Bar

### Position

The LEAP bar **replaces the global status bar** (`frame[height - 1]`) at the bottom of the screen while leap mode is active. This is the same slot that `BuildSearchStatusBar` currently occupies for search mode.

> **Why not per-column**: This app has a single global status bar (not per-pane summary lines like rwf). The LEAP bar occupies the same space, consistent with how search mode renders.

### Layout anatomy

```
┌─ LEAP ─┬─◂?─┬──────── scrollable ────────┬── right anchor ──┐
  LEAP    ◂    trail/sep/local filter▌        N matches
└────────┴────┴────────────────────────────┴──────────────────┘
```

- **`LEAP` label** — fixed left, always visible
- **`◂`** — scroll indicator, shown only when trail has scrolled off the left edge
- **Scrollable zone** — trail + `/` separators + local filter + cursor block; scrolls horizontally so the cursor is always visible
- **Right anchor** — match count or `(no match)` in Mode B; space always reserved

### Scroll behavior

The cursor is always anchored at the right of the scrollable zone. As the buffer grows deep (e.g. `mapm/ci/te/obj/bui`), the trail scrolls off the left. The `◂` indicator appears when content is hidden left. On Backspace the trail scrolls back into view naturally.

Use an `int leapBarScrollOffset` within `LeapState` to track how far the trail is scrolled left, updated whenever the buffer changes.

### Colors — ANSI codes

| Element | Color | ANSI |
|---------|-------|------|
| `LEAP` label | red/pink | `\e[91m` (bright red) |
| Trail (already-navigated path) | dim gray | `\e[38;5;59m` or `\e[2;37m` |
| `/` depth separator | mid gray | `\e[38;5;66m` |
| Local filter (active query) | yellow | `\e[93m` (bright yellow, matches existing search query color) |
| Cursor block `▌` | yellow | same as local filter |
| `(no match)` (Mode B) | mid-gray — readable but not alarming | `\e[38;5;102m` |
| Match count (`N matches`) | green | `\e[92m` (bright green) |
| `1 match · dir` | green | `\e[92m` |
| `1 match · file` | green | `\e[92m` |

Reuse `AnsiColors` or the existing ANSI string building patterns already in `Program.cs` (e.g. `\x1b[...m` sequences).

### Right anchor examples

```
LEAP  ◂mapm/bui▌                  2 matches   ← normal
LEAP  ◂mapm/xyz▌               (no match)   ← Mode B, zero-match
LEAP  ◂mapm/src▌           1 match · dir   ← auto-enter firing
```

---

## Architecture Notes

### New class: `LeapState`

Parallel to `SearchState` (line 457 in `Program.cs`):

```csharp
sealed class LeapState
{
    public bool Active;
    public string Buffer = "";          // full buffer including "/" separators
    public string LastValidBuffer = ""; // last buffer that produced ≥ 1 match (rollback)
    public string RootDir = "";         // directory where F3 was pressed (for Ctrl+K)
    public int RootCursorIndex;         // column.Selected before F3 (for Esc restore)
    // (directory, buffer.Length at time of entry) for backtrack via Backspace
    public List<(string Dir, int BufferLen)> DirStack = new();
    public bool NeedsRecompute;         // set by keystroke, consumed by debounce tick
    public DateTime LastInputTime;
    public int BarScrollOffset;         // how far the trail is scrolled left in LEAP bar
}
```

Add `public LeapState Leap = new();` to `ScreenState` alongside `Search`.

### Main loop integration

In the main input loop (around line 1621), add a leap-mode branch before the search check:

```csharp
if (State.Leap.Active)
{
    await HandleLeapKeyAsync(key);
    continue;
}
if (State.Search.Active)
{
    await HandleSearchKeyAsync(key);
    continue;
}
```

Also add a debounce tick check for leap alongside the existing search debounce (around line 1621):

```csharp
if (State.Leap.Active && State.Leap.NeedsRecompute
    && (DateTime.UtcNow - State.Leap.LastInputTime).TotalMilliseconds >= NavigationDebounceMs)
{
    await ApplyLeapFilterAsync();
}
```

### Key binding

In `HandleSpecialKeyAsync`, add F3 handling (new case, currently unbound):

```csharp
case ConsoleKey.F3:
    EnterLeapMode();
    break;
```

### `EnterLeapMode` / `ExitLeapMode`

Follow the exact pattern of `EnterSearchMode` / `ExitSearchMode`:

```csharp
static void EnterLeapMode()
{
    Column col = Columns[State.ActiveColumn];
    State.Leap.Active = true;
    State.Leap.Buffer = "";
    State.Leap.LastValidBuffer = "";
    State.Leap.RootDir = col.Path;
    State.Leap.RootCursorIndex = col.Selected;
    State.Leap.DirStack.Clear();
    State.Leap.NeedsRecompute = false;
    State.Leap.BarScrollOffset = 0;
}

static void ExitLeapMode(bool restoreCursor)
{
    State.Leap.Active = false;
    if (restoreCursor)
    {
        // navigate back to root dir and restore cursor
        Column col = Columns[State.ActiveColumn];
        col.Path = State.Leap.RootDir;
        // reload entries + restore cursor index
        col.Selected = State.Leap.RootCursorIndex;
    }
}
```

### `HandleLeapKeyAsync`

New method, parallel to `HandleSearchKeyAsync` (line 2684). Routes:

- `F3` → `ExitLeapMode(restoreCursor: false)` + rebuild
- `Esc` → `ExitLeapMode(restoreCursor: true)` + rebuild
- `Enter` → if current entry is dir: enter directory; else `OpenFileAsync()` + `ExitLeapMode(false)`
- `→` → enter directory (append `/`) or select file + exit
- `←` → strip local filter + `/` from buffer, go to parent
- `↑`/`Ctrl+P` → move cursor up among visible entries
- `↓`/`Ctrl+N` → move cursor down among visible entries
- `Ctrl+U` → clear local filter (keep trail)
- `Ctrl+K` → clear buffer, return to root dir
- `Backspace` → remove one char from buffer; if removed char was `/`, exit directory
- Printable chars → append to buffer, set `NeedsRecompute = true`, `LastInputTime = DateTime.UtcNow`

### Filtering in `DrawColumnToLines`

In `DrawColumnToLines` (line 3033), add a leap-filter branch parallel to the existing `inSearch` path (line 3076):

```csharp
bool inLeap = active && State.Leap.Active;
List<int> leapVisibleIndices = new List<int>();
if (inLeap)
{
    // build leapVisibleIndices from column.Entries filtered by local filter
    // (prefix + substring + migemo, same as search but hiding non-matches)
}
```

Render only entries in `leapVisibleIndices`. The cursor `column.Selected` references the actual `column.Entries` index, so map through `leapVisibleIndices` for display row calculation.

### Background filtering (`ApplyLeapFilterAsync`)

New method, parallel to the existing async search worker (around line 2651). Runs after debounce:

1. Extract local filter = `Buffer` after last `/`
2. Build match list: prefix matches + substring matches + Migemo matches
3. If 0 matches:
   - Mode A: trim `Buffer` to `LastValidBuffer`, update UI
   - Mode B: set a flag `NoMatch = true`, show `(no match)` in LEAP bar
4. If ≥ 1 match: `LastValidBuffer = Buffer`; set `column.Selected` to first match

Reuse `MigemoProvider` (already instantiated as `_migemo` in the program).

### Status bar rendering

In the status bar section of `BuildFrame` (around line 2999):

```csharp
if (State.Leap.Active)
{
    string leapBar = BuildLeapStatusBar(State.Leap, matchCount, width);
    frame[height - 1] = leapBar;
}
else if (State.Search.Active)
{
    // existing search bar logic
}
```

`BuildLeapStatusBar` is a new method, parallel to `BuildSearchStatusBar` (line 600).

---

## Constants to Add

Since the app has no config files, configuration is handled via constants. Add near the top of `Program.cs` alongside existing constants:

```csharp
// Leap navigation
const int LeapDebounceMs = 300;           // reuse NavigationDebounceMs or set separately
enum LeapFeedback { TaskPanel, Inline }
const LeapFeedback LeapNoMatchFeedback = LeapFeedback.TaskPanel;  // Mode A default
```

---

## Implementation Notes vs. rwf Spec

| rwf concept | Console column file manager equivalent |
|-------------|---------------------------------------|
| `UIMode::Leap` | `State.Leap.Active` boolean (parallel to `State.Search.Active`) |
| `PaneModel.apply_current_filter()` | `leapVisibleIndices` list computed in `DrawColumnToLines` |
| Per-pane summary line | Global status bar (`frame[height - 1]`) — LEAP bar goes here |
| Job system (Rust) | `Task` + `async/await`, `CancellationTokenSource` (same as existing search) |
| `rwf-lib/src/model/ui.rs` | `Program.cs` (monolithic), `ScreenState` class |
| `rwf-lib/src/job/` | Inline `Task.Run` blocks in `Program.cs` |
| `LeapState` struct | `sealed class LeapState` in `Program.cs` |
| `SearchDebounceMs: 150` | `NavigationDebounceMs = 300` (existing constant, reuse) |
| `MigemoEnabled` config | `_migemo.IsAvailable` (already runtime-detected) |
| Multiple panes | Multiple `Column` objects; leap applies to `Columns[State.ActiveColumn]` |

---

## Verification Plan

1. **Basic filter**: enter leap with F3, type a prefix — only matching entries visible in active column, others hidden.
2. **Zero-match (Mode A / TaskPanel)**: type chars that produce no match — after 300ms debounce, buffer reverts to `LastValidBuffer`, status bar shows what was removed briefly.
3. **Zero-match (Mode B / Inline)**: same scenario with `LeapNoMatchFeedback = Inline` — buffer keeps invalid input, LEAP bar right anchor shows `(no match)` in dim color.
4. **Auto-enter**: type until 1 dir match → automatic entry, buffer shows `trail/`, local filter empty, right-side columns rebuild via `RebuildRightSideAsync`.
5. **Backspace unwinds**: ⌫ through local filter chars, then through `/` — exits directory, parent re-filtered.
6. **File single match**: narrow to 1 file — cursor pins, no auto-open. Enter calls `OpenFileAsync()`; `→` selects only.
7. **Left arrow**: in leap mode with local filter, press `←` — goes to parent, local+sep stripped from buffer.
8. **Ctrl+U**: clears local filter, all entries of current dir shown, buffer ends with `/`.
9. **Ctrl+K**: returns to `RootDir`, buffer empty, all entries shown.
10. **Esc**: cancel — cursor and directory restored to pre-F3 state.
11. **F3 toggle**: exit with F3 — cursor stays on current filtered item, normal mode restored.
12. **LEAP bar scroll**: type a deep path (4+ levels) — `◂` indicator appears, cursor always visible, trail scrolls off left; Backspace scrolls trail back into view. `BarScrollOffset` in `LeapState` drives this.
13. **50k entries**: measure filter response time with 300ms debounce; no UI block during typing.
14. **AND segments**: type `"map tex"` — only entries matching both appear; order-independent.
15. **Migemo** (if `_migemo.IsAvailable`): type romaji, Japanese filename matches appear after debounce settles alongside any ASCII matches.
16. **Search mode coexistence**: verify that entering leap mode while search mode is inactive works, and that neither mode can be active simultaneously.
17. **Preview pane**: verify that `StartPreviewLoad()` is called after exiting leap mode (same as after exiting search mode, line 2694).
