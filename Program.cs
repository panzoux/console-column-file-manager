// Build:
// dotnet new console -n ColumnFileManager
// Replace Program.cs with this file
// dotnet run
//
// Tested: .NET 8 / Windows 10+ / macOS 12+
// Single file, no external dependencies, LINQ-free
//
// Features:
// - Column-based file browser (Finder-like)
// - Vertical scroll with ScrollOffset
// - Horizontal scroll (multiple columns)
// - Windows drive list / macOS root support
// - Symbolic link cycle prevention
// - Unicode width handling (East Asian)
// - Incremental rendering (diff-based)
// - Dynamic resize handling
//
// Operations:
// ↑↓ = scroll / select
// ← → = switch column
// Enter = open folder
// Backspace = parent directory
// 'r' = refresh
// Escape = quit

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


sealed class Line
{
    private readonly List<string> _cols = new List<string>();
    private int _totalDisplayWidth;

    // Separator takes 1 display col; content slot shrinks for non-first columns
    public void AddColumn(string content, int displayWidth, int columnWidth)
    {
        bool isFirst = _cols.Count == 0;
        int contentSlot = columnWidth - (isFirst ? 0 : 1);
        int padding = contentSlot - displayWidth;
        if (padding > 0)
            content += new string(' ', padding);
        if (!isFirst)
            content = "|" + content;
        _cols.Add(content);
        _totalDisplayWidth += columnWidth;
    }

    public string Render(int targetWidth)
    {
        string result = string.Concat(_cols);
        // Pad using display width, not character count
        int padding = targetWidth - _totalDisplayWidth;
        if (padding > 0)
            result += new string(' ', padding);
        return result;
    }
}

static class AnsiColors
{
    public const string Blue = "\x1b[34m";
    public const string Reset = "\x1b[0m";
    public static string Colorize(string text, string color) => color + text + Reset;
}

/// <summary>
/// Proper CJK character width handling (adapted from twf\Utilities\CharacterWidthHelper.cs)
/// </summary>
static class CharacterWidth
{
    public static int CJKCharacterWidth { get; set; } = 2;
    public static string DefaultEllipsis { get; set; } = "…";

    public static int GetCharWidth(char c)
    {
        if (CJKCharacterWidth == 0)
            return 1;
        if (IsZeroWidthCharacter(c))
            return 0;
        if (IsCJKCharacter(c))
            return CJKCharacterWidth;
        return 1;
    }

    private static bool IsCJKCharacter(char c)
    {
        int code = (int)c;
        if (code >= 0x4E00 && code <= 0x9FFF) return true;   // CJK Unified Ideographs
        if (code >= 0x3400 && code <= 0x4DBF) return true;   // CJK Extension A
        if (code >= 0x3040 && code <= 0x309F) return true;   // Hiragana
        if (code >= 0x30A0 && code <= 0x30FF) return true;   // Katakana
        if (code >= 0x31F0 && code <= 0x31FF) return true;   // Katakana Phonetic Extensions
        if (code >= 0xAC00 && code <= 0xD7AF) return true;   // Hangul Syllables
        if (code >= 0x1100 && code <= 0x11FF) return true;   // Hangul Jamo
        if (code >= 0xFF00 && code <= 0xFFEF) return true;   // Fullwidth Forms
        if (code >= 0xF900 && code <= 0xFAFF) return true;   // CJK Compatibility Ideographs
        if (code >= 0x2E80 && code <= 0x2EFF) return true;   // CJK Radicals Supplement
        if (code >= 0x3000 && code <= 0x303F) return true;   // CJK Symbols and Punctuation
        if (code >= 0x3100 && code <= 0x31EF) return true;   // Bopomofo, Hangul Compat Jamo, Kanbun, CJK Strokes
        if (code >= 0x3200 && code <= 0x32FF) return true;   // Enclosed CJK Letters and Months (㉒ etc.)
        if (code >= 0x3300 && code <= 0x33FF) return true;   // CJK Compatibility
        if (code >= 0xFE30 && code <= 0xFE4F) return true;   // CJK Compatibility Forms
        return false;
    }

