# Quick Search & Navigation Keys — Design Spec
Date: 2026-06-14

## Overview

Two independent additions to the console column file manager:

1. **Navigation keys** — page scroll and jump-to-top/bottom shortcuts
2. **Incremental search** — `/`-triggered in-column search with migemo support and optional regex mode

---

## Section 1: Navigation Keys

Six new bindings added to `HandleSpecialKeyAsync`, all acting on `Columns[State.ActiveColumn]`.

| Key(s) | Action |
|---|---|
| `PageUp`, `Ctrl+B` | Scroll up by `visibleHeight` rows, clamp to index 0 |
| `PageDown`, `Ctrl+F` | Scroll down by `visibleHeight` rows, clamp to last entry |
| `Home`, `g` | Jump to index 0 |
| `End`, `G` (Shift+G) | Jump to last entry |

`visibleHeight` = `Console.WindowHeight - 3` (matches `DrawColumnToLines` budget of `frameHeight - 3`).

After any navigation: call `UpdateHorizontalScroll()`, `RebuildRightSideAsync(State.ActiveColumn)`, `StartPreviewLoad()` — same tail as `MoveUpAsync`/`MoveDownAsync`.

---

## Section 2: Search State Architecture

### SearchState class

```csharp
sealed class SearchState
{
    public bool Active;
    public string Query = "";
    public int Anchor;          // column.Selected saved on search entry; restored on Esc
    public List<int> Matches = new();
    public int MatchIndex;      // index into Matches
    public bool RegexMode;      // Ctrl+R toggles; false = literal/migemo, true = raw regex
    public CancellationTokenSource? SearchCts;
    public bool NeedsRecompute; // set on keystroke, cleared when recompute fires
    public DateTime LastInputTime;
}
```

`ScreenState` gains one field:
```csharp
public SearchState Search = new();
```

### MigemoProvider

Static nullable field initialized once at startup:
```csharp
static MigemoProvider? _migemo;
```

Initialized in `Main` before entering the alternate screen buffer:
```csharp
_migemo = new MigemoProvider();
if (!_migemo.IsAvailable)
{
    if (_migemo.DllLoaded)   // DLL found but dict missing
        Console.WriteLine("migemo: dict not found — plain search active");
    _migemo = null;
}
```

`MigemoProvider` exposes two booleans:
- `DllLoaded` — set to `true` once `migemo_open` is successfully called (DLL present); `false` if P/Invoke throws (DLL absent)
- `IsAvailable` — `true` only if DLL loaded AND `migemo_open` returned a non-null handle (dict found)

If DLL is absent (P/Invoke throws on first call): `DllLoaded = false`, `_migemo = null`, no output.
If DLL loaded but no dict found: print one line to stdout **before** `\x1b[?1049h` so it appears in terminal scrollback after quit.
If fully available: no output.

### Dict lookup order (first hit wins)

1. `<exe-dir>/dict/migemo-dict`
2. `%APPDATA%\ColumnFileManager\dict\migemo-dict`
3. `/usr/share/cmigemo/utf-8/migemo-dict`
4. `/usr/local/share/migemo/utf-8/migemo-dict`
5. `/opt/homebrew/share/migemo/utf-8/migemo-dict`

### MigemoProvider implementation

Ported from twf's `MigemoProvider`. Changes from twf:
- No DI / logging
- No `IMigemoProvider` interface
- No `RegexOptions.Compiled` (not AOT-safe; use plain `new Regex(pattern, RegexOptions.IgnoreCase)`)

P/Invoke declarations (NativeAOT-compatible):
```csharp
[DllImport("migemo", CharSet=CharSet.Ansi, CallingConvention=CallingConvention.Cdecl)]
static extern IntPtr migemo_open(string dict_path);
[DllImport("migemo", CallingConvention=CallingConvention.Cdecl)]
static extern void migemo_close(IntPtr h);
[DllImport("migemo", CharSet=CharSet.Ansi, CallingConvention=CallingConvention.Cdecl)]
static extern IntPtr migemo_query(IntPtr h, string query);
[DllImport("migemo", CallingConvention=CallingConvention.Cdecl)]
static extern void migemo_release(IntPtr h, IntPtr result);
```

---

## Section 3: Search Mode Behaviour

### Entering / exiting search

The main input loop checks `State.Search.Active` first:

```
if (State.Search.Active) → HandleSearchKeyAsync(key)
else                      → HandleSpecialKeyAsync(key)   // existing path
```

**Enter search** (`/` in normal mode):
- Set `State.Search.Active = true`
- Save `State.Search.Anchor = column.Selected`
- Clear `Query`, `Matches`, `MatchIndex`, `RegexMode = false`
- Fire immediate recompute (empty query → no matches, cursor stays at anchor)

