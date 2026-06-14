# Quick Search & Navigation Keys Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add page/jump navigation keys and `/`-triggered incremental search with regex mode and optional migemo (Japanese romaji) support to the console column file manager.

**Architecture:** All changes live in `Program.cs` (single-file codebase). New classes `SearchState` and `MigemoProvider` are added inline. Key routing branches on `State.Search.Active` before the existing `HandleSpecialKeyAsync` switch. Async match computation with debounce and cancellation mirrors the existing preview-loading pattern.

**Tech Stack:** .NET 8, xUnit, P/Invoke to `cmigemo` native library (optional), `System.Text.RegularExpressions.Regex`

---

## File Map

| File | Change |
|---|---|
| `Program.cs` | Add `SearchState`, `MigemoProvider` classes; extend `ScreenState`; add nav keys, search key handler, match computation, status bar, highlighting |
| `ColumnFileManager.Tests/NavigationTests.cs` | New — page scroll and jump logic |
| `ColumnFileManager.Tests/SearchMatchTests.cs` | New — `MatchesSearchQuery`, `FindNearestMatchIndex` |
| `ColumnFileManager.Tests/SearchStatusBarTests.cs` | New — `BuildSearchStatusBar` output |

---

## Task 1: Add `SearchState` class and `_searchLock` to `Program.cs`

**Files:**
- Modify: `Program.cs` (after `ScreenState` class, around line 432)

- [ ] **Step 1: Insert `SearchState` class after `ScreenState`**

Add immediately after the closing `}` of `ScreenState`:

```csharp
sealed class SearchState
{
    public bool Active;
    public string Query = "";
    public int Anchor;           // column.Selected when search started; restored on Esc
    public List<int> Matches = new();
    public int MatchIndex;       // index into Matches of current highlighted match
    public bool RegexMode;       // false = literal/migemo, true = raw regex
    public CancellationTokenSource? SearchCts;
    public bool NeedsRecompute;  // set by keystroke, consumed by debounce tick
    public DateTime LastInputTime;
    public bool SearchDone;      // false while async scan in progress
}
```

- [ ] **Step 2: Add `Search` field to `ScreenState`**

In `ScreenState`, add after `public readonly PreviewPane Preview = new();`:

```csharp
public SearchState Search = new();
```

- [ ] **Step 3: Add `_searchLock` static field**

In the top-level static fields area (near `_lastErrorMessage`, around line 1440), add:

```csharp
static readonly object _searchLock = new object();
```

- [ ] **Step 4: Build and verify no errors**