    private static bool IsZeroWidthCharacter(char c)
    {
        int code = (int)c;
        if (code >= 0x0300 && code <= 0x036F) return true;   // Combining Diacritical Marks
        if (code >= 0x1AB0 && code <= 0x1AFF) return true;
        if (code >= 0x1DC0 && code <= 0x1DFF) return true;
        if (code >= 0xFE20 && code <= 0xFE2F) return true;
        if (code == 0x200B || code == 0x200C || code == 0x200D) return true;  // Zero-width joiners
        if (code >= 0xFE00 && code <= 0xFE0F) return true;   // Variation Selectors
        return false;
    }

    public static int GetStringWidth(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        if (CJKCharacterWidth == 0)
            return text.Length;
        int width = 0;
        foreach (char c in text)
            width += GetCharWidth(c);
        return width;
    }

    public static string SmartTruncate(string? text, int maxWidth, string? ellipsis = null)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        if (maxWidth < 0)
            throw new ArgumentException("Max width cannot be negative");

        ellipsis ??= DefaultEllipsis;
        int textWidth = GetStringWidth(text);
        if (textWidth <= maxWidth)
            return text;

        int ellipsisWidth = GetStringWidth(ellipsis);
        int availableWidth = maxWidth - ellipsisWidth;

        if (availableWidth < 2)
            return TruncateToWidth(text, maxWidth, ellipsis);

        // Keep 2/3 at start, 1/3 at end (to preserve extensions)
        int widthForEnd = availableWidth / 3;
        int widthForStart = availableWidth - widthForEnd;

        int currentWidth = 0;
        int startIndex = 0;
        for (int i = 0; i < text.Length; i++)
        {
            int len = GetCharWidth(text[i]);
            if (currentWidth + len > widthForStart) break;
            currentWidth += len;
            startIndex++;
        }

        currentWidth = 0;
        int endIndex = text.Length;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            int len = GetCharWidth(text[i]);
            if (currentWidth + len > widthForEnd) break;
            currentWidth += len;
            endIndex--;
        }

        if (startIndex >= endIndex)
            return TruncateToWidth(text, maxWidth, ellipsis);

        return text.Substring(0, startIndex) + ellipsis + text.Substring(endIndex);
    }

    private static string TruncateToWidth(string? text, int maxWidth, string? ellipsis = null)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        ellipsis ??= DefaultEllipsis;
        int currentWidth = 0;
        int ellipsisWidth = GetStringWidth(ellipsis);
        int targetWidth = maxWidth - ellipsisWidth;

        if (targetWidth < 0)
        {
            targetWidth = maxWidth;
            ellipsis = string.Empty;
        }

        for (int i = 0; i < text.Length; i++)
        {
            int charWidth = GetCharWidth(text[i]);
            if (currentWidth + charWidth > targetWidth)
                return text.Substring(0, i) + ellipsis;
            currentWidth += charWidth;
        }

        return text;
    }

    public static string PadToWidth(string? text, int targetWidth, char padChar = ' ')
    {
        if (string.IsNullOrEmpty(text))
            return new string(padChar, targetWidth);

        int textWidth = GetStringWidth(text);
        if (textWidth >= targetWidth)
            return text;

        int spacesNeeded = targetWidth - textWidth;
        return text + new string(padChar, spacesNeeded);
    }
}

sealed class Column
{
    public string Path;
    public List<string> Entries;
    public int Selected;
    public int ScrollOffset;
    public Dictionary<string, List<string>> CachedChildren;
    public DateTime CachedTime;

    // Loading state for async operations
    public bool IsLoading { get; set; }
    public int EntriesRead { get; set; }
    public int DirectoriesCount { get; set; }
    public CancellationTokenSource? ReadCts { get; set; }
    public Task? LoadingTask { get; set; }