### Key dispatch in HandleSearchKeyAsync

| Key | Action |
|---|---|
| Printable char | Append to `Query`; debounce recompute |
| `Backspace` | Remove last char; debounce recompute |
| `Ctrl+R` | Toggle `RegexMode`; debounce recompute (query unchanged) |
| `DownArrow`, `Ctrl+N` | Advance `MatchIndex` (wrap); move cursor to `Matches[MatchIndex]` |
| `UpArrow`, `Ctrl+P` | Decrement `MatchIndex` (wrap); move cursor to `Matches[MatchIndex]` |
| `Enter` | Exit search mode; leave cursor at current match |
| `Esc` | Exit search mode; restore `column.Selected = State.Search.Anchor`; cancel search task |
| Everything else | Swallowed (not passed to normal handler) |

Ctrl+N/P cycle wraps: at last match, Ctrl+N goes to first; at first, Ctrl+P goes to last.

### Debounce

On each search keystroke: set `State.Search.NeedsRecompute = true`, record `State.Search.LastInputTime = DateTime.UtcNow`. Do **not** call `RecomputeMatches` immediately.

In the main loop tick (50ms poll, when no key pending):
```
if (State.Search.Active
    && State.Search.NeedsRecompute
    && (now - State.Search.LastInputTime).TotalMilliseconds >= 300)
    → RecomputeMatches()
```

This debounce applies to all search modes (literal, migemo, regex). Esc/Enter flush cancellation immediately without waiting.

### Async match computation (RecomputeMatches)

1. Cancel and dispose any previous `State.Search.SearchCts`
2. Create new `CancellationTokenSource`, store in `State.Search.SearchCts`
3. Clear `State.Search.Matches`, reset `State.Search.MatchIndex = 0`
4. Launch `Task.Run` that iterates `column.Entries`:
   - **Regex mode**: compile `new Regex(query, IgnoreCase)`; if pattern invalid, yield no matches
   - **Literal mode + migemo available**: call `migemo_query(handle, query)` to get regex string; compile and use regex; fall back to `Contains` if regex invalid
   - **Literal mode + no migemo**: `entry.Contains(query, OrdinalIgnoreCase)`
   - On each hit: lock and append index to `State.Search.Matches`
   - Check `CancellationToken` every entry; exit early if cancelled
5. On completion: determine `MatchIndex` — first match at index ≥ `Anchor`; if none, use index 0 (first match in list); move `column.Selected` to `Matches[MatchIndex]`
6. Clear `NeedsRecompute`

### Status bar in search mode

Replaces normal status string in `BuildFrame`:

```
/query▌                      (1/3)    ← literal mode, search done
/query▌                     (1/3*)    ← literal mode, still scanning
/^rep.*▌            [regex]  (1/3)    ← regex mode, done
/^([▌               [regex]  (0)      ← regex mode, invalid pattern
```

- `/` prompt char: ANSI 33 (yellow)
- `[regex]` tag: ANSI 33 (yellow), shown only in regex mode
- Match count `(n/m)`: ANSI 90 (dark gray)
- `*` suffix on total while scanning: ANSI 33 (yellow)
- No-match `(0)`: ANSI 31 (red); prompt char also turns red
- Count pinned to right edge — does not shift as query grows

### Match highlighting in DrawColumnToLines

Active column in search mode:

- **Other matches** (`Matches` contains this index, not current): ANSI 32 (green fg) on default bg
- **Current match** (`Matches[MatchIndex]`): ANSI 42 (green bg) + ANSI 30 (black text) — overrides normal selection rendering
- Normal selection colours (`\x1b[47m` / `\x1b[100m`) still apply to non-match selected entry when there are no matches

---

## Section 4: Migemo Integration Details

### Match logic summary

| Mode | `_migemo` | Behaviour |
|---|---|---|
| Literal | null | `entry.Contains(query, OrdinalIgnoreCase)` |
| Literal | available | `migemo_query` → regex → `Regex.IsMatch`; fallback to `Contains` on regex error |
| Regex | any | `new Regex(query, IgnoreCase).IsMatch`; no migemo expansion |

### AOT compatibility

- P/Invoke: fully supported in NativeAOT
- `new Regex(pattern, IgnoreCase)` without `Compiled`: supported in NativeAOT
- `RegexOptions.Compiled`: NOT used (runtime code-gen, not AOT-safe)
- No reflection, no DI, no `AppDomain` usage beyond `BaseDirectory` for path lookup

---

## Out of scope

- Persisting search state per column across left/right navigation
- Regex syntax help or error display beyond showing `(0)`
- Highlighting the matched substring within an entry name