```
dotnet build
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```
git add Program.cs
git commit -m "feat: add SearchState class and _searchLock"
```

---

## Task 2: Add `MigemoProvider` class to `Program.cs`

**Files:**
- Modify: `Program.cs` (add class after `SearchState`)

- [ ] **Step 1: Add `MigemoProvider` after `SearchState`**

```csharp
sealed class MigemoProvider : IDisposable
{
    [System.Runtime.InteropServices.DllImport("migemo",
        CharSet = System.Runtime.InteropServices.CharSet.Ansi,
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern IntPtr migemo_open(string dict_path);

    [System.Runtime.InteropServices.DllImport("migemo",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern void migemo_close(IntPtr h);

    [System.Runtime.InteropServices.DllImport("migemo",
        CharSet = System.Runtime.InteropServices.CharSet.Ansi,
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern IntPtr migemo_query(IntPtr h, string query);

    [System.Runtime.InteropServices.DllImport("migemo",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern void migemo_release(IntPtr h, IntPtr result);

    IntPtr _handle = IntPtr.Zero;
    bool _disposed;

    public bool DllLoaded { get; private set; }   // true if migemo.dll was found
    public bool IsAvailable { get; private set; } // true if DLL + dict both loaded

    public MigemoProvider()
    {
        try
        {
            string? dictFile = FindDictFile();
            if (dictFile == null) return;
            DllLoaded = true;
            _handle = migemo_open(dictFile);
            IsAvailable = _handle != IntPtr.Zero;
        }
        catch
        {
            // DLL absent or failed to load — silent
        }
    }

    static string? FindDictFile()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "dict", "migemo-dict"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ColumnFileManager", "dict", "migemo-dict"),
            "/usr/share/cmigemo/utf-8/migemo-dict",
            "/usr/local/share/migemo/utf-8/migemo-dict",
            "/opt/homebrew/share/migemo/utf-8/migemo-dict",
        };
        foreach (var path in candidates)
            if (File.Exists(path)) return path;
        return null;
    }

    public string ExpandPattern(string romaji)
    {
        if (!IsAvailable || string.IsNullOrEmpty(romaji)) return romaji;
        try
        {
            IntPtr ptr = migemo_query(_handle, romaji);
            if (ptr == IntPtr.Zero) return romaji;
            string? result = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr);
            migemo_release(_handle, ptr);
            return result ?? romaji;
        }
        catch { return romaji; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            try { migemo_close(_handle); } catch { }
            _handle = IntPtr.Zero;
        }
    }
}
```

- [ ] **Step 2: Add static `_migemo` field**

Near `_searchLock`:

```csharp
static MigemoProvider? _migemo;
```

- [ ] **Step 3: Build**

```
dotnet build
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```
git add Program.cs
git commit -m "feat: add MigemoProvider with P/Invoke to cmigemo"
```

---

## Task 3: Navigation keys (PageUp/PgDn, Home/End, g/G)

**Files:**
- Modify: `Program.cs` — `HandleSpecialKeyAsync` and new `NavigatePage` helper
- Create: `ColumnFileManager.Tests/NavigationTests.cs`

- [ ] **Step 1: Write failing tests**

Create `ColumnFileManager.Tests/NavigationTests.cs`:

```csharp
using Xunit;

public class NavigationTests
{
    static Column MakeColumn(int entryCount, int selected = 0, int scrollOffset = 0)
    {
        var c = new Column("test");
        for (int i = 0; i < entryCount; i++)
            c.Entries.Add($"file{i:D3}.txt");
        c.Selected = selected;
        c.ScrollOffset = scrollOffset;
        return c;
    }

    [Fact]
    public void NavigatePage_Down_AdvancesByVisibleHeight()
    {
        var c = MakeColumn(50, selected: 0);
        NavigationHelper.PageDown(c, visibleHeight: 10);
        Assert.Equal(10, c.Selected);
    }

    [Fact]
    public void NavigatePage_Down_ClampsAtLastEntry()
    {
        var c = MakeColumn(10, selected: 5);
        NavigationHelper.PageDown(c, visibleHeight: 20);
        Assert.Equal(9, c.Selected);
    }

    [Fact]
    public void NavigatePage_Up_DecrementsByVisibleHeight()
    {
        var c = MakeColumn(50, selected: 30);
        NavigationHelper.PageUp(c, visibleHeight: 10);
        Assert.Equal(20, c.Selected);
    }

    [Fact]
    public void NavigatePage_Up_ClampsAtZero()
    {
        var c = MakeColumn(50, selected: 3);
        NavigationHelper.PageUp(c, visibleHeight: 10);
        Assert.Equal(0, c.Selected);
    }

    [Fact]
    public void NavigatePage_Home_JumpsToFirst()
    {
        var c = MakeColumn(50, selected: 25);
        NavigationHelper.GoHome(c);
        Assert.Equal(0, c.Selected);
        Assert.Equal(0, c.ScrollOffset);
    }

    [Fact]
    public void NavigatePage_End_JumpsToLast()
    {
        var c = MakeColumn(50, selected: 0);
        NavigationHelper.GoEnd(c);
        Assert.Equal(49, c.Selected);
    }
}
```

- [ ] **Step 2: Run — confirm FAIL**

```
dotnet test ColumnFileManager.Tests --filter NavigationTests
```
Expected: fails with "NavigationHelper not found".

- [ ] **Step 3: Add `NavigationHelper` static class and wire keys in `Program.cs`**

Add `NavigationHelper` in `Program.cs` (near other helper classes):

```csharp
static class NavigationHelper
{
    public static void PageDown(Column c, int visibleHeight)
    {
        c.Selected = Math.Min(c.Selected + visibleHeight, c.Entries.Count - 1);
    }

    public static void PageUp(Column c, int visibleHeight)
    {
        c.Selected = Math.Max(c.Selected - visibleHeight, 0);
    }

    public static void GoHome(Column c)
    {
        c.Selected = 0;
        c.ScrollOffset = 0;
    }

    public static void GoEnd(Column c)
    {
        c.Selected = Math.Max(0, c.Entries.Count - 1);
    }
}
```

In `HandleSpecialKeyAsync`, add these cases to the `switch (key.Key)`:

```csharp
case ConsoleKey.PageUp:
    NavigationHelper.PageUp(Columns[State.ActiveColumn], Console.WindowHeight - 3);
    UpdateHorizontalScroll();
    await RebuildRightSideAsync(State.ActiveColumn);
    if (State.Preview.IsVisible) StartPreviewLoad();
    break;

case ConsoleKey.PageDown:
    NavigationHelper.PageDown(Columns[State.ActiveColumn], Console.WindowHeight - 3);
    UpdateHorizontalScroll();
    await RebuildRightSideAsync(State.ActiveColumn);
    if (State.Preview.IsVisible) StartPreviewLoad();
    break;

case ConsoleKey.Home:
    NavigationHelper.GoHome(Columns[State.ActiveColumn]);
    UpdateHorizontalScroll();
    await RebuildRightSideAsync(State.ActiveColumn);
    if (State.Preview.IsVisible) StartPreviewLoad();
    break;

case ConsoleKey.End:
    NavigationHelper.GoEnd(Columns[State.ActiveColumn]);
    UpdateHorizontalScroll();
    await RebuildRightSideAsync(State.ActiveColumn);
    if (State.Preview.IsVisible) StartPreviewLoad();
    break;

case ConsoleKey.G:
    if (key.Modifiers == ConsoleModifiers.None)
    {
        // g → top
        NavigationHelper.GoHome(Columns[State.ActiveColumn]);
        UpdateHorizontalScroll();
        await RebuildRightSideAsync(State.ActiveColumn);
        if (State.Preview.IsVisible) StartPreviewLoad();
    }
    else if (key.Modifiers == ConsoleModifiers.Shift)
    {
        // G → bottom
        NavigationHelper.GoEnd(Columns[State.ActiveColumn]);
        UpdateHorizontalScroll();
        await RebuildRightSideAsync(State.ActiveColumn);
        if (State.Preview.IsVisible) StartPreviewLoad();
    }
    break;

case ConsoleKey.B:
    if (key.Modifiers == ConsoleModifiers.Control)
    {
        NavigationHelper.PageUp(Columns[State.ActiveColumn], Console.WindowHeight - 3);
        UpdateHorizontalScroll();
        await RebuildRightSideAsync(State.ActiveColumn);
        if (State.Preview.IsVisible) StartPreviewLoad();
    }
    break;

case ConsoleKey.F:
    if (key.Modifiers == ConsoleModifiers.Control)
    {
        NavigationHelper.PageDown(Columns[State.ActiveColumn], Console.WindowHeight - 3);
        UpdateHorizontalScroll();
        await RebuildRightSideAsync(State.ActiveColumn);
        if (State.Preview.IsVisible) StartPreviewLoad();
    }
    break;
```

- [ ] **Step 4: Run tests — confirm PASS**

```
dotnet test ColumnFileManager.Tests --filter NavigationTests
```
Expected: all 6 pass.

- [ ] **Step 5: Commit**

```
git add Program.cs ColumnFileManager.Tests/NavigationTests.cs
git commit -m "feat: add PageUp/Down, Home/End, g/G navigation keys"
```

---

## Task 4: `MatchesSearchQuery` and `FindNearestMatchIndex` helpers

**Files:**
- Modify: `Program.cs` — add two static helper methods
- Create: `ColumnFileManager.Tests/SearchMatchTests.cs`

- [ ] **Step 1: Write failing tests**

Create `ColumnFileManager.Tests/SearchMatchTests.cs`:

```csharp
using Xunit;

public class SearchMatchTests
{
    // ── MatchesSearchQuery ──────────────────────────────────────────────

    [Fact]
    public void Match_LiteralSubstring_CaseInsensitive()
    {
        Assert.True(SearchHelper.MatchesSearchQuery("Report_2025.pdf", "report", false, null));
    }

    [Fact]
    public void Match_LiteralNoMatch()
    {
        Assert.False(SearchHelper.MatchesSearchQuery("budget.xlsx", "report", false, null));
    }

    [Fact]
    public void Match_EmptyQuery_ReturnsFalse()
    {
        Assert.False(SearchHelper.MatchesSearchQuery("anything.txt", "", false, null));
    }

    [Fact]
    public void Match_RegexMode_ValidPattern()
    {
        Assert.True(SearchHelper.MatchesSearchQuery("report_2025.pdf", @"^rep.*\.pdf$", true, null));
    }

    [Fact]
    public void Match_RegexMode_InvalidPattern_ReturnsFalse()
    {
        // unclosed group — must not throw
        Assert.False(SearchHelper.MatchesSearchQuery("anything.txt", @"^([", true, null));
    }

    [Fact]
    public void Match_RegexMode_CaseInsensitive()
    {
        Assert.True(SearchHelper.MatchesSearchQuery("REPORT.pdf", "report", true, null));
    }

    // ── FindNearestMatchIndex ───────────────────────────────────────────

    [Fact]
    public void Nearest_PicksFirstAtOrAfterAnchor()
    {
        var matches = new List<int> { 2, 7, 11 };
        Assert.Equal(1, SearchHelper.FindNearestMatchIndex(matches, anchor: 5));
    }

    [Fact]
    public void Nearest_WrapsToBeginnningWhenNoMatchAfterAnchor()
    {
        var matches = new List<int> { 2, 7 };
        Assert.Equal(0, SearchHelper.FindNearestMatchIndex(matches, anchor: 8));
    }

    [Fact]
    public void Nearest_EmptyMatches_ReturnsZero()
    {
        Assert.Equal(0, SearchHelper.FindNearestMatchIndex(new List<int>(), anchor: 3));
    }

    [Fact]
    public void Nearest_ExactAnchorMatch()
    {
        var matches = new List<int> { 5, 10 };
        Assert.Equal(0, SearchHelper.FindNearestMatchIndex(matches, anchor: 5));
    }
}
```

- [ ] **Step 2: Run — confirm FAIL**

```
dotnet test ColumnFileManager.Tests --filter SearchMatchTests
```
Expected: fails with "SearchHelper not found".

- [ ] **Step 3: Add `SearchHelper` static class to `Program.cs`**

```csharp
static class SearchHelper
{
    public static bool MatchesSearchQuery(string entry, string query, bool regexMode, MigemoProvider? migemo)
    {
        if (string.IsNullOrEmpty(query)) return false;

        if (regexMode)
        {
            try { return System.Text.RegularExpressions.Regex.IsMatch(entry, query, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
            catch (System.Text.RegularExpressions.RegexParseException) { return false; }
        }

        if (migemo?.IsAvailable == true)
        {
            string pattern = migemo.ExpandPattern(query);
            try { return System.Text.RegularExpressions.Regex.IsMatch(entry, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
            catch { return entry.Contains(query, StringComparison.OrdinalIgnoreCase); }
        }

        return entry.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public static int FindNearestMatchIndex(List<int> matches, int anchor)
    {
        for (int i = 0; i < matches.Count; i++)
            if (matches[i] >= anchor) return i;
        return 0;
    }
}
```

- [ ] **Step 4: Run — confirm PASS**

```
dotnet test ColumnFileManager.Tests --filter SearchMatchTests
```
Expected: all 10 pass.

- [ ] **Step 5: Commit**

```
git add Program.cs ColumnFileManager.Tests/SearchMatchTests.cs
git commit -m "feat: add SearchHelper with MatchesSearchQuery and FindNearestMatchIndex"
```

---

## Task 5: `RecomputeMatchesAsync` — async match scan with cancellation

**Files:**
- Modify: `Program.cs` — add `RecomputeMatchesAsync` static method

- [ ] **Step 1: Add `RecomputeMatchesAsync` to `Program.cs`**

```csharp
static async Task RecomputeMatchesAsync()
{
    // Cancel any in-progress scan
    State.Search.SearchCts?.Cancel();
    State.Search.SearchCts?.Dispose();
    var cts = new CancellationTokenSource();
    State.Search.SearchCts = cts;

    string query = State.Search.Query;
    bool regexMode = State.Search.RegexMode;
    int anchor = State.Search.Anchor;
    Column col = Columns[State.ActiveColumn];

    lock (_searchLock)
    {
        State.Search.Matches.Clear();
        State.Search.SearchDone = false;
        State.Search.NeedsRecompute = false;
    }

    await Task.Run(() =>
    {
        for (int i = 0; i < col.Entries.Count; i++)
        {
            if (cts.Token.IsCancellationRequested) return;
            string name = col.Entries[i].TrimEnd('/');
            if (SearchHelper.MatchesSearchQuery(name, query, regexMode, _migemo))
            {
                lock (_searchLock) { State.Search.Matches.Add(i); }
            }
        }

        if (cts.Token.IsCancellationRequested) return;

        lock (_searchLock)
        {
            State.Search.MatchIndex = SearchHelper.FindNearestMatchIndex(State.Search.Matches, anchor);
            if (State.Search.Matches.Count > 0)
                col.Selected = State.Search.Matches[State.Search.MatchIndex];
            State.Search.SearchDone = true;
        }
    }, cts.Token);
}
```

- [ ] **Step 2: Build**

```
dotnet build
```
Expected: 0 errors.

- [ ] **Step 3: Commit**

```
git add Program.cs
git commit -m "feat: add RecomputeMatchesAsync with async scan and cancellation"
```

---

## Task 6: Search entry, exit and key routing

**Files:**
- Modify: `Program.cs` — main loop key dispatch, `EnterSearchMode`, `ExitSearchMode`, `HandleSearchKeyAsync`

- [ ] **Step 1: Add `EnterSearchMode` and `ExitSearchMode`**

```csharp
static void EnterSearchMode()
{
    Column col = Columns[State.ActiveColumn];
    State.Search.Active = true;
    State.Search.Anchor = col.Selected;
    State.Search.Query = "";
    State.Search.RegexMode = false;
    lock (_searchLock)
    {
        State.Search.Matches.Clear();
        State.Search.SearchDone = true;
        State.Search.NeedsRecompute = false;
    }
}

static void ExitSearchMode(bool restoreCursor)
{
    State.Search.SearchCts?.Cancel();
    State.Search.SearchCts?.Dispose();
    State.Search.SearchCts = null;
    State.Search.Active = false;
    if (restoreCursor)
        Columns[State.ActiveColumn].Selected = State.Search.Anchor;
    lock (_searchLock)
    {
        State.Search.Matches.Clear();
        State.Search.SearchDone = true;
    }
}
```

- [ ] **Step 2: Add `HandleSearchKeyAsync`**

```csharp
static async Task HandleSearchKeyAsync(ConsoleKeyInfo key)
{
    SearchState s = State.Search;
    Column col = Columns[State.ActiveColumn];

    // Esc — exit search, restore cursor
    if (key.Key == ConsoleKey.Escape)
    {
        ExitSearchMode(restoreCursor: true);
        return;
    }

    // Enter — confirm, leave cursor at current match
    if (key.Key == ConsoleKey.Enter)
    {
        ExitSearchMode(restoreCursor: false);
        return;
    }

    // Ctrl+R — toggle regex mode, recompute
    if (key.Key == ConsoleKey.R && (key.Modifiers & ConsoleModifiers.Control) != 0)
    {
        s.RegexMode = !s.RegexMode;
        s.NeedsRecompute = true;
        s.LastInputTime = DateTime.UtcNow;
        return;
    }

    // Ctrl+N / DownArrow — next match
    if (key.Key == ConsoleKey.DownArrow ||
        (key.Key == ConsoleKey.N && (key.Modifiers & ConsoleModifiers.Control) != 0))
    {
        lock (_searchLock)
        {
            if (s.Matches.Count > 0)
            {
                s.MatchIndex = (s.MatchIndex + 1) % s.Matches.Count;
                col.Selected = s.Matches[s.MatchIndex];
                UpdateHorizontalScroll();
            }
        }
        return;
    }

    // Ctrl+P / UpArrow — previous match
    if (key.Key == ConsoleKey.UpArrow ||
        (key.Key == ConsoleKey.P && (key.Modifiers & ConsoleModifiers.Control) != 0))
    {
        lock (_searchLock)
        {
            if (s.Matches.Count > 0)
            {
                s.MatchIndex = (s.MatchIndex - 1 + s.Matches.Count) % s.Matches.Count;
                col.Selected = s.Matches[s.MatchIndex];
                UpdateHorizontalScroll();
            }
        }
        return;
    }

    // Backspace — delete last char
    if (key.Key == ConsoleKey.Backspace)
    {
        if (s.Query.Length > 0)
        {
            s.Query = s.Query[..^1];
            s.NeedsRecompute = true;
            s.LastInputTime = DateTime.UtcNow;
        }
        return;
    }

    // Printable char — append to query
    if (!char.IsControl(key.KeyChar))
    {
        s.Query += key.KeyChar;
        s.NeedsRecompute = true;
        s.LastInputTime = DateTime.UtcNow;
        return;
    }

    // Everything else is swallowed
}
```

- [ ] **Step 3: Wire key routing in the main loop**

In the main loop (around line 1484), the key-read block currently looks like:

```csharp
ConsoleKeyInfo key = Console.ReadKey(true);

switch (key.KeyChar)
{
    case (char)27: // Esc
        return;

    default:
        await HandleSpecialKeyAsync(key);
        break;
}
```

Replace it with:

```csharp
ConsoleKeyInfo key = Console.ReadKey(true);

if (State.Search.Active)
{
    await HandleSearchKeyAsync(key);
    continue;
}

switch (key.KeyChar)
{
    case (char)27: // Esc
        return;

    default:
        await HandleSpecialKeyAsync(key);
        break;
}
```

- [ ] **Step 4: Wire `/` key in `HandleSpecialKeyAsync`**

In the `switch (key.Key)` inside `HandleSpecialKeyAsync`, add a `default:` case (or extend existing one) to catch `/`:

```csharp
default:
    if (key.KeyChar == '/' && key.Modifiers == ConsoleModifiers.None)
        EnterSearchMode();
    break;
```

- [ ] **Step 5: Build**

```
dotnet build
```
Expected: 0 errors.

- [ ] **Step 6: Smoke test manually**

Run the app, press `/`, type a few characters, press Esc — cursor should return to original position. Press `/` again, press Enter — cursor stays.

```
dotnet run
```

- [ ] **Step 7: Commit**

```
git add Program.cs
git commit -m "feat: wire search entry/exit and key routing"
```

---

## Task 7: Debounce tick in main loop

**Files:**
- Modify: `Program.cs` — main loop no-key-available branch

- [ ] **Step 1: Add debounce check to the 50ms poll**

In the main loop, find the `if (!Console.KeyAvailable)` branch:

```csharp
if (!Console.KeyAvailable)
{
    System.Threading.Thread.Sleep(50);
    continue;
}
```

Replace with:

```csharp
if (!Console.KeyAvailable)
{
    if (State.Search.Active
        && State.Search.NeedsRecompute
        && (DateTime.UtcNow - State.Search.LastInputTime).TotalMilliseconds >= 300)
    {
        await RecomputeMatchesAsync();
    }
    System.Threading.Thread.Sleep(50);
    continue;
}
```

- [ ] **Step 2: Build and smoke test**

```
dotnet build && dotnet run
```

Press `/`, type `rep` slowly — after 300ms the cursor should jump to the first matching entry.

- [ ] **Step 3: Commit**

```
git add Program.cs
git commit -m "feat: add 300ms debounce for search recompute"
```

---

## Task 8: `BuildSearchStatusBar` helper

**Files:**
- Modify: `Program.cs` — add `BuildSearchStatusBar` to `SearchHelper`
- Create: `ColumnFileManager.Tests/SearchStatusBarTests.cs`

- [ ] **Step 1: Write failing tests**

Create `ColumnFileManager.Tests/SearchStatusBarTests.cs`:

```csharp
using Xunit;

public class SearchStatusBarTests
{
    static string Strip(string s)
    {
        // Remove all ANSI escape sequences for assertion
        return System.Text.RegularExpressions.Regex.Replace(s, @"\x1b\[[0-9;]*m", "");
    }

    [Fact]
    public void StatusBar_EmptyQuery_ShowsPromptOnly()
    {
        var s = new SearchState { Active = true, Query = "", SearchDone = true };
        string bar = SearchHelper.BuildSearchStatusBar(s, new System.Collections.Generic.List<int>(), done: true, width: 40);
        Assert.StartsWith("/ ", Strip(bar)); // '/' + cursor space
    }

    [Fact]
    public void StatusBar_MatchesDone_ShowsCount()
    {
        var s = new SearchState { Active = true, Query = "rep", MatchIndex = 0, SearchDone = true };
        var matches = new System.Collections.Generic.List<int> { 1, 3, 7 };
        string bar = SearchHelper.BuildSearchStatusBar(s, matches, done: true, width: 60);
        string plain = Strip(bar);
        Assert.Contains("(1/3)", plain);
        Assert.DoesNotContain("*", plain);
        Assert.DoesNotContain("[regex]", plain);
    }

    [Fact]
    public void StatusBar_Scanning_ShowsStar()
    {
        var s = new SearchState { Active = true, Query = "rep", MatchIndex = 0 };
        var matches = new System.Collections.Generic.List<int> { 1, 3 };
        string bar = SearchHelper.BuildSearchStatusBar(s, matches, done: false, width: 60);
        string plain = Strip(bar);
        Assert.Contains("(1/2*)", plain);
    }

    [Fact]
    public void StatusBar_NoMatch_ShowsRedZero()
    {
        var s = new SearchState { Active = true, Query = "xyz", SearchDone = true };
        var matches = new System.Collections.Generic.List<int>();
        string bar = SearchHelper.BuildSearchStatusBar(s, matches, done: true, width: 60);
        // ANSI 31 (red) appears, plain text has (0)
        Assert.Contains("\x1b[31m", bar);
        Assert.Contains("(0)", Strip(bar));
    }

    [Fact]
    public void StatusBar_RegexMode_ShowsTag()
    {
        var s = new SearchState { Active = true, Query = "^rep", RegexMode = true, MatchIndex = 0, SearchDone = true };
        var matches = new System.Collections.Generic.List<int> { 2 };
        string bar = SearchHelper.BuildSearchStatusBar(s, matches, done: true, width: 60);
        Assert.Contains("[regex]", Strip(bar));
    }

    [Fact]
    public void StatusBar_CountPinnedRight_CountAtEnd()
    {
        var s = new SearchState { Active = true, Query = "r", MatchIndex = 0, SearchDone = true };
        var matches = new System.Collections.Generic.List<int> { 0, 5, 9 };
        string bar = SearchHelper.BuildSearchStatusBar(s, matches, done: true, width: 40);
        string plain = Strip(bar);
        // (1/3) appears near the right end
        int pos = plain.IndexOf("(1/3)", StringComparison.Ordinal);
        Assert.True(pos > plain.Length - 10);
    }
}
```

- [ ] **Step 2: Run — confirm FAIL**

```
dotnet test ColumnFileManager.Tests --filter SearchStatusBarTests
```
Expected: fails with "BuildSearchStatusBar not found".

- [ ] **Step 3: Add `BuildSearchStatusBar` to `SearchHelper`**

```csharp
public static string BuildSearchStatusBar(SearchState search, List<int> matches, bool done, int width)
{
    bool noMatch = matches.Count == 0 && search.Query.Length > 0 && done;

    // Left: /query▌
    string promptColor = noMatch ? "\x1b[31m" : "\x1b[33m";
    string reset = "\x1b[0m";
    string left = promptColor + "/" + reset + search.Query + "\x1b[7m \x1b[0m";
    int leftLen = 1 + search.Query.Length + 1; // display width: '/' + query + cursor

    // Right: [regex] (n/m*)
    string regexTag = search.RegexMode ? "\x1b[33m[regex]\x1b[0m " : "";
    int regexTagLen = search.RegexMode ? 8 : 0; // "[regex] " = 8 chars

    string countPart;
    int countLen;
    if (search.Query.Length == 0)
    {
        countPart = "";
        countLen = 0;
    }
    else if (noMatch)
    {
        countPart = "\x1b[31m(0)\x1b[0m";
        countLen = 3;
    }
    else
    {
        int pos = matches.Count > 0 ? search.MatchIndex + 1 : 0;
        int total = matches.Count;
        string starPart = done ? "" : "\x1b[33m*\x1b[0m";
        int starLen = done ? 0 : 1;
        string inner = $"{pos}/{total}";
        countPart = $"\x1b[90m({reset}{inner}{reset}{starPart}\x1b[90m)\x1b[0m";
        countLen = 1 + inner.Length + starLen + 1; // ( inner * )
    }

    string right = regexTag + countPart;
    int rightLen = regexTagLen + countLen;

    int spaces = Math.Max(1, width - leftLen - rightLen);
    return left + new string(' ', spaces) + right;
}
```

- [ ] **Step 4: Run — confirm PASS**

```
dotnet test ColumnFileManager.Tests --filter SearchStatusBarTests
```
Expected: all 6 pass.

- [ ] **Step 5: Commit**

```
git add Program.cs ColumnFileManager.Tests/SearchStatusBarTests.cs
git commit -m "feat: add BuildSearchStatusBar helper with ANSI formatting"
```

---

## Task 9: Wire search status bar into `BuildFrame`

**Files:**
- Modify: `Program.cs` — `BuildFrame` status bar section

- [ ] **Step 1: Replace status bar logic in `BuildFrame`**

Find the status-bar block in `BuildFrame` (around line 2598):

```csharp
// Status line
string status;
if (_lastErrorMessage != null)
{
    status = _lastErrorMessage;
    _lastErrorMessage = null;
}
else
{
    string previewHint = ...
    status = $"Esc=Quit | ...";
}
status = CharacterWidth.SmartTruncate(status, width);
frame[height - 1] = CharacterWidth.PadToWidth(status, width);
```

Replace with:

```csharp
// Status line
string status;
if (State.Search.Active)
{
    List<int> matchSnapshot;
    bool done;
    lock (_searchLock)
    {
        matchSnapshot = new List<int>(State.Search.Matches);
        done = State.Search.SearchDone;
    }
    status = SearchHelper.BuildSearchStatusBar(State.Search, matchSnapshot, done, width);
    frame[height - 1] = CharacterWidth.PadToWidth(status, width);
}
else if (_lastErrorMessage != null)
{
    status = _lastErrorMessage;
    _lastErrorMessage = null;
    status = CharacterWidth.SmartTruncate(status, width);
    frame[height - 1] = CharacterWidth.PadToWidth(status, width);
}
else
{
    string previewHint = State.Preview.IsVisible ? "Shift+V=Preview[on]" : "Shift+V=Preview[off]";
    if (IsWindows())
        status = $"Esc=Quit | ↑↓=move | PgUp/PgDn=page | Home/End g/G=jump | /=search | Ctrl+Enter=Open | Shift+Enter=Menu | Ctrl+L/F5=Refresh | {previewHint}";
    else
        status = $"Esc=Quit | ↑↓=move | PgUp/PgDn=page | Home/End g/G=jump | /=search | Ctrl+Enter=Open File | Ctrl+L/F5=Refresh | {previewHint}";
    status = CharacterWidth.SmartTruncate(status, width);
    frame[height - 1] = CharacterWidth.PadToWidth(status, width);
}
```

- [ ] **Step 2: Build and smoke test**

```
dotnet build && dotnet run
```

Press `/`, type `rep` — status bar should show `/rep▌` on left and `(1/3)` on right after 300ms.

- [ ] **Step 3: Commit**

```
git add Program.cs
git commit -m "feat: wire search status bar into BuildFrame"
```

---

## Task 10: Match highlighting in `DrawColumnToLines`

**Files:**
- Modify: `Program.cs` — `DrawColumnToLines` (around line 2619)

- [ ] **Step 1: Capture search match snapshot at top of `DrawColumnToLines`**

Immediately after the `int startRow = 1;` line (line 2659), add:

```csharp
// Search match snapshot — only meaningful for the active column
bool inSearch = active && State.Search.Active;
HashSet<int> matchSet = new HashSet<int>();
int currentMatchEntry = -1;
if (inSearch)
{
    lock (_searchLock)
    {
        if (State.Search.Matches.Count > 0)
        {
            matchSet = new HashSet<int>(State.Search.Matches);
            currentMatchEntry = State.Search.Matches[State.Search.MatchIndex];
        }
    }
}
```

- [ ] **Step 2: Override entry rendering inside the loop**

The loop (line 2663) currently has two branches: `if (isSelected)` and `else`. The entry's absolute index in `column.Entries` is `scrollOffset + i`. Replace both branches with:

```csharp
int entryAbsIndex = scrollOffset + i;
bool isCurrentMatch = inSearch && entryAbsIndex == currentMatchEntry;
bool isOtherMatch   = inSearch && !isCurrentMatch && matchSet.Contains(entryAbsIndex);

if (isCurrentMatch)
{
    // Green bg + black text — overrides normal selection colours
    string displayText = prefix + entry;
    int displayTextWidth = CharacterWidth.GetStringWidth(displayText);
    int paddingNeeded = Math.Max(0, contentSlot - displayTextWidth);
    string fullLine = "\x1b[42m\x1b[30m" + displayText + new string(' ', paddingNeeded) + "\x1b[0m";
    lines[startRow + i].AddColumn(fullLine, displayTextWidth + paddingNeeded, ColumnWidth);
}
else if (isSelected)
{
    // Normal selection rendering (unchanged from original)
    string bgColor = active ? "\x1b[47m" : "\x1b[100m";
    string textColor = isDirectory ? "\x1b[34m" : "\x1b[30m";
    string reset = "\x1b[0m";
    string displayText = prefix + entry;
    int displayTextWidth = CharacterWidth.GetStringWidth(displayText);
    int paddingNeeded = Math.Max(0, contentSlot - displayTextWidth);
    string fullLine = bgColor + textColor + displayText + new string(' ', paddingNeeded) + reset;
    int totalDisplayWidth = displayTextWidth + paddingNeeded;
    lines[startRow + i].AddColumn(fullLine, totalDisplayWidth, ColumnWidth);
}
else if (isOtherMatch)
{
    // Green fg, no background
    int displayWidth = 2 + CharacterWidth.GetStringWidth(entry);
    string coloredEntry = "\x1b[32m" + entry + "\x1b[0m";
    lines[startRow + i].AddColumn(prefix + coloredEntry, displayWidth, ColumnWidth);
}
else
{
    // Normal non-selected rendering (unchanged from original)
    int displayWidth = 2 + CharacterWidth.GetStringWidth(entry);
    if (isDirectory)
        entry = AnsiColors.Colorize(entry, AnsiColors.Blue);
    lines[startRow + i].AddColumn(prefix + entry, displayWidth, ColumnWidth);
}
```

- [ ] **Step 3: Build and smoke test**

```
dotnet build && dotnet run
```

Press `/`, type a prefix — other matches turn green text, current match shows black text on green background.

- [ ] **Step 4: Commit**

```
git add Program.cs
git commit -m "feat: highlight search matches in DrawColumnToLines"
```

---

## Task 11: Startup migemo initialization and warning

**Files:**
- Modify: `Program.cs` — `Main` method, before `\x1b[?1049h`

- [ ] **Step 1: Initialize `_migemo` before entering alternate screen**

In `Main`, find:
```csharp
Console.Write("\x1b[?1049h"); // Enter alternate screen buffer
```

Add immediately before it:

```csharp
_migemo = new MigemoProvider();
if (!_migemo.IsAvailable)
{
    if (_migemo.DllLoaded)
        Console.WriteLine("migemo: dict not found — plain search active");
    _migemo.Dispose();
    _migemo = null;
}
```

- [ ] **Step 2: Dispose `_migemo` on exit**

In the `finally` block of `Main`:

```csharp
finally
{
    try { Console.CursorVisible = true; } catch { }
    Console.Write("\x1b[?1049l");
    _migemo?.Dispose();
}
```

- [ ] **Step 3: Build**

```
dotnet build
```
Expected: 0 errors.

- [ ] **Step 4: Commit**

```
git add Program.cs
git commit -m "feat: initialize MigemoProvider at startup with dict-not-found warning"
```

---

## Task 12: Final smoke test

- [ ] **Step 1: Run all tests**

```
dotnet test ColumnFileManager.Tests
```
Expected: all tests pass.

- [ ] **Step 2: Manual verification checklist**

Run `dotnet run` and verify each item:

- [ ] `PgUp`/`PgDn` scroll the active column by a page
- [ ] `Ctrl+B`/`Ctrl+F` do the same
- [ ] `Home`/`g` jump to top; `End`/`G` jump to bottom
- [ ] `/` enters search mode — status bar changes to `/▌`
- [ ] Typing updates `/query▌` immediately; after 300ms matches highlight
- [ ] `Ctrl+N`/`↓` and `Ctrl+P`/`↑` cycle through green-highlighted matches
- [ ] Current match shows black text on green background
- [ ] Other matches show green text
- [ ] `(n/m)` count appears at right edge of status bar
- [ ] `(n/m*)` shows while scan is in progress
- [ ] `(0)` and red prompt appear when no match
- [ ] `Ctrl+R` adds `[regex]` tag; `^rep.*pdf$` matches correctly
- [ ] `Ctrl+R` again removes `[regex]` tag
- [ ] `Enter` exits search, cursor stays at matched entry
- [ ] `Esc` exits search, cursor returns to original position
- [ ] `Esc` from normal mode still quits the app

- [ ] **Step 3: Final commit**

```
git add Program.cs
git commit -m "feat: complete quick search and navigation keys"
```