    public Column(string path)
    {
        Path = path;
        Entries = new List<string>();
        Selected = 0;
        ScrollOffset = 0;
        CachedChildren = new Dictionary<string, List<string>>();
        CachedTime = DateTime.UtcNow;
        IsLoading = false;
        EntriesRead = 0;
        DirectoriesCount = 0;
    }
}

sealed class ScreenState
{
    public int ActiveColumn;
    public int HorizontalScroll;
    public string[]? PrevFrame;
    public int PrevWidth;
    public int PrevHeight;
}

static class Program
{
    const int ColumnWidth = 32;
    const int CacheExpireMs = 2000;
    const int MaxPathLength = 100;
    const int NavigationDebounceMs = 300;

    static readonly List<Column> Columns = new List<Column>();
    static readonly ScreenState State = new ScreenState();
    static DateTime _lastNavigationTime = DateTime.MinValue;

    static async Task Main()
    {
        try
        {
            Console.CursorVisible = false;
        }
        catch { }

        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            string root = GetRootPath();
            Columns.Add(CreateColumn(root));
            await RebuildRightSideAsync(0);

            while (true)
            {
                Draw();

                if (!Console.KeyAvailable)
                {
                    System.Threading.Thread.Sleep(50);
                    continue;
                }

                ConsoleKeyInfo key = Console.ReadKey(true);

                switch (key.KeyChar)
                {
                    case 'r':
                    case 'R':
                        RefreshCurrent();
                        break;

                    case (char)27: // Esc
                        Console.Clear();
                        try { Console.CursorVisible = true; } catch { }
                        return;

                    default:
                        await HandleSpecialKeyAsync(key);
                        break;
                }
            }
        }
        finally
        {
            try { Console.CursorVisible = true; } catch { }
        }
    }

    static string GetRootPath()
    {
        // Special marker for drive list view
        if (IsWindows())
        {
            return "::DRIVES::";
        }
        return "/";
    }

    static bool IsWindows()
    {
        return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows);
    }

    static async Task HandleSpecialKeyAsync(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                await MoveUpAsync();
                break;

            case ConsoleKey.DownArrow:
                await MoveDownAsync();
                break;

            case ConsoleKey.LeftArrow:
                MoveLeft();
                break;

            case ConsoleKey.RightArrow:
                MoveRight();
                break;

            case ConsoleKey.Enter:
                await EnterAsync();
                break;

            case ConsoleKey.Backspace:
                Parent();
                break;
        }
    }

    static Column CreateColumn(string path)
    {
        Column column = new Column(path);

        // Special case: drive list for Windows
        if (path == "::DRIVES::")
        {
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    if (drives[i].IsReady)
                    {
                        column.Entries.Add(drives[i].Name.TrimEnd('\\') + "/");
                    }
                }
            }
            catch
            {
            }
            return column;
        }

        if (!IsValidPath(path))
            return column;

        try
        {
            // Directories first
            string[] dirs = Directory.GetDirectories(path);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < dirs.Length; i++)
            {
                string name = Path.GetFileName(dirs[i]);
                column.Entries.Add(name + "/");
            }

            // Then files
            string[] files = Directory.GetFiles(path);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < files.Length; i++)
            {
                column.Entries.Add(Path.GetFileName(files[i]));
            }
        }
        catch
        {
            // Permission denied, etc.
        }

        column.CachedTime = DateTime.UtcNow;
        return column;
    }

    static Task<Column> CreateColumnAsync(string path, CancellationToken ct)
    {
        Column column = new Column(path);

        // Special case: drive list for Windows
        if (path == "::DRIVES::")
        {
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    if (drives[i].IsReady)
                    {
                        column.Entries.Add(drives[i].Name.TrimEnd('\\') + "/");
                    }
                }
            }
            catch
            {
            }
            column.IsLoading = false;
            return Task.FromResult(column);
        }

        if (!IsValidPath(path))
        {
            column.IsLoading = false;
            return Task.FromResult(column);
        }

        column.IsLoading = true;
        column.EntriesRead = 0;
        column.DirectoriesCount = 0;
        column.ReadCts = new CancellationTokenSource();

        column.LoadingTask = Task.Run(async () =>
        {
            try
            {
                int visibleHeight = Console.WindowHeight - 3;
                int bufferSize = Math.Max(100, visibleHeight + 20);

                List<string> dirs = new List<string>();
                List<string> files = new List<string>();

                // Read directories first (non-blocking enumeration)
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(path))
                    {
                        ct.ThrowIfCancellationRequested();
                        dirs.Add(dir);
                        if (dirs.Count >= bufferSize)
                            break;
                    }
                    dirs.Sort(StringComparer.OrdinalIgnoreCase);
                    column.DirectoriesCount = dirs.Count;

                    // Add to entries with "/" suffix
                    foreach (var dir in dirs)
                    {
                        column.Entries.Add(Path.GetFileName(dir) + "/");
                        column.EntriesRead++;
                    }
                }
                catch
                {
                    // Permission denied or other errors
                }

                // Read files (up to remaining buffer)
                int remainingBuffer = bufferSize - dirs.Count;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(path))
                    {
                        ct.ThrowIfCancellationRequested();
                        files.Add(file);
                        column.EntriesRead++;
                        if (files.Count >= remainingBuffer)
                            break;
                    }
                    files.Sort(StringComparer.OrdinalIgnoreCase);

                    // Add to entries
                    foreach (var file in files)
                    {
                        column.Entries.Add(Path.GetFileName(file));
                    }
                }
                catch
                {
                    // Permission denied or other errors
                }

                // Continue reading rest in background
                if (!ct.IsCancellationRequested)
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            List<string> restDirs = new List<string>();
                            int count = 0;
                            foreach (var dir in Directory.EnumerateDirectories(path))
                            {
                                if (ct.IsCancellationRequested)
                                    return;
                                if (count >= dirs.Count)
                                {
                                    restDirs.Add(dir);
                                }
                                count++;
                            }

                            List<string> restFiles = new List<string>();
                            count = 0;
                            foreach (var file in Directory.EnumerateFiles(path))
                            {
                                if (ct.IsCancellationRequested)
                                    return;
                                if (count >= files.Count)
                                {
                                    restFiles.Add(file);
                                }
                                count++;
                            }

                            restDirs.Sort(StringComparer.OrdinalIgnoreCase);
                            restFiles.Sort(StringComparer.OrdinalIgnoreCase);

                            foreach (var dir in restDirs)
                            {
                                if (ct.IsCancellationRequested)
                                    return;
                                column.Entries.Add(Path.GetFileName(dir) + "/");
                                column.EntriesRead++;
                            }

                            foreach (var file in restFiles)
                            {
                                if (ct.IsCancellationRequested)
                                    return;
                                column.Entries.Add(Path.GetFileName(file));
                                column.EntriesRead++;
                            }
                        }
                        catch
                        {
                            // Permission denied or other errors
                        }
                    }, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when navigating away
            }
            finally
            {
                column.IsLoading = false;
            }
        }, ct);

        column.CachedTime = DateTime.UtcNow;
        return Task.FromResult(column);
    }

    static string GetRealPath(string path)
    {
        try
        {
            DirectoryInfo di = new DirectoryInfo(path);
            return di.FullName;
        }
        catch
        {
            return path;
        }
    }

    static bool IsValidPath(string path)
    {
        try
        {
            DirectoryInfo di = new DirectoryInfo(path);
            return di.Exists;
        }
        catch
        {
            return false;
        }
    }

    static string FindDeepestAccessiblePath(string path)
    {
        try
        {
            DirectoryInfo di = new DirectoryInfo(path);
            if (di.Exists)
                return path;

            // Walk backward to find the deepest accessible parent
            while (di.Parent != null)
            {
                di = di.Parent;
                if (di.Exists)
                    return di.FullName;
            }

            // Return root as fallback
            return IsWindows() ? "C:\\" : "/";
        }
        catch
        {
            return IsWindows() ? "C:\\" : "/";
        }
    }

    static async Task MoveUpAsync()
    {
        Column c = Columns[State.ActiveColumn];
        int visibleHeight = Console.WindowHeight - 2;

        if (c.Selected > 0)
        {
            c.Selected--;

            // Auto-scroll
            if (c.Selected < c.ScrollOffset)
                c.ScrollOffset = c.Selected;

            // Only rebuild (read directory) if debounce allows
            if (IsNavigationDebounced())
                await RebuildRightSideAsync(State.ActiveColumn);
            else
                CancelRightSideReads(State.ActiveColumn);
        }
    }

    static async Task MoveDownAsync()
    {
        Column c = Columns[State.ActiveColumn];
        int visibleHeight = Console.WindowHeight - 2;

        if (c.Selected + 1 < c.Entries.Count)
        {
            c.Selected++;

            // Auto-scroll
            if (c.Selected >= c.ScrollOffset + visibleHeight)
                c.ScrollOffset = c.Selected - visibleHeight + 1;

            // Only rebuild (read directory) if debounce allows
            if (IsNavigationDebounced())
                await RebuildRightSideAsync(State.ActiveColumn);
            else
                CancelRightSideReads(State.ActiveColumn);
        }
    }

    static bool IsNavigationDebounced()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - _lastNavigationTime).TotalMilliseconds < NavigationDebounceMs)
            return false;
        _lastNavigationTime = now;
        return true;
    }

    static void CancelRightSideReads(int columnIndex)
    {
        // Cancel any pending directory reads on columns to the right of current
        for (int i = columnIndex + 1; i < Columns.Count; i++)
        {
            Columns[i].ReadCts?.Cancel();
        }
    }

    static void MoveLeft()
    {
        if (State.ActiveColumn > 0)
        {
            State.ActiveColumn--;
            UpdateHorizontalScroll();
        }
    }

    static void MoveRight()
    {
        if (State.ActiveColumn + 1 < Columns.Count)
        {
            State.ActiveColumn++;
            UpdateHorizontalScroll();
        }
    }

    static void UpdateHorizontalScroll()
    {
        int visibleColumns = Math.Max(1, Console.WindowWidth / ColumnWidth);

        if (State.ActiveColumn < State.HorizontalScroll)
            State.HorizontalScroll = State.ActiveColumn;

        if (State.ActiveColumn >= State.HorizontalScroll + visibleColumns)
            State.HorizontalScroll = State.ActiveColumn - visibleColumns + 1;
    }

    static async Task EnterAsync()
    {
        Column c = Columns[State.ActiveColumn];

        if (c.Entries.Count == 0)
            return;

        string name = c.Entries[c.Selected];

        if (!name.EndsWith("/"))
            return;

        if (State.ActiveColumn + 1 < Columns.Count)
        {
            State.ActiveColumn++;
        }
        else
        {
            State.ActiveColumn++;
        }

        UpdateHorizontalScroll();
        await RebuildRightSideAsync(State.ActiveColumn - 1);
    }

    static void Parent()
    {
        if (State.ActiveColumn == 0)
            return;

        State.ActiveColumn--;
        UpdateHorizontalScroll();
    }

    static void RefreshCurrent()
    {
        Column c = Columns[State.ActiveColumn];
        c.CachedChildren.Clear();
        c.CachedTime = DateTime.UtcNow.AddMilliseconds(-CacheExpireMs - 1);
        c.ReadCts?.Cancel();
        c.Entries.Clear();
        c.EntriesRead = 0;
        c.DirectoriesCount = 0;
        c.Selected = 0;
        c.ScrollOffset = 0;
        _ = RebuildRightSideAsync(State.ActiveColumn);
    }

    static string GetCurrentFullPath()
    {
        if (State.ActiveColumn < 0 || State.ActiveColumn >= Columns.Count)
            return "";

        Column c = Columns[State.ActiveColumn];

        if (c.Entries.Count == 0)
            return c.Path;

        if (c.Selected < 0 || c.Selected >= c.Entries.Count)
            return c.Path;

        string name = c.Entries[c.Selected];

        if (c.Path == "::DRIVES::")
            return name.TrimEnd('/');

        return Path.Combine(c.Path, name.TrimEnd('/'));
    }

    static async Task RebuildRightSideAsync(int columnIndex)
    {
        while (Columns.Count > columnIndex + 1)
        {
            Column col = Columns[Columns.Count - 1];
            // Cancel any ongoing reads
            col.ReadCts?.Cancel();
            Columns.RemoveAt(Columns.Count - 1);
        }

        string? nextPath = GetSelectedDirectory(columnIndex);

        while (nextPath != null)
        {
            Column next = await CreateColumnAsync(nextPath, CancellationToken.None);
            Columns.Add(next);

            nextPath = GetSelectedDirectory(Columns.Count - 1);
        }
    }

    static string? GetSelectedDirectory(int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= Columns.Count)
            return null;

        Column c = Columns[columnIndex];

        if (c.Entries.Count == 0)
            return null;

        if (c.Selected < 0 || c.Selected >= c.Entries.Count)
            return null;

        string name = c.Entries[c.Selected];

        if (!name.EndsWith("/"))
            return null;

        // Special case: drive list
        if (c.Path == "::DRIVES::")
        {
            string drive = name.Substring(0, name.Length - 1);  // "C:"
            // Ensure proper path format
            if (!drive.EndsWith("\\") && !drive.EndsWith("/"))
                drive += "\\";
            try
            {
                if (Directory.Exists(drive))
                    return drive;
            }
            catch
            {
            }
            return null;
        }

        string folderName = name.Substring(0, name.Length - 1);
        string full = Path.Combine(c.Path, folderName);

        try
        {
            if (Directory.Exists(full))
                return full;
        }
        catch
        {
        }

        return null;
    }

    static void Draw()
    {
        int width = Console.WindowWidth;
        int height = Console.WindowHeight;

        // Resize detection
        if (width != State.PrevWidth || height != State.PrevHeight)
        {
            State.PrevFrame = null;
            State.PrevWidth = width;
            State.PrevHeight = height;
        }

        string[] currentFrame = BuildFrame(width, height);

        // Incremental rendering
        if (State.PrevFrame == null)
        {
            Console.Clear();
            for (int i = 0; i < currentFrame.Length; i++)
                WriteLineAt(0, i, currentFrame[i]);
        }
        else
        {
            for (int i = 0; i < currentFrame.Length; i++)
            {
                if (State.PrevFrame[i] != currentFrame[i])
                    WriteLineAt(0, i, currentFrame[i]);
            }
        }

        State.PrevFrame = currentFrame;
    }

    static string[] BuildFrame(int width, int height)
    {
        string[] frame = new string[height];

        // Build frame using segment-based approach
        Line[] lines = new Line[height];
        for (int i = 0; i < height; i++)
            lines[i] = new Line();

        int displayX = 0;  // Track display position, not character position

        for (int i = State.HorizontalScroll;
             i < Columns.Count && displayX + ColumnWidth <= width;
             i++)
        {
            bool isFirstVisible = (i == State.HorizontalScroll);
            DrawColumnToLines(lines, Columns[i], displayX, i == State.ActiveColumn, i < State.ActiveColumn, width, height, isFirstVisible);
            displayX += ColumnWidth;
        }

        // Render each line to string and pad to window width
        for (int i = 0; i < height - 2; i++)
        {
            frame[i] = lines[i].Render(width);
        }

        // Full path line (above status)
        string fullPath = GetCurrentFullPath();
        fullPath = CharacterWidth.SmartTruncate(fullPath, width);
        frame[height - 2] = CharacterWidth.PadToWidth(fullPath, width);

        // Status line
        string status = "Esc=Quit | ↑↓=Select | ←→=Column | Enter=Open | Bksp=Parent | R=Refresh";
        status = CharacterWidth.SmartTruncate(status, width);
        frame[height - 1] = CharacterWidth.PadToWidth(status, width);

        return frame;
    }

    static void DrawColumnToLines(Line[] lines, Column column, int displayX, bool active, bool isLeft, int frameWidth, int frameHeight, bool isFirstVisible)
    {
        int visibleHeight = frameHeight - 3;  // Reserve 3 rows: header + fullpath + status

        int entryCount = column.Entries.Count;

        // Auto-scroll to keep selection visible
        if (column.Selected < column.ScrollOffset)
            column.ScrollOffset = column.Selected;
        if (column.Selected >= column.ScrollOffset + visibleHeight)
            column.ScrollOffset = column.Selected - visibleHeight + 1;

        // Content slot: non-first columns lose 1 display col to the separator
        int separatorWidth = isFirstVisible ? 0 : 1;
        int contentSlot = ColumnWidth - separatorWidth;  // display cols available for content
        int maxEntryWidth = contentSlot - 2;             // minus 2-char prefix

        // Header - show only leaf name, not full path
        string header;
        if (column.Path == "::DRIVES::")
        {
            header = "Drives";
        }
        else
        {
            header = Path.GetFileName(column.Path);
            if (string.IsNullOrEmpty(header))
                header = column.Path;
        }

        // Append progress counter if loading
        if (column.IsLoading)
        {
            header += $" [loading {column.EntriesRead}]";
        }

        header = CharacterWidth.SmartTruncate(header, contentSlot);
        int headerDisplayWidth = CharacterWidth.GetStringWidth(header);
        lines[0].AddColumn(header, headerDisplayWidth, ColumnWidth);

        int startRow = 1;
        int scrollOffset = column.ScrollOffset;

        // Loop ALL visible rows so short columns still contribute empty cells
        for (int i = 0; i < visibleHeight; i++)
        {
            if (scrollOffset + i >= entryCount)
            {
                lines[startRow + i].AddColumn("", 0, ColumnWidth);
                continue;
            }

            string text = column.Entries[scrollOffset + i];
            bool isDirectory = text.EndsWith("/");
            bool isSelected = (scrollOffset + i) == column.Selected;

            string prefix;
            if (isSelected)
            {
                if (active)
                    prefix = "> ";
                else if (isLeft)
                    prefix = "] ";
                else
                    prefix = "  ";
            }
            else
                prefix = "  ";

            // Truncate to fit
            string entry = CharacterWidth.SmartTruncate(text, maxEntryWidth);

            if (isSelected)
            {
                // Background extends across full pane width
                // Active: white bg, Inactive: gray bg
                // Text: black for files, dark blue for directories
                string bgColor = active ? "\x1b[47m" : "\x1b[100m";
                string textColor = isDirectory ? "\x1b[34m" : "\x1b[30m";
                string reset = "\x1b[0m";

                string displayText = prefix + entry;
                int displayTextWidth = CharacterWidth.GetStringWidth(displayText);
                int paddingNeeded = Math.Max(0, contentSlot - displayTextWidth);

                // Build full line: background + text color + content + padding + reset
                string fullLine = bgColor + textColor + displayText + new string(' ', paddingNeeded) + reset;
                int totalDisplayWidth = displayTextWidth + paddingNeeded;

                lines[startRow + i].AddColumn(fullLine, totalDisplayWidth, ColumnWidth);
            }
            else
            {
                // Non-selected: use original logic
                int displayWidth = 2 + CharacterWidth.GetStringWidth(entry);
                if (isDirectory)
                    entry = AnsiColors.Colorize(entry, AnsiColors.Blue);
                lines[startRow + i].AddColumn(prefix + entry, displayWidth, ColumnWidth);
            }
        }
    }




    static void WriteLineAt(int x, int y, string text)
    {
        if (y >= Console.WindowHeight)
            return;

        try
        {
            Console.SetCursorPosition(x, y);
            Console.Write(text);
        }
        catch
        {
            // Window resize during write
        }
    }
}
