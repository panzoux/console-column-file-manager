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
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.ComponentModel;
using System.Security.Cryptography;

internal enum FileCategory { Text, Image, Video, Audio, Archive, Executable, Pdf, Drive, Binary }

internal record FileType(FileCategory Category, string Label, string MimeType = "");

internal static class FileTypeDetector
{
    private static readonly (int Offset, byte[] Sig, FileType Type)[] _sigs =
    [
        (0,  [0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A], new(FileCategory.Image,      "PNG Image",         "image/png")),
        (0,  [0x66,0x4C,0x61,0x43],                      new(FileCategory.Audio,      "FLAC Audio",        "audio/flac")),
        (0,  [0x4F,0x67,0x67,0x53],                      new(FileCategory.Audio,      "OGG Audio",         "audio/ogg")),
        (0,  [0x1A,0x45,0xDF,0xA3],                      new(FileCategory.Video,      "Matroska Video",    "video/x-matroska")),
        (0,  [0x47,0x49,0x46,0x38],                      new(FileCategory.Image,      "GIF Image",         "image/gif")),
        (0,  [0x25,0x50,0x44,0x46],                      new(FileCategory.Pdf,        "PDF Document",      "application/pdf")),
        (0,  [0x50,0x4B,0x03,0x04],                      new(FileCategory.Archive,    "ZIP Archive",       "application/zip")),
        (0,  [0x50,0x4B,0x05,0x06],                      new(FileCategory.Archive,    "ZIP Archive",       "application/zip")),
        (0,  [0x37,0x7A,0xBC,0xAF,0x27,0x1C],            new(FileCategory.Archive,    "7-Zip Archive")),
        (0,  [0x52,0x61,0x72,0x21,0x1A,0x07],            new(FileCategory.Archive,    "RAR Archive")),
        (0,  [0x7F,0x45,0x4C,0x46],                      new(FileCategory.Executable, "ELF Executable")),
        // RIFF is handled by dedicated early-return before the loop
        (4,  [0x66,0x74,0x79,0x70],                      new(FileCategory.Video,      "MP4 Video",         "video/mp4")), // ftyp box
        (0,  [0x4D,0x5A],                                new(FileCategory.Executable, "PE Executable",     "application/x-msdownload")),
        (0,  [0x49,0x44,0x33],                           new(FileCategory.Audio,      "MP3 Audio",         "audio/mpeg")),
        (0,  [0x42,0x5A,0x68],                           new(FileCategory.Archive,    "BZip2 Archive")),
        (0,  [0x1F,0x8B],                                new(FileCategory.Archive,    "GZip Archive",      "application/gzip")),
        (0,  [0xFF,0xD8],                                new(FileCategory.Image,      "JPEG Image",        "image/jpeg")),
        (0,  [0x42,0x4D],                                new(FileCategory.Image,      "BMP Image",         "image/bmp")),
        (0,  [0xFF,0xFB],                                new(FileCategory.Audio,      "MP3 Audio",         "audio/mpeg")),
        (0,  [0xFF,0xF3],                                new(FileCategory.Audio,      "MP3 Audio",         "audio/mpeg")),
        (0,  [0xFF,0xF2],                                new(FileCategory.Audio,      "MP3 Audio",         "audio/mpeg")),
    ];

    private static readonly Dictionary<string, FileType> _extMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"]  = new(FileCategory.Text, "Plain Text",       "text/plain"),
        [".md"]   = new(FileCategory.Text, "Markdown",         "text/markdown"),
        [".cs"]   = new(FileCategory.Text, "C# Source"),
        [".js"]   = new(FileCategory.Text, "JavaScript"),
        [".ts"]   = new(FileCategory.Text, "TypeScript"),
        [".py"]   = new(FileCategory.Text, "Python Source"),
        [".rb"]   = new(FileCategory.Text, "Ruby Source"),
        [".go"]   = new(FileCategory.Text, "Go Source"),
        [".rs"]   = new(FileCategory.Text, "Rust Source"),
        [".c"]    = new(FileCategory.Text, "C Source"),
        [".h"]    = new(FileCategory.Text, "C Header"),
        [".cpp"]  = new(FileCategory.Text, "C++ Source"),
        [".java"] = new(FileCategory.Text, "Java Source"),
        [".json"] = new(FileCategory.Text, "JSON"),
        [".xml"]  = new(FileCategory.Text, "XML"),
        [".html"] = new(FileCategory.Text, "HTML"),
        [".htm"]  = new(FileCategory.Text, "HTML"),
        [".css"]  = new(FileCategory.Text, "CSS"),
        [".sh"]   = new(FileCategory.Text, "Shell Script"),
        [".bash"] = new(FileCategory.Text, "Shell Script"),
        [".bat"]  = new(FileCategory.Text, "Batch Script"),
        [".cmd"]  = new(FileCategory.Text, "Batch Script"),
        [".ps1"]  = new(FileCategory.Text, "PowerShell Script"),
        [".yaml"] = new(FileCategory.Text, "YAML"),
        [".yml"]  = new(FileCategory.Text, "YAML"),
        [".toml"] = new(FileCategory.Text, "TOML"),
        [".ini"]  = new(FileCategory.Text, "INI Config"),
        [".cfg"]  = new(FileCategory.Text, "Config File"),
        [".conf"] = new(FileCategory.Text, "Config File"),
        [".log"]  = new(FileCategory.Text, "Log File"),
        [".csv"]  = new(FileCategory.Text, "CSV"),
        [".sql"]  = new(FileCategory.Text, "SQL Script"),
        [".svg"]  = new(FileCategory.Image, "SVG Image",       "image/svg+xml"),
        [".ico"]  = new(FileCategory.Image, "Icon",            "image/x-icon"),
        [".webp"] = new(FileCategory.Image, "WebP Image",      "image/webp"),
        [".mp4"]  = new(FileCategory.Video, "MP4 Video",       "video/mp4"),
        [".mov"]  = new(FileCategory.Video, "QuickTime Video", "video/quicktime"),
        [".avi"]  = new(FileCategory.Video, "AVI Video",       "video/x-msvideo"),
        [".wmv"]  = new(FileCategory.Video, "WMV Video",       "video/x-ms-wmv"),
        [".webm"] = new(FileCategory.Video, "WebM Video",      "video/webm"),
        [".mkv"]  = new(FileCategory.Video, "Matroska Video"),
        [".flv"]  = new(FileCategory.Video, "Flash Video"),
        [".wav"]  = new(FileCategory.Audio, "WAV Audio",       "audio/wav"),
        [".mp3"]  = new(FileCategory.Audio, "MP3 Audio",       "audio/mpeg"),
        [".flac"] = new(FileCategory.Audio, "FLAC Audio",      "audio/flac"),
        [".aac"]  = new(FileCategory.Audio, "AAC Audio"),
        [".m4a"]  = new(FileCategory.Audio, "M4A Audio"),
        [".ogg"]  = new(FileCategory.Audio, "OGG Audio"),
        [".opus"] = new(FileCategory.Audio, "Opus Audio"),
        [".zip"]  = new(FileCategory.Archive, "ZIP Archive",   "application/zip"),
        [".tar"]  = new(FileCategory.Archive, "TAR Archive"),
        [".gz"]   = new(FileCategory.Archive, "GZip Archive",  "application/gzip"),
        [".bz2"]  = new(FileCategory.Archive, "BZip2 Archive"),
        [".xz"]   = new(FileCategory.Archive, "XZ Archive"),
        [".7z"]   = new(FileCategory.Archive, "7-Zip Archive"),
        [".rar"]  = new(FileCategory.Archive, "RAR Archive"),
        [".lzh"]  = new(FileCategory.Archive, "LZH Archive"),
        [".lha"]  = new(FileCategory.Archive, "LZH Archive"),
        [".exe"]  = new(FileCategory.Executable, "Executable"),
        [".dll"]  = new(FileCategory.Executable, "DLL Library"),
        [".sys"]  = new(FileCategory.Executable, "System Driver"),
        [".pdf"]  = new(FileCategory.Pdf, "PDF Document"),
        [".ttf"]  = new(FileCategory.Binary, "TrueType Font"),
        [".otf"]  = new(FileCategory.Binary, "OpenType Font"),
        [".woff"] = new(FileCategory.Binary, "Web Font"),
        [".woff2"]= new(FileCategory.Binary, "Web Font"),
    };

    public static FileType Detect(string path, ReadOnlySpan<byte> magic)
    {
        // Disambiguate RIFF before general table scan
        if (magic.Length >= 4 && magic[0]==0x52&&magic[1]==0x49&&magic[2]==0x46&&magic[3]==0x46)
        {
            if (magic.Length < 12) return new(FileCategory.Binary, "RIFF Data");
            if (magic[8]==0x41&&magic[9]==0x56&&magic[10]==0x49&&magic[11]==0x20)
                return new(FileCategory.Video, "AVI Video",  "video/x-msvideo");
            if (magic[8]==0x57&&magic[9]==0x41&&magic[10]==0x56&&magic[11]==0x45)
                return new(FileCategory.Audio, "WAV Audio",  "audio/wav");
            if (magic[8]==0x57&&magic[9]==0x45&&magic[10]==0x42&&magic[11]==0x50)
                return new(FileCategory.Image, "WebP Image", "image/webp");
            return new(FileCategory.Binary, "RIFF Data");
        }

        foreach (var (offset, sig, type) in _sigs)
        {
            if (magic.Length < offset + sig.Length) continue;
            bool match = true;
            for (int i = 0; i < sig.Length && match; i++)
                if (magic[offset + i] != sig[i]) match = false;
            if (!match) continue;

            // Refine MP4 ftyp: check brand at bytes 8-11
            if (offset == 4 && sig[0] == 0x66 && magic.Length >= 12)
            {
                if (magic[8]==0x71&&magic[9]==0x74&&magic[10]==0x20&&magic[11]==0x20)
                    return new(FileCategory.Video, "QuickTime Video", "video/quicktime");
                if (magic[8]==0x4D&&magic[9]==0x34&&magic[10]==0x41&&magic[11]==0x20)
                    return new(FileCategory.Audio, "M4A Audio", "audio/mp4");
            }

            // Refine MZ: use extension for dll/sys
            if (sig[0] == 0x4D && sig[1] == 0x5A)
            {
                string e = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (e == ".dll") return new(FileCategory.Executable, "DLL Library",    "application/x-msdownload");
                if (e == ".sys") return new(FileCategory.Executable, "System Driver");
            }

            return type;
        }

        string ext = System.IO.Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext) && _extMap.TryGetValue(ext, out var extType))
            return extType;

        // Heuristic: check for null bytes — text files have none
        if (magic.Length > 0)
        {
            int check = Math.Min(magic.Length, 512);
            bool hasNull = false;
            for (int i = 0; i < check; i++) if (magic[i] == 0) { hasNull = true; break; }
            if (!hasNull) return new(FileCategory.Text, "Text File", "text/plain");
        }

        return new(FileCategory.Binary, "Binary Data", "application/octet-stream");
    }
}

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
    public const string Blue   = "\x1b[34m";
    public const string Yellow = "\x1b[33m";
    public const string Reset  = "\x1b[0m";
    public static string Colorize(string text, string color) => color + text + Reset;
}

static class NavigationHelper
{
    public static void PageDown(Column c, int visibleHeight)
    {
        if (c.Entries.Count == 0) return;
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
    public string? RestoreTo;
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
    public readonly PreviewPane Preview = new();
    public SearchState Search = new();
}

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
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern IntPtr migemo_query(IntPtr h, byte[] query);

    [System.Runtime.InteropServices.DllImport("migemo",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern void migemo_release(IntPtr h, IntPtr result);

    IntPtr _handle = IntPtr.Zero;
    bool _disposed;

    public bool DllLoaded { get; private set; }   // true if migemo.dll was found
    public bool IsAvailable { get; private set; } // true if DLL + dict both loaded

    public MigemoProvider()
    {
        // Probe DLL availability before touching the dict
        if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("migemo", out _))
            return; // DLL not present — silent
        DllLoaded = true;

        string? dictFile = FindDictFile();
        if (dictFile == null) return; // DLL present but no dict found

        try
        {
            _handle = migemo_open(dictFile);
            IsAvailable = _handle != IntPtr.Zero;
        }
        catch
        {
            // migemo_open failed — silent
        }
    }

    static string? FindDictFile()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "dict", "utf-8", "migemo-dict"),
            Path.Combine(AppContext.BaseDirectory, "dict", "migemo-dict"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ColumnFileManager", "dict", "utf-8", "migemo-dict"),
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
            byte[] queryBytes = System.Text.Encoding.UTF8.GetBytes(romaji + '\0');
            IntPtr ptr = migemo_query(_handle, queryBytes);
            if (ptr == IntPtr.Zero) return romaji;
            try
            {
                string? result = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr);
                return result ?? romaji;
            }
            finally
            {
                migemo_release(_handle, ptr);
            }
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

    public static string BuildSearchStatusBar(SearchState search, List<int> matches, bool done, int width)
    {
        bool noMatch = matches.Count == 0 && search.Query.Length > 0 && done;

        // Left: /query▌
        string promptColor = noMatch ? "\x1b[31m" : "\x1b[33m";
        string reset = "\x1b[0m";
        string left = promptColor + "/" + reset + search.Query + "\x1b[7m \x1b[0m";
        int leftLen = 1 + search.Query.Length + 1; // '/' + query + cursor

        // Right: [regex]  (n/m*)
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
}

internal record PreviewContent(
    string    TypeLabel,
    string    InfoLine,
    string    Modified,
    string[]  BodyLines,
    bool      IsPartial,
    string?   ExtMismatch = null,  // set when magic-detected type ≠ extension type
    string[]? PixelLines  = null,  // pre-rendered ANSI half-block rows; bypasses SmartTruncate
    int       PixelWidth  = 0      // visual width of each pixel line (count of ▄ chars)
);

internal sealed class PreviewPane
{
    public bool IsVisible;
    public string? CurrentPath;
    public FileType? CurrentType;
    public PreviewContent? Content;
    public bool IsLoading;
    public CancellationTokenSource? Cts;

    public void Cancel()
    {
        Cts?.Cancel();
        Cts?.Dispose();
        Cts = null;
        IsLoading = false;
    }
}

internal static class PreviewLoader
{
    // ── Extension-mismatch detection ──────────────────────────────────────

    internal static string? CheckExtensionMismatch(string filePath, FileType detected)
    {
        if (detected.Category == FileCategory.Binary || detected.Category == FileCategory.Text)
            return null; // too ambiguous to warn on

        string ext = System.IO.Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;

        // Try extension-only detection (pass empty magic so only ext map is used)
        var extOnly = FileTypeDetector.Detect(filePath, ReadOnlySpan<byte>.Empty);
        if (extOnly.Category == FileCategory.Binary) return null; // extension unknown

        if (extOnly.Category != detected.Category || extOnly.Label != detected.Label)
            return $"⚠ {ext} extension but detected as {detected.Label}";
        return null;
    }

    // ── Public dispatch ────────────────────────────────────────────────────

    public static async Task<PreviewContent> LoadAsync(
        string filePath, bool isDrive, FileType fileType,
        int width, int height, CancellationToken ct)
    {
        string modified = GetModified(filePath, isDrive);
        int bodyHeight = Math.Max(1, height - 5); // header + info + date + divider + status rows

        try
        {
            if (isDrive)
                return LoadDrive(filePath, width, bodyHeight);

            string? mismatch = CheckExtensionMismatch(filePath, fileType);

            var content = fileType.Category switch
            {
                FileCategory.Text      => await LoadTextAsync(filePath, fileType, modified, width, bodyHeight, ct),
                FileCategory.Image     => await LoadImagePreviewAsync(filePath, fileType, modified, width, bodyHeight, ct),
                FileCategory.Video     => await LoadVideoPreviewAsync(filePath, fileType, modified, width, bodyHeight, ct),
                FileCategory.Audio     => LoadAudioMeta(filePath, fileType, modified, width),
                FileCategory.Archive   => await LoadArchiveAsync(filePath, fileType, modified, width, bodyHeight, ct),
                FileCategory.Executable=> LoadExecutable(filePath, fileType, modified, width),
                FileCategory.Pdf       => LoadPdf(filePath, fileType, modified, width),
                _                      => await LoadBinaryAsync(filePath, fileType, modified, width, bodyHeight, ct),
            };

            // If type-specific loader produced no body content, fall back to hex dump
            if (content.BodyLines.Length == 0 && content.PixelLines is null)
            {
                var hex = await LoadBinaryAsync(filePath, fileType, modified, width, bodyHeight, ct);
                content = content with { BodyLines = hex.BodyLines };
            }

            return mismatch != null ? content with { ExtMismatch = mismatch } : content;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new PreviewContent(fileType.Label, ex.Message, modified, [], false);
        }
    }

    // ── Hex helpers (internal for tests) ───────────────────────────────────

    internal static int CalcBytesPerRow(int previewWidth)
    {
        // Layout per row: 8 (addr) + 2 (gap) + N*3 (hex) + 1 (gap) + N (ascii) + 4 (margin) = 15 + 4N
        int n = (previewWidth - 15) / 4;
        return Math.Clamp(n, 4, 16);
    }

    internal static string[] FormatHexLines(byte[] data, int bytesPerRow, int previewWidth)
    {
        int rowCount = (data.Length + bytesPerRow - 1) / bytesPerRow;
        string[] lines = new string[rowCount];
        var sb = new System.Text.StringBuilder(previewWidth + 4);

        for (int row = 0; row < rowCount; row++)
        {
            sb.Clear();
            int offset = row * bytesPerRow;
            sb.Append(offset.ToString("X8"));
            sb.Append("  ");

            // Hex part
            for (int i = 0; i < bytesPerRow; i++)
            {
                if (offset + i < data.Length)
                    sb.Append(data[offset + i].ToString("X2")).Append(' ');
                else
                    sb.Append("   ");
            }

            sb.Append(' ');

            // ASCII part
            for (int i = 0; i < bytesPerRow; i++)
            {
                if (offset + i >= data.Length) break;
                byte b = data[offset + i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }

            lines[row] = sb.ToString();
        }
        return lines;
    }

    // ── Text ───────────────────────────────────────────────────────────────

    private static async Task<PreviewContent> LoadTextAsync(
        string filePath, FileType fileType, string modified,
        int width, int bodyHeight, CancellationToken ct)
    {
        string encoding = "UTF-8";
        int lineCount = 0;
        var bodyLines = new List<string>(bodyHeight + 1);

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);

        // Detect encoding from BOM
        byte[] bom = new byte[4];
        int bomRead = await fs.ReadAsync(bom, ct);
        if (bomRead >= 3 && bom[0]==0xEF && bom[1]==0xBB && bom[2]==0xBF) encoding = "UTF-8 BOM";
        else if (bomRead >= 2 && bom[0]==0xFF && bom[1]==0xFE) encoding = "UTF-16 LE";
        else if (bomRead >= 2 && bom[0]==0xFE && bom[1]==0xFF) encoding = "UTF-16 BE";
        fs.Seek(0, SeekOrigin.Begin);

        using var reader = new StreamReader(fs, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        const int maxCountLines = 10_000;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();
            lineCount++;
            if (bodyLines.Count < bodyHeight)
                bodyLines.Add(CharacterWidth.SmartTruncate(line, width - 1));
            if (lineCount >= maxCountLines && bodyLines.Count >= bodyHeight)
                break;
        }

        string lineInfo = lineCount >= maxCountLines ? $"> {maxCountLines} lines" : $"{lineCount} lines";
        string infoLine = $"{encoding} · {lineInfo} · {FormatSize(new FileInfo(filePath).Length)}";
        return new PreviewContent(fileType.Label, infoLine, modified, [.. bodyLines], false);
    }

    // ── Binary / hex fallback ──────────────────────────────────────────────

    private static async Task<PreviewContent> LoadBinaryAsync(
        string filePath, FileType fileType, string modified,
        int width, int bodyHeight, CancellationToken ct)
    {
        int bytesPerRow = CalcBytesPerRow(width);
        int bytesToRead = Math.Min(bytesPerRow * bodyHeight, 4096);

        byte[] buf = new byte[bytesToRead];
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        int read = await fs.ReadAsync(buf.AsMemory(0, bytesToRead), ct);

        byte[] data = buf[..read];
        string[] hexLines = FormatHexLines(data, bytesPerRow, width);

        long size = new FileInfo(filePath).Length;
        string infoLine = FormatSize(size);
        return new PreviewContent(fileType.Label, infoLine, modified, hexLines, false);
    }

    // ── Shared utilities ───────────────────────────────────────────────────

    private static string GetModified(string filePath, bool isDrive)
    {
        if (isDrive) return "";
        try { return File.GetLastWriteTime(filePath).ToString("yyyy-MM-dd HH:mm"); }
        catch { return ""; }
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private static byte[] ReadHeader(string filePath, int bytes)
    {
        using var fs = File.OpenRead(filePath);
        byte[] buf = new byte[Math.Min((int)fs.Length, bytes)];
        fs.Read(buf, 0, buf.Length);
        return buf;
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0
            ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    // ── Audio metadata ────────────────────────────────────────────────────
    private static PreviewContent LoadAudioMeta(string filePath, FileType fileType, string modified, int width)
    {
        try
        {
            byte[] header = ReadHeader(filePath, 512);
            string info = fileType.Label switch
            {
                "WAV Audio"  => ParseWavInfo(header, filePath),
                "FLAC Audio" => ParseFlacInfo(header),
                "MP3 Audio"  => ParseMp3Info(header, filePath),
                _ => FormatSize(new FileInfo(filePath).Length)
            };
            return new PreviewContent(fileType.Label, info, modified, [], false);
        }
        catch { return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false); }
    }

    private static string ParseWavInfo(byte[] h, string filePath)
    {
        // RIFF/WAVE: read fmt chunk dynamically to find data offset
        // Standard RIFF header: "RIFF"(0) size(4) "WAVE"(8)
        // offset 12 = "fmt "(ASCII) or other chunks
        // offset 16 = fmt chunk size (LE32), then the fmt data follows
        if (h.Length < 20) return FormatSize(new FileInfo(filePath).Length);

        // Try to read fmt chunk size from standard offset (WAVE files often have fmt at 12)
        int fmtChunkSize = h[16]|(h[17]<<8)|(h[18]<<16)|(h[19]<<24);
        int dataOffset = 20 + fmtChunkSize + 8; // skip fmt data + "data" tag (4) + chunk size (4)

        if (h.Length < dataOffset + 4) return FormatSize(new FileInfo(filePath).Length);

        // Extract fmt parameters from the fmt chunk (always at offset 20 after the size field)
        int channels      = h[22]|(h[23]<<8);
        int sampleRate    = h[24]|(h[25]<<8)|(h[26]<<16)|(h[27]<<24);
        int byteRate      = h[28]|(h[29]<<8)|(h[30]<<16)|(h[31]<<24);
        int bitsPerSample = h[34]|(h[35]<<8);

        long dataBytes    = h[dataOffset]|((long)h[dataOffset+1]<<8)|((long)h[dataOffset+2]<<16)|((long)h[dataOffset+3]<<24);
        double seconds    = byteRate > 0 ? (double)dataBytes / byteRate : 0;
        string dur = FormatDuration(seconds);
        string ch  = channels == 1 ? "Mono" : channels == 2 ? "Stereo" : $"{channels}ch";
        return $"{dur} · {sampleRate / 1000.0:F1} kHz · {ch} · {bitsPerSample}-bit";
    }

    private static string ParseFlacInfo(byte[] h)
    {
        // STREAMINFO block: after 4-byte "fLaC" marker
        // byte 4 = block type (0 = STREAMINFO), bytes 5-7 = length
        // bytes 18-21: sample rate (20 bits BE), channels (3 bits), bitsPerSample-1 (5 bits)
        // bytes 21-25: total samples (36 bits)
        if (h.Length < 38) return "";
        int sampleRate = (h[18]<<12)|(h[19]<<4)|(h[20]>>4);
        int channels   = ((h[20]>>1) & 0x07) + 1;
        int bits       = (((h[20]&0x01)<<4)|(h[21]>>4)) + 1;
        long samples   = ((long)(h[21]&0x0F)<<32)|((long)h[22]<<24)|((long)h[23]<<16)|((long)h[24]<<8)|h[25];
        double seconds = sampleRate > 0 ? (double)samples / sampleRate : 0;
        string dur = FormatDuration(seconds);
        string ch  = channels == 1 ? "Mono" : channels == 2 ? "Stereo" : $"{channels}ch";
        return $"{dur} · {sampleRate / 1000.0:F1} kHz · {ch} · {bits}-bit";
    }

    private static string ParseMp3Info(byte[] h, string filePath)
    {
        long fileSize = new FileInfo(filePath).Length;
        int bitrate = 0;
        for (int i = 0; i < h.Length - 3; i++)
        {
            if (h[i] != 0xFF || (h[i+1] & 0xE0) != 0xE0) continue;
            int b3 = h[i+2];
            int brIdx = (b3 >> 4) & 0x0F;
            int[] table = [0,32,40,48,56,64,80,96,112,128,160,192,224,256,320,0];
            bitrate = table[brIdx] * 1000;
            break;
        }
        double seconds = bitrate > 0 ? (fileSize * 8.0) / bitrate : 0;
        string dur = seconds > 0 ? FormatDuration(seconds) : "unknown duration";
        return bitrate > 0 ? $"{dur} · {bitrate/1000} kbps" : $"{dur} · {FormatSize(fileSize)}";
    }

    // ── Video metadata ────────────────────────────────────────────────────
    private static PreviewContent LoadVideoMeta(string filePath, FileType fileType, string modified, int width)
    {
        try
        {
            string info = fileType.Label switch
            {
                "MP4 Video" or "QuickTime Video" or "M4A Audio" => ParseMp4Info(filePath),
                "AVI Video" => ParseAviInfo(filePath),
                _ => FormatSize(new FileInfo(filePath).Length)
            };
            return new PreviewContent(fileType.Label, info, modified, [], false);
        }
        catch { return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false); }
    }

    internal static string GetVideoCachePath(string filePath)
    {
        var fi = new FileInfo(filePath);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(filePath));
        string prefix = Convert.ToHexString(hash[..16]).ToLowerInvariant();
        string cacheDir = Path.Combine(Path.GetTempPath(), "ColumnFileManager");
        Directory.CreateDirectory(cacheDir);
        return Path.Combine(cacheDir, $"{prefix}_{fi.LastWriteTimeUtc.Ticks:x16}_{fi.Length:x16}.png");
    }

    private static async Task<bool> ExtractVideoFrameAsync(string filePath, string cachePath, CancellationToken ct)
    {
        string tempFile = cachePath + ".tmp";
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute       = false,
                CreateNoWindow        = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-ss");       psi.ArgumentList.Add("00:00:05");
            psi.ArgumentList.Add("-i");        psi.ArgumentList.Add(filePath);
            psi.ArgumentList.Add("-vframes");  psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-vf");       psi.ArgumentList.Add("scale=63:200:force_original_aspect_ratio=decrease");
            psi.ArgumentList.Add("-f");        psi.ArgumentList.Add("image2");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-y");        psi.ArgumentList.Add(tempFile);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            process = Process.Start(psi);
            if (process is null) return false;
            await process.WaitForExitAsync(linkedCts.Token);

            if (process.ExitCode != 0 || !File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                return false;

            File.Move(tempFile, cachePath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return false; }
        catch (Win32Exception) { return false; }
        catch { return false; }
        finally
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    internal static async Task<PreviewContent> LoadVideoPreviewAsync(
        string filePath, FileType fileType, string modified,
        int width, int bodyHeight, CancellationToken ct)
    {
        var meta = LoadVideoMeta(filePath, fileType, modified, width);

        try
        {
            ct.ThrowIfCancellationRequested();

            int pixelCols = width - 1;
            int pixelRows = (bodyHeight - 1) * 2;
            if (pixelCols <= 0 || pixelRows <= 0) return meta;

            string cachePath = GetVideoCachePath(filePath);

            if (!File.Exists(cachePath))
            {
                bool extracted = await ExtractVideoFrameAsync(filePath, cachePath, ct);
                if (!extracted) return meta;
            }

            var (pixelLines, pixelWidth) = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var image = Image.Load<Rgba32>(cachePath);
                ct.ThrowIfCancellationRequested();

                int srcW = image.Width;
                int srcH = image.Height;
                double scaleX = pixelCols > 0 ? (double)pixelCols / srcW : 1.0;
                double scaleY = pixelRows > 0 ? (double)pixelRows / srcH : 1.0;
                double scale  = Math.Min(scaleX, scaleY);
                int tgtW = Math.Max(1, (int)(srcW * scale));
                int tgtH = Math.Max(2, (int)(srcH * scale));
                if (tgtH % 2 != 0) tgtH--;

                image.Mutate(ctx => ctx.Resize(tgtW, tgtH));
                string[] lines = RenderPixelLines(image, tgtW, tgtH);
                return (lines, tgtW);
            }, ct);

            ct.ThrowIfCancellationRequested();
            return meta with { PixelLines = pixelLines, PixelWidth = pixelWidth };
        }
        catch (OperationCanceledException) { throw; }
        catch { return meta; }
    }

    private static string ParseMp4Info(string filePath)
    {
        long fileSize = new FileInfo(filePath).Length;
        double duration = 0;
        int vidW = 0, vidH = 0;
        using var fs = File.OpenRead(filePath);
        WalkMp4Boxes(fs, fs.Length, ref duration, ref vidW, ref vidH, 0);
        var parts = new List<string>();
        if (vidW > 0 && vidH > 0) parts.Add($"{vidW} × {vidH}");
        if (duration > 0) parts.Add(FormatDuration(duration));
        parts.Add(FormatSize(fileSize));
        return string.Join(" · ", parts);
    }

    private static void WalkMp4Boxes(Stream s, long limit, ref double duration, ref int w, ref int h, int depth)
    {
        if (depth > 6) return;
        byte[] buf = new byte[8];
        while (s.Position < limit - 8)
        {
            long boxStart = s.Position;
            if (s.Read(buf, 0, 8) < 8) return;
            long size = (long)(uint)((buf[0]<<24)|(buf[1]<<16)|(buf[2]<<8)|buf[3]);
            string type = System.Text.Encoding.ASCII.GetString(buf, 4, 4);
            if (size == 1)
            {
                byte[] ext = new byte[8];
                s.Read(ext, 0, 8);
                size = (long)(((ulong)ext[0]<<56)|((ulong)ext[1]<<48)|((ulong)ext[2]<<40)|((ulong)ext[3]<<32)
                            | ((ulong)ext[4]<<24)|((ulong)ext[5]<<16)|((ulong)ext[6]<<8)|(ulong)ext[7]);
            }
            if (size < 8) return;
            long contentStart = s.Position;
            long boxEnd = boxStart + size;

            if (type == "moov" || type == "trak")
            {
                WalkMp4Boxes(s, boxEnd, ref duration, ref w, ref h, depth + 1);
                s.Seek(boxEnd, SeekOrigin.Begin);
                continue;
            }
            if (type == "mvhd" && duration == 0)
            {
                byte[] mvhd = new byte[Math.Min(28, (int)(boxEnd - contentStart))];
                s.Read(mvhd, 0, mvhd.Length);
                int ver = mvhd[0];
                long ts, dur;
                if (ver == 1)
                {
                    ts  = (long)(((ulong)mvhd[4]<<56)|((ulong)mvhd[5]<<48)|((ulong)mvhd[6]<<40)|((ulong)mvhd[7]<<32)
                               | ((ulong)mvhd[8]<<24)|((ulong)mvhd[9]<<16)|((ulong)mvhd[10]<<8)|(ulong)mvhd[11]);
                    dur = (long)(((ulong)mvhd[12]<<56)|((ulong)mvhd[13]<<48)|((ulong)mvhd[14]<<40)|((ulong)mvhd[15]<<32)
                               | ((ulong)mvhd[16]<<24)|((ulong)mvhd[17]<<16)|((ulong)mvhd[18]<<8)|(ulong)mvhd[19]);
                }
                else
                {
                    ts  = (uint)((uint)mvhd[4]<<24|(uint)mvhd[5]<<16|(uint)mvhd[6]<<8|(uint)mvhd[7]);
                    dur = (uint)((uint)mvhd[8]<<24|(uint)mvhd[9]<<16|(uint)mvhd[10]<<8|(uint)mvhd[11]);
                }
                if (ts > 0) duration = (double)dur / ts;
            }
            if (type == "tkhd" && w == 0)
            {
                byte[] tkhd = new byte[Math.Min(100, (int)(boxEnd - contentStart))];
                s.Read(tkhd, 0, tkhd.Length);
                int off = tkhd[0] == 1 ? 92 : 76;
                if (tkhd.Length >= off + 8)
                {
                    w = (tkhd[off]<<8)|tkhd[off+1];
                    h = (tkhd[off+4]<<8)|tkhd[off+5];
                }
            }
            s.Seek(boxEnd, SeekOrigin.Begin);
        }
    }

    private static string ParseAviInfo(string filePath)
    {
        // AVI layout: RIFF(0) filesize(4) "AVI "(8) LIST(12) listsize(16) "hdrl"(20)
        //   "avih"(24) chunksize(28)  avih-data(32):
        //   usPerFrame(32) maxBytesPerSec(36) padding(40) flags(44) totalFrames(48)
        //   initialFrames(52) streams(56) bufferSize(60) width(64) height(68)
        long fileSize = new FileInfo(filePath).Length;
        byte[] h = ReadHeader(filePath, 72);
        if (h.Length < 72) return FormatSize(fileSize);
        if (h[24]!=0x61||h[25]!=0x76||h[26]!=0x69||h[27]!=0x68) return FormatSize(fileSize); // "avih"
        long usPerFrame  = (long)h[32]|((long)h[33]<<8)|((long)h[34]<<16)|((long)h[35]<<24);
        long totalFrames = (long)h[48]|((long)h[49]<<8)|((long)h[50]<<16)|((long)h[51]<<24);
        int  vidW        = h[64]|(h[65]<<8)|(h[66]<<16)|(h[67]<<24);
        int  vidH        = h[68]|(h[69]<<8)|(h[70]<<16)|(h[71]<<24);
        double fps     = usPerFrame > 0 ? 1_000_000.0 / usPerFrame : 0;
        double seconds = fps > 0 && totalFrames > 0 ? totalFrames / fps : 0;
        var parts = new List<string>();
        if (vidW > 0 && vidH > 0) parts.Add($"{vidW} × {vidH}");
        if (seconds > 0) parts.Add(FormatDuration(seconds));
        parts.Add(FormatSize(fileSize));
        return string.Join(" · ", parts);
    }

    // ── Archive metadata ──────────────────────────────────────────────────
    private static async Task<PreviewContent> LoadArchiveAsync(
        string filePath, FileType fileType, string modified,
        int width, int bodyHeight, CancellationToken ct)
    {
        try
        {
            long size = new FileInfo(filePath).Length;
            switch (fileType.Label)
            {
                case "ZIP Archive":
                    return await LoadZipAsync(filePath, fileType, modified, size, width, bodyHeight, ct);
                case "GZip Archive":
                {
                    string orig = ReadGzipOriginalName(filePath);
                    string info = string.IsNullOrEmpty(orig)
                        ? FormatSize(size)
                        : $"{orig} · {FormatSize(size)}";
                    return new PreviewContent(fileType.Label, info, modified, [], false);
                }
                default:
                    return new PreviewContent(fileType.Label, FormatSize(size), modified, [], false);
            }
        }
        catch { return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false); }
    }

    private static async Task<PreviewContent> LoadZipAsync(
        string filePath, FileType fileType, string modified,
        long size, int width, int bodyHeight, CancellationToken ct)
    {
        await Task.Yield();
        using var za = System.IO.Compression.ZipFile.OpenRead(filePath);
        int total = za.Entries.Count;
        var body = new List<string>(Math.Min(total, bodyHeight));
        foreach (var entry in za.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (body.Count >= bodyHeight) break;
            string name = CharacterWidth.SmartTruncate(entry.FullName, width - 12);
            string entSize = FormatSize(entry.Length).PadLeft(9);
            body.Add($"  {name.PadRight(width - 13)}{entSize}");
        }
        string info = $"{total} entries · {FormatSize(size)}";
        return new PreviewContent(fileType.Label, info, modified, [.. body], false);
    }

    private static string ReadGzipOriginalName(string filePath)
    {
        byte[] h = ReadHeader(filePath, 256);
        if (h.Length < 10) return "";
        byte flags = h[3];
        if ((flags & 0x08) == 0) return "";
        int pos = 10;
        if ((flags & 0x04) != 0) { if (pos + 2 > h.Length) return ""; int xlen = h[pos]|(h[pos+1]<<8); pos += 2 + xlen; }
        var name = new System.Text.StringBuilder();
        while (pos < h.Length && h[pos] != 0) { name.Append((char)h[pos]); pos++; }
        return name.ToString();
    }

    // ── Drive info ────────────────────────────────────────────────────────
    private static PreviewContent LoadDrive(string drivePath, int width, int bodyHeight)
    {
        var di = new DriveInfo(drivePath);
        if (!di.IsReady)
            return new PreviewContent("Drive", "Not ready", "", [], false);

        long total = di.TotalSize;
        long free  = di.AvailableFreeSpace;
        long used  = total - free;
        double pct = total > 0 ? (double)used / total : 0;

        int barWidth = Math.Max(4, width - 8);
        int filled = Math.Min((int)(pct * barWidth), barWidth);
        string bar = new string('█', filled) + new string('░', barWidth - filled);

        string driveType = di.DriveType switch
        {
            DriveType.Fixed      => "Fixed Drive",
            DriveType.Removable  => "Removable Drive",
            DriveType.Network    => "Network Drive",
            DriveType.CDRom      => "Optical Drive",
            DriveType.Ram        => "RAM Disk",
            _ => "Drive"
        };

        var body = new List<string>
        {
            $"Label:      {(string.IsNullOrEmpty(di.VolumeLabel) ? "(none)" : di.VolumeLabel)}",
            $"Filesystem: {di.DriveFormat}",
            $"Total:      {FormatSize(total)}",
            $"Used:       {FormatSize(used)} ({pct:P0})",
            $"Free:       {FormatSize(free)}",
            "",
            bar + $" {pct:P0}"
        };

        string typeLabel = $"{di.DriveFormat} · {driveType}";
        return new PreviewContent(typeLabel, "", "", [.. body], false);
    }

    // ── Image metadata ────────────────────────────────────────────────────
    private static PreviewContent LoadImageMeta(string filePath, FileType fileType, string modified, int width)
    {
        try
        {
            byte[] header = ReadHeader(filePath, 64);
            string dims = "unknown dimensions";
            string extra = "";

            switch (fileType.Label)
            {
                case "PNG Image":
                {
                    var (w, h, bits, color) = ParsePngHeader(header);
                    dims = $"{w} × {h} px";
                    extra = $"{bits}-bit {color}";
                    break;
                }
                case "JPEG Image":
                {
                    var (w, h) = ParseJpegDimensions(filePath);
                    dims = $"{w} × {h} px";
                    break;
                }
                case "GIF Image":
                {
                    var (w, h) = ParseGifHeader(header);
                    dims = $"{w} × {h} px";
                    break;
                }
                case "BMP Image":
                {
                    var (w, h) = ParseBmpHeader(header);
                    dims = $"{w} × {h} px";
                    break;
                }
                case "WebP Image":
                {
                    var (w, h) = ParseWebpHeader(header);
                    dims = $"{w} × {h} px";
                    break;
                }
                case "SVG Image":
                {
                    var (w, h) = ParseSvgDimensions(filePath);
                    dims = string.IsNullOrEmpty(w) ? "vector (no size)" : $"{w} × {h}";
                    break;
                }
            }

            long size = new FileInfo(filePath).Length;
            string info = string.IsNullOrEmpty(extra)
                ? $"{dims} · {FormatSize(size)}"
                : $"{dims} · {extra} · {FormatSize(size)}";
            return new PreviewContent(fileType.Label, info, modified, [], false);
        }
        catch { return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false); }
    }

    // ── Image pixel rendering ─────────────────────────────────────────────
    internal static string[] RenderPixelLines(Image<Rgba32> image, int tgtW, int tgtH)
    {
        // image is already resized to tgtW × tgtH by the caller
        int termRows = tgtH / 2;
        string[] lines = new string[termRows];
        var sb = new StringBuilder(tgtW * 42);

        for (int row = 0; row < termRows; row++)
        {
            sb.Clear();
            int upperY = row * 2;
            int lowerY = upperY + 1;

            for (int x = 0; x < tgtW; x++)
            {
                Rgba32 up = image[x, upperY];
                Rgba32 lo = image[x, lowerY];

                // Alpha-blend against black background
                byte upR = (byte)(up.R * up.A / 255);
                byte upG = (byte)(up.G * up.A / 255);
                byte upB = (byte)(up.B * up.A / 255);
                byte loR = (byte)(lo.R * lo.A / 255);
                byte loG = (byte)(lo.G * lo.A / 255);
                byte loB = (byte)(lo.B * lo.A / 255);

                // Upper pixel row → ANSI background; lower → foreground; char = ▄
                sb.Append("\x1b[48;2;");
                sb.Append(upR); sb.Append(';');
                sb.Append(upG); sb.Append(';');
                sb.Append(upB); sb.Append('m');
                sb.Append("\x1b[38;2;");
                sb.Append(loR); sb.Append(';');
                sb.Append(loG); sb.Append(';');
                sb.Append(loB); sb.Append('m');
                sb.Append('▄');
            }

            sb.Append("\x1b[0m");
            lines[row] = sb.ToString();
        }

        return lines;
    }

    internal static async Task<PreviewContent> LoadImagePreviewAsync(
        string filePath, FileType fileType, string modified, int width, int bodyHeight, CancellationToken ct)
    {
        // Always get metadata first — InfoLine (dimensions + size) comes from LoadImageMeta
        var meta = LoadImageMeta(filePath, fileType, modified, width);

        // SVG and ICO cannot be rasterized by ImageSharp (no bundled rasterizer)
        if (fileType.Label == "SVG Image" || fileType.Label == "Icon")
            return meta;

        try
        {
            ct.ThrowIfCancellationRequested();

            // pixel columns = previewWidth - 1  (separator takes 1 col)
            // pixel rows    = (bodyHeight-1) * 2  (DrawPreviewToLines draws bodyHeight-1 body rows)
            int pixelCols = width - 1;
            int pixelRows = (bodyHeight - 1) * 2;

            // ImageSharp is CPU-bound; run on thread pool to stay async
            var (pixelLines, pixelWidth) = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var image = Image.Load<Rgba32>(filePath);
                ct.ThrowIfCancellationRequested();

                int srcW = image.Width;
                int srcH = image.Height;
                double scaleX = pixelCols > 0 ? (double)pixelCols / srcW : 1.0;
                double scaleY = pixelRows > 0 ? (double)pixelRows / srcH : 1.0;
                double scale  = Math.Min(scaleX, scaleY);
                int tgtW = Math.Max(1, (int)(srcW * scale));
                int tgtH = Math.Max(2, (int)(srcH * scale));
                if (tgtH % 2 != 0) tgtH--;

                image.Mutate(ctx => ctx.Resize(tgtW, tgtH));
                string[] lines = RenderPixelLines(image, tgtW, tgtH);
                return (lines, tgtW);
            }, ct);

            ct.ThrowIfCancellationRequested();
            return meta with { PixelLines = pixelLines, PixelWidth = pixelWidth };
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Corrupted file, unsupported sub-format, etc. → fall back to metadata only
            return meta;
        }
    }

    internal static (int W, int H, int Bits, string Color) ParsePngHeader(byte[] h)
    {
        if (h.Length < 26) return (0, 0, 0, "");
        int w     = (h[16]<<24)|(h[17]<<16)|(h[18]<<8)|h[19];
        int ht    = (h[20]<<24)|(h[21]<<16)|(h[22]<<8)|h[23];
        int bits  = h[24];
        string color = h[25] switch { 0=>"Grayscale", 2=>"RGB", 3=>"Indexed", 4=>"Grayscale+A", 6=>"RGBA", _=>"?" };
        return (w, ht, bits, color);
    }

    internal static (int W, int H) ParseGifHeader(byte[] h)
    {
        if (h.Length < 10) return (0, 0);
        int w = h[6] | (h[7] << 8);
        int ht = h[8] | (h[9] << 8);
        return (w, ht);
    }

    internal static (int W, int H) ParseBmpHeader(byte[] h)
    {
        if (h.Length < 26) return (0, 0);
        int w  = h[18]|(h[19]<<8)|(h[20]<<16)|(h[21]<<24);
        int ht = h[22]|(h[23]<<8)|(h[24]<<16)|(h[25]<<24);
        return (w, Math.Abs(ht));
    }

    private static (int W, int H) ParseWebpHeader(byte[] h)
    {
        // VP8 : bytes 26-27 = width-1, 28-29 = height-1 (14-bit LE)
        if (h.Length < 30) return (0, 0);
        if (h[12]==0x56&&h[13]==0x50&&h[14]==0x38&&h[15]==0x20) // "VP8 "
        {
            int w = 1 + ((h[26]|(h[27]<<8)) & 0x3FFF);
            int ht= 1 + ((h[28]|(h[29]<<8)) & 0x3FFF);
            return (w, ht);
        }
        return (0, 0);
    }

    private static (int W, int H) ParseJpegDimensions(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        byte[] buf = new byte[2];
        if (fs.Read(buf, 0, 2) < 2 || buf[0] != 0xFF || buf[1] != 0xD8) return (0, 0);
        while (fs.Position < fs.Length - 9)
        {
            if (fs.ReadByte() != 0xFF) continue;
            int marker = fs.ReadByte();
            bool isSof = marker == 0xC0 || marker == 0xC1 || marker == 0xC2
                       || marker == 0xC9 || marker == 0xCA;
            if (fs.Read(buf, 0, 2) < 2) break;
            int segLen = (buf[0] << 8) | buf[1];
            if (isSof)
            {
                byte[] sof = new byte[5];
                if (fs.Read(sof, 0, 5) < 5) break;
                int h = (sof[1] << 8) | sof[2];
                int w = (sof[3] << 8) | sof[4];
                return (w, h);
            }
            fs.Seek(segLen - 2, SeekOrigin.Current);
        }
        return (0, 0);
    }

    private static (string W, string H) ParseSvgDimensions(string filePath)
    {
        try
        {
            byte[] buf = new byte[1024];
            using var fs = File.OpenRead(filePath);
            int read = fs.Read(buf, 0, buf.Length);
            string text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
            string w = ExtractAttr(text, "width");
            string h = ExtractAttr(text, "height");
            return (w, h);
        }
        catch { return ("", ""); }
    }

    private static string ExtractAttr(string text, string attr)
    {
        int idx = text.IndexOf(attr + "=\"", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        int start = idx + attr.Length + 2;
        int end = text.IndexOf('"', start);
        return end > start ? text[start..end] : "";
    }

    // ── PDF ───────────────────────────────────────────────────────────────
    private static PreviewContent LoadPdf(string filePath, FileType fileType, string modified, int width)
    {
        byte[] header = ReadHeader(filePath, 512);
        string version = ParsePdfVersion(header);
        int pages = CountPdfPages(filePath);
        string title = ReadPdfInfoField(filePath, "Title");
        string author = ReadPdfInfoField(filePath, "Author");

        var body = new List<string>();
        if (!string.IsNullOrEmpty(title))  body.Add(CharacterWidth.SmartTruncate($"Title:  {title}", width - 1));
        if (!string.IsNullOrEmpty(author)) body.Add(CharacterWidth.SmartTruncate($"Author: {author}", width - 1));

        long size = new FileInfo(filePath).Length;
        string pageStr = pages > 0 ? $"{pages} pages" : "? pages";
        string info = $"PDF {version} · {pageStr} · {FormatSize(size)}";
        return new PreviewContent(fileType.Label, info, modified, [.. body], false);
    }

    internal static string ParsePdfVersion(byte[] header)
    {
        string text = System.Text.Encoding.ASCII.GetString(header, 0, Math.Min(header.Length, 16));
        int idx = text.IndexOf("%PDF-", StringComparison.Ordinal);
        if (idx < 0) return "?";
        int start = idx + 5;
        int end = start;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
        return end > start ? text[start..end] : "?";
    }

    private static int CountPdfPages(string filePath)
    {
        try
        {
            long length = new FileInfo(filePath).Length;
            int scanSize = (int)Math.Min(65536, length);
            byte[] buf = new byte[scanSize];
            using var fs = File.OpenRead(filePath);
            fs.Seek(-scanSize, SeekOrigin.End);
            fs.Read(buf, 0, scanSize);
            string tail = System.Text.Encoding.Latin1.GetString(buf);
            int best = 0, idx = 0;
            while ((idx = tail.IndexOf("/Count ", idx, StringComparison.Ordinal)) >= 0)
            {
                idx += 7;
                int end = idx;
                while (end < tail.Length && char.IsDigit(tail[end])) end++;
                if (end > idx && int.TryParse(tail[idx..end], out int n) && n > best) best = n;
            }
            return best;
        }
        catch { return 0; }
    }

    private static string ReadPdfInfoField(string filePath, string fieldName)
    {
        try
        {
            long length = new FileInfo(filePath).Length;
            int scanSize = (int)Math.Min(131072, length);
            byte[] buf = new byte[scanSize];
            using var fs = File.OpenRead(filePath);
            fs.Seek(-scanSize, SeekOrigin.End);
            fs.Read(buf, 0, scanSize);
            string tail = System.Text.Encoding.Latin1.GetString(buf);
            string key = "/" + fieldName + " (";
            int idx = tail.LastIndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return "";
            int start = idx + key.Length;
            int end = tail.IndexOf(')', start);
            return end > start ? tail[start..end].Trim() : "";
        }
        catch { return ""; }
    }

    // ── PE Executable ─────────────────────────────────────────────────────
    private static PreviewContent LoadExecutable(string filePath, FileType fileType, string modified, int width)
    {
        try
        {
            byte[] header = ReadHeader(filePath, 512);
            if (header.Length < 64 || header[0] != 0x4D || header[1] != 0x5A)
                return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false);

            int peOffset = header[60]|(header[61]<<8)|(header[62]<<16)|(header[63]<<24);
            if (peOffset + 24 >= header.Length)
                header = ReadHeader(filePath, peOffset + 256);
            if (peOffset + 24 >= header.Length)
                return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false);

            if (header[peOffset]!=0x50||header[peOffset+1]!=0x45)
                return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false);

            int machine = header[peOffset+4]|(header[peOffset+5]<<8);
            string arch = machine switch
            {
                0x014C => "x86",
                0x8664 => "x86-64",
                0xAA64 => "ARM64",
                0x01C4 => "ARM",
                _ => $"0x{machine:X4}"
            };

            int optMagic = header[peOffset+24]|(header[peOffset+25]<<8);
            int subOffset = peOffset + 92;
            int subsystem = subOffset + 1 < header.Length
                ? header[subOffset]|(header[subOffset+1]<<8) : 0;
            string sub = subsystem switch
            {
                2 => "GUI",
                3 => "Console",
                1 => "Native/Driver",
                _ => $"sub={subsystem}"
            };

            bool isDotNet = false;
            int ddOffset = optMagic == 0x20B ? peOffset + 136 : peOffset + 120;
            int clrDirOffset = ddOffset + 14 * 8;
            if (clrDirOffset + 4 < header.Length)
                isDotNet = (header[clrDirOffset]|(header[clrDirOffset+1]<<8)|(header[clrDirOffset+2]<<16)|(header[clrDirOffset+3]<<24)) != 0;

            long size = new FileInfo(filePath).Length;
            string dotnet = isDotNet ? " · .NET" : "";
            string info = $"{arch} · {sub}{dotnet} · {FormatSize(size)}";
            return new PreviewContent(fileType.Label, info, modified, [], false);
        }
        catch { return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false); }
    }
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
    static string? _lastErrorMessage = null;
    static readonly object _searchLock = new object();
    static MigemoProvider? _migemo;

    static readonly Dictionary<string, string> _cursorMemory = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<string> _cursorMemoryKeys = new();
    const int CursorMemoryLimit = 1000;

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        _migemo = new MigemoProvider();
        if (!_migemo.IsAvailable)
        {
            if (_migemo.DllLoaded)
                Console.WriteLine("migemo: dict not found — plain search active");
            _migemo.Dispose();
            _migemo = null;
        }

        Console.Write("\x1b[?1049h"); // Enter alternate screen buffer

        try
        {
            Console.CursorVisible = false;
        }
        catch { }

        try
        {
            string root = GetRootPath();
            Columns.Add(CreateColumn(root));
            await RebuildRightSideAsync(0);

            while (true)
            {
                HandleResizeIfNeeded();
                Draw();

                if (!Console.KeyAvailable)
                {
                    if (State.Search.Active
                        && State.Search.NeedsRecompute
                        && (DateTime.UtcNow - State.Search.LastInputTime).TotalMilliseconds >= 300)
                    {
                        await RecomputeMatchesAsync();
                        UpdateHorizontalScroll();
                        await RebuildRightSideAsync(State.ActiveColumn);
                        if (State.Preview.IsVisible) StartPreviewLoad();
                    }
                    System.Threading.Thread.Sleep(50);
                    continue;
                }

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
            }
        }
        finally
        {
            try { Console.CursorVisible = true; } catch { }
            Console.Write("\x1b[?1049l"); // Leave alternate screen buffer
            _migemo?.Dispose();
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

    static bool SelectedItemIsFileOrDrive()
    {
        if (State.ActiveColumn < 0 || State.ActiveColumn >= Columns.Count) return false;
        Column c = Columns[State.ActiveColumn];
        if (c.Selected < 0 || c.Selected >= c.Entries.Count) return false;
        string name = c.Entries[c.Selected];
        // Regular file (not a directory) OR a drive entry in the drives column
        if (!name.EndsWith("/")) return true;
        if (c.Path == "::DRIVES::") return true;
        return false;
    }

    static (string Path, bool IsDrive)? GetPreviewTarget()
    {
        if (!SelectedItemIsFileOrDrive()) return null;
        Column c = Columns[State.ActiveColumn];
        string name = c.Entries[c.Selected];

        if (c.Path == "::DRIVES::")
        {
            // "C:/" → "C:\"
            string drivePath = name.TrimEnd('/') + "\\";
            return (drivePath, true);
        }

        return (System.IO.Path.Combine(c.Path, name), false);
    }

    static void StartPreviewLoad(bool forceReload = false)
    {
        if (!State.Preview.IsVisible) return;

        // Empty-directory placeholder — show a clean message instead of trying to open a non-existent path
        if (State.ActiveColumn >= 0 && State.ActiveColumn < Columns.Count)
        {
            Column col = Columns[State.ActiveColumn];
            if (col.Selected >= 0 && col.Selected < col.Entries.Count && col.Entries[col.Selected] == "<No file>")
            {
                State.Preview.Cancel();
                State.Preview.CurrentPath = null;
                State.Preview.Content = new PreviewContent("No preview", "Empty folder", "", [], false);
                State.Preview.IsLoading = false;
                return;
            }
        }

        // Directory preview — show item count from the already-loaded right column
        if (State.ActiveColumn >= 0 && State.ActiveColumn < Columns.Count)
        {
            Column col = Columns[State.ActiveColumn];
            if (col.Selected >= 0 && col.Selected < col.Entries.Count)
            {
                string selName = col.Entries[col.Selected];
                if (selName.EndsWith("/") && col.Path != "::DRIVES::")
                {
                    string dirPath = System.IO.Path.Combine(col.Path, selName.TrimEnd('/'));
                    if (!forceReload && State.Preview.CurrentPath == dirPath && State.Preview.Content != null)
                        return;
                    string subtitle = "";
                    int rcIdx = State.ActiveColumn + 1;
                    if (rcIdx < Columns.Count)
                    {
                        Column rc = Columns[rcIdx];
                        if (!rc.IsLoading && rc.Entries.Count > 0)
                        {
                            int count = (rc.Entries.Count == 1 && rc.Entries[0] == "<No file>") ? 0 : rc.Entries.Count;
                            subtitle = count == 0 ? "Empty" : $"{count} item{(count == 1 ? "" : "s")}";
                        }
                    }
                    State.Preview.Cancel();
                    State.Preview.CurrentPath = dirPath;
                    State.Preview.Content = new PreviewContent("Directory", subtitle, "", [], false);
                    State.Preview.IsLoading = false;
                    return;
                }
            }
        }

        var target = GetPreviewTarget();
        if (target == null) return;

        string path = target.Value.Path;
        bool isDrive = target.Value.IsDrive;

        // Skip reload if already showing this path at current dimensions
        if (!forceReload && State.Preview.CurrentPath == path && State.Preview.Content != null && !State.Preview.IsLoading)
            return;

        State.Preview.Cancel();
        State.Preview.CurrentPath = path;
        State.Preview.IsLoading = true;
        State.Preview.Content = null;

        // Read first 16 bytes for type detection
        byte[] magic = new byte[16];
        try
        {
            if (!isDrive)
            {
                using var fs = File.OpenRead(path);
                fs.Read(magic, 0, 16);
            }
        }
        catch { }

        FileType fileType = isDrive
            ? new FileType(FileCategory.Drive, "Drive")
            : FileTypeDetector.Detect(path, magic);
        State.Preview.CurrentType = fileType;

        var cts = new CancellationTokenSource();
        State.Preview.Cts = cts;
        int termWidth = Console.WindowWidth;
        int drawnCols = 0;
        for (int i = State.HorizontalScroll; i < Columns.Count && (drawnCols + 1) * ColumnWidth <= termWidth; i++)
            drawnCols++;
        int width = Math.Max(ColumnWidth, termWidth - drawnCols * ColumnWidth);
        int height = Console.WindowHeight;

        _ = Task.Run(async () =>
        {
            try
            {
                var content = await PreviewLoader.LoadAsync(path, isDrive, fileType, width, height, cts.Token);
                if (!cts.Token.IsCancellationRequested)
                {
                    State.Preview.Content = content;
                    State.Preview.IsLoading = false;
                    // No Draw() here — main loop polls every 50 ms and picks up the new content
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { State.Preview.Content = new PreviewContent("Error", ex.Message, "", [], false); State.Preview.IsLoading = false; }
        }, cts.Token);
    }

    static void CancelPreviewLoad()
    {
        State.Preview.Cancel();
        State.Preview.Content = null;
        State.Preview.CurrentPath = null;
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
                await MoveRightAsync();
                break;

            case ConsoleKey.Enter:
                if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                    await ShowContextMenuAsync();
                else if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                    await OpenFileAsync();
                else
                    await EnterAsync();
                break;

            case ConsoleKey.Backspace:
                Parent();
                break;

            case ConsoleKey.V:
                if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                {
                    State.Preview.IsVisible = !State.Preview.IsVisible;
                    UpdateHorizontalScroll();
                    if (State.Preview.IsVisible)
                        StartPreviewLoad();
                    else
                        CancelPreviewLoad();
                    Draw();
                }
                break;

            case ConsoleKey.L:
                if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                    RefreshCurrent();
                break;

            case ConsoleKey.F5:
                RefreshCurrent();
                break;

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
                    // g → top (must have NO modifiers)
                    NavigationHelper.GoHome(Columns[State.ActiveColumn]);
                    UpdateHorizontalScroll();
                    await RebuildRightSideAsync(State.ActiveColumn);
                    if (State.Preview.IsVisible) StartPreviewLoad();
                }
                else if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                {
                    // G → bottom
                    NavigationHelper.GoEnd(Columns[State.ActiveColumn]);
                    UpdateHorizontalScroll();
                    await RebuildRightSideAsync(State.ActiveColumn);
                    if (State.Preview.IsVisible) StartPreviewLoad();
                }
                break;

            case ConsoleKey.B:
                if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    NavigationHelper.PageUp(Columns[State.ActiveColumn], Console.WindowHeight - 3);
                    UpdateHorizontalScroll();
                    await RebuildRightSideAsync(State.ActiveColumn);
                    if (State.Preview.IsVisible) StartPreviewLoad();
                }
                break;

            case ConsoleKey.F:
                if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    NavigationHelper.PageDown(Columns[State.ActiveColumn], Console.WindowHeight - 3);
                    UpdateHorizontalScroll();
                    await RebuildRightSideAsync(State.ActiveColumn);
                    if (State.Preview.IsVisible) StartPreviewLoad();
                }
                break;

            default:
                if (key.KeyChar == '/' && key.Modifiers == ConsoleModifiers.None)
                    EnterSearchMode();
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
            ApplyCursorMemory(column);
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
        ApplyCursorMemory(column);

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
                // Add placeholder if folder is empty (allows cursor display and future file creation)
                if (column.Entries.Count == 0)
                {
                    column.Entries.Add("<No file>");
                }
                column.IsLoading = false;
                SeekRestoreTo(column);
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

            UpdateHorizontalScroll();

            // Reset debounce timer on every cursor move, rebuild if cursor stable for 300ms
            if (IsNavigationDebounced())
            {
                await RebuildRightSideAsync(State.ActiveColumn);
            }
            else
            {
                CancelRightSideReads(State.ActiveColumn);
                // Validate right pane matches current cursor; rebuild immediately if stale
                await ValidateAndRebuildRightPaneIfNeeded(State.ActiveColumn);
            }
            if (State.Preview.IsVisible) StartPreviewLoad();
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

            UpdateHorizontalScroll();

            // Reset debounce timer on every cursor move, rebuild if cursor stable for 300ms
            if (IsNavigationDebounced())
            {
                await RebuildRightSideAsync(State.ActiveColumn);
            }
            else
            {
                CancelRightSideReads(State.ActiveColumn);
                // Validate right pane matches current cursor; rebuild immediately if stale
                await ValidateAndRebuildRightPaneIfNeeded(State.ActiveColumn);
            }
            if (State.Preview.IsVisible) StartPreviewLoad();
        }
    }

    static bool IsNavigationDebounced()
    {
        DateTime now = DateTime.UtcNow;
        // Check if 300ms has passed since cursor LAST MOVED (not last rebuild)
        // Always update timer on cursor move to implement "reset on move" behavior
        bool shouldRebuild = (now - _lastNavigationTime).TotalMilliseconds >= NavigationDebounceMs;
        _lastNavigationTime = now;  // Reset timer on every cursor move
        return shouldRebuild;
    }

    static void CancelRightSideReads(int columnIndex)
    {
        // Cancel any pending directory reads on columns to the right of current
        // This stops in-progress reads to prevent old data from updating after cursor moves
        for (int i = columnIndex + 1; i < Columns.Count; i++)
        {
            Columns[i].ReadCts?.Cancel();
        }
    }

    static async Task ValidateAndRebuildRightPaneIfNeeded(int columnIndex)
    {
        // Check if right pane matches current cursor selection
        // Rebuild if: cursor on directory but no right pane, or right pane is stale
        string? currentSelection = GetSelectedDirectory(columnIndex);

        if (currentSelection == null)
        {
            // Cursor on file — prune any stale right columns left over from previous selection
            while (Columns.Count > columnIndex + 1)
            {
                Column col = Columns[Columns.Count - 1];
                col.ReadCts?.Cancel();
                SaveCursorMemory(col);
                Columns.RemoveAt(Columns.Count - 1);
            }
            return;
        }

        if (Columns.Count <= columnIndex + 1)
        {
            // No right pane yet, but cursor points to directory → create it
            await RebuildRightSideAsync(columnIndex);
            return;
        }

        Column rightPane = Columns[columnIndex + 1];

        // If right pane's path doesn't match what cursor points to, rebuild immediately
        if (currentSelection != rightPane.Path)
        {
            await RebuildRightSideAsync(columnIndex);
        }
    }

    static void MoveLeft()
    {
        if (State.ActiveColumn > 0)
        {
            State.ActiveColumn--;
            UpdateHorizontalScroll();
            if (State.Preview.IsVisible) StartPreviewLoad();
        }
    }

    static async Task MoveRightAsync()
    {
        if (State.ActiveColumn + 1 < Columns.Count)
        {
            // Right column exists, just move to it
            State.ActiveColumn++;
            UpdateHorizontalScroll();
            if (State.Preview.IsVisible) StartPreviewLoad();
        }
        else if (State.ActiveColumn < Columns.Count)
        {
            // No right column - force rebuild to show right pane (e.g., from slow read)
            // This will read the directory of the currently selected entry
            await RebuildRightSideAsync(State.ActiveColumn);

            // If a new column was created, move to it
            if (State.ActiveColumn + 1 < Columns.Count)
            {
                State.ActiveColumn++;
                UpdateHorizontalScroll();
                if (State.Preview.IsVisible) StartPreviewLoad();
            }
        }
    }

    static void UpdateHorizontalScroll()
    {
        bool previewTakesSlot = State.Preview.IsVisible && SelectedItemIsFileOrDrive();
        int visibleColumns = Math.Max(1, Console.WindowWidth / ColumnWidth - (previewTakesSlot ? 1 : 0));

        // Only reserve a right-column slot when on a directory — files never expand rightward,
        // and the old dir column may still be in Columns during async rebuild.
        bool hasRightColumn = !previewTakesSlot && Columns.Count > State.ActiveColumn + 1;
        int rightReserve = hasRightColumn ? 1 : 0;
        int minHS = Math.Max(0, State.ActiveColumn - visibleColumns + 1 + rightReserve);
        // maxHS = AC: the only hard constraint is that AC must be visible (HS ≤ AC).
        // No Columns.Count upper bound — allows the rightmost slot to be blank when the
        // directory tree is shallow (Finder behavior: scroll position is preserved).
        int maxHS = State.ActiveColumn;
        State.HorizontalScroll = Math.Max(minHS, Math.Min(State.HorizontalScroll, maxHS));
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
        CancelPreviewLoad();
        if (State.Preview.IsVisible) StartPreviewLoad();
        await RebuildRightSideAsync(State.ActiveColumn - 1);
    }

    static async Task OpenFileAsync()
    {
        Column c = Columns[State.ActiveColumn];

        if (c.Entries.Count == 0)
            return;

        string name = c.Entries[c.Selected];

        // Only open files, not directories
        if (name.EndsWith("/"))
            return;

        string filePath = GetCurrentFullPath();
        await LaunchFileAsync(filePath);
    }

    static async Task LaunchFileAsync(string filePath)
    {
        try
        {
            if (IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.OSX))
            {
                Process.Start("/usr/bin/open", filePath);
            }
            else
            {
                // Linux and other Unix-like systems
                Process.Start("xdg-open", filePath);
            }
            _lastErrorMessage = null;
        }
        catch
        {
            _lastErrorMessage = "Cannot open file";
        }

        await Task.CompletedTask;
    }

    static async Task ShowContextMenuAsync()
    {
        // Windows only - show context menu
        if (!IsWindows())
            return;

        Column c = Columns[State.ActiveColumn];

        if (c.Entries.Count == 0)
            return;

        string name = c.Entries[c.Selected];

        // Only show context menu for files, not directories
        if (name.EndsWith("/"))
            return;

        string filePath = GetCurrentFullPath();
        await LaunchContextMenuAsync(filePath);
    }

    static async Task LaunchContextMenuAsync(string filePath)
    {
        // Windows only
        if (!IsWindows())
        {
            await Task.CompletedTask;
            return;
        }

        try
        {
            string? ctxmenuPath = null;

            // Try to find ctxmenu.exe in application directory first
            string appDir = AppContext.BaseDirectory;
            string appDirPath = Path.Combine(appDir, "ctxmenu.exe");
            if (File.Exists(appDirPath))
            {
                ctxmenuPath = appDirPath;
            }
            else
            {
                // Try to find in PATH - just use the name and let Windows search PATH
                ctxmenuPath = "ctxmenu.exe";
            }

            // Try to launch - will throw if not found in PATH
            // Quote the file path to handle spaces and special characters (including Unicode)
            string quotedPath = $"\"{filePath}\"";
            Process.Start(ctxmenuPath, quotedPath);
            _lastErrorMessage = null;
        }
        catch
        {
            _lastErrorMessage = "ctxmenu.exe not found";
        }

        await Task.CompletedTask;
    }

    static void Parent()
    {
        if (State.ActiveColumn == 0)
            return;

        State.ActiveColumn--;
        UpdateHorizontalScroll();
        if (State.Preview.IsVisible) StartPreviewLoad();
    }

    static void SaveCursorMemory(Column col)
    {
        if (col.Selected < 0 || col.Selected >= col.Entries.Count) return;
        string entry = col.Entries[col.Selected];
        if (entry == "<No file>") return;
        if (!_cursorMemory.ContainsKey(col.Path))
        {
            if (_cursorMemoryKeys.Count >= CursorMemoryLimit)
            {
                _cursorMemory.Remove(_cursorMemoryKeys[0]);
                _cursorMemoryKeys.RemoveAt(0);
            }
            _cursorMemoryKeys.Add(col.Path);
        }
        _cursorMemory[col.Path] = entry;
    }

    static void ApplyCursorMemory(Column col)
    {
        if (!_cursorMemory.TryGetValue(col.Path, out string? target)) return;
        col.RestoreTo = target;
        SeekRestoreTo(col);
    }

    static void SeekRestoreTo(Column col)
    {
        if (col.RestoreTo == null) return;
        for (int i = 0; i < col.Entries.Count; i++)
        {
            if (col.Entries[i] == col.RestoreTo)
            {
                col.Selected = i;
                col.RestoreTo = null;
                return;
            }
        }
    }

    static void RefreshCurrent()
    {
        Column c = Columns[State.ActiveColumn];
        c.CachedChildren.Clear();
        c.CachedTime = DateTime.UtcNow.AddMilliseconds(-CacheExpireMs - 1);
        c.ReadCts?.Cancel();
        _ = RefreshCurrentAsync();
    }

    static async Task RefreshCurrentAsync()
    {
        Column current = Columns[State.ActiveColumn];
        string currentPath = current.Path;

        SaveCursorMemory(current);

        Column refreshed = await CreateColumnAsync(currentPath, CancellationToken.None);
        if (refreshed.LoadingTask != null)
            await refreshed.LoadingTask;
        Columns[State.ActiveColumn] = refreshed;

        await RebuildRightSideAsync(State.ActiveColumn);
    }

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

    static async Task HandleSearchKeyAsync(ConsoleKeyInfo key)
    {
        SearchState s = State.Search;
        Column col = Columns[State.ActiveColumn];

        if (key.Key == ConsoleKey.Escape)
        {
            ExitSearchMode(restoreCursor: true);
            UpdateHorizontalScroll();
            await RebuildRightSideAsync(State.ActiveColumn);
            if (State.Preview.IsVisible) StartPreviewLoad();
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            ExitSearchMode(restoreCursor: false);
            UpdateHorizontalScroll();
            await RebuildRightSideAsync(State.ActiveColumn);
            if (State.Preview.IsVisible) StartPreviewLoad();
            return;
        }

        if (key.Key == ConsoleKey.R && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            s.RegexMode = !s.RegexMode;
            s.NeedsRecompute = true;
            s.LastInputTime = DateTime.UtcNow;
            return;
        }

        if (key.Key == ConsoleKey.DownArrow ||
            (key.Key == ConsoleKey.N && (key.Modifiers & ConsoleModifiers.Control) != 0))
        {
            bool moved = false;
            lock (_searchLock)
            {
                if (s.Matches.Count > 0)
                {
                    s.MatchIndex = (s.MatchIndex + 1) % s.Matches.Count;
                    col.Selected = s.Matches[s.MatchIndex];
                    moved = true;
                }
            }
            if (moved)
            {
                UpdateHorizontalScroll();
                await RebuildRightSideAsync(State.ActiveColumn);
                if (State.Preview.IsVisible) StartPreviewLoad();
            }
            return;
        }

        if (key.Key == ConsoleKey.UpArrow ||
            (key.Key == ConsoleKey.P && (key.Modifiers & ConsoleModifiers.Control) != 0))
        {
            bool moved = false;
            lock (_searchLock)
            {
                if (s.Matches.Count > 0)
                {
                    s.MatchIndex = (s.MatchIndex - 1 + s.Matches.Count) % s.Matches.Count;
                    col.Selected = s.Matches[s.MatchIndex];
                    moved = true;
                }
            }
            if (moved)
            {
                UpdateHorizontalScroll();
                await RebuildRightSideAsync(State.ActiveColumn);
                if (State.Preview.IsVisible) StartPreviewLoad();
            }
            return;
        }

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

        if (!char.IsControl(key.KeyChar))
        {
            s.Query += key.KeyChar;
            s.NeedsRecompute = true;
            s.LastInputTime = DateTime.UtcNow;
            return;
        }

        // Everything else is swallowed
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
            col.ReadCts?.Cancel();
            SaveCursorMemory(col);
            Columns.RemoveAt(Columns.Count - 1);
        }

        string? nextPath = GetSelectedDirectory(columnIndex);

        while (nextPath != null)
        {
            Column next = await CreateColumnAsync(nextPath, CancellationToken.None);
            if (next.LoadingTask != null)
                await next.LoadingTask;
            Columns.Add(next);

            nextPath = GetSelectedDirectory(Columns.Count - 1);
        }

        // Recalculate scroll now that the right-side column count is final.
        UpdateHorizontalScroll();
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

    static void HandleResizeIfNeeded()
    {
        int w = Console.WindowWidth;
        int h = Console.WindowHeight;
        if (w <= 0 || h <= 0) return;
        if (w == State.PrevWidth && h == State.PrevHeight) return;

        State.PrevWidth = w;
        State.PrevHeight = h;
        State.PrevFrame = null; // force full redraw in Draw()

        UpdateHorizontalScroll();

        // Clamp every column's scroll offset for the new height
        int visibleHeight = Math.Max(1, h - 3);
        for (int i = 0; i < Columns.Count; i++)
        {
            Column col = Columns[i];
            if (col.ScrollOffset < 0)
                col.ScrollOffset = 0;
            if (col.Selected >= col.ScrollOffset + visibleHeight)
                col.ScrollOffset = col.Selected - visibleHeight + 1;
        }

        // Reload preview at new dimensions (cancels any in-flight load, bypasses same-path guard)
        if (State.Preview.IsVisible)
            StartPreviewLoad(forceReload: true);
    }

    static void Draw()
    {
        int width = Console.WindowWidth;
        int height = Console.WindowHeight;

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

        // Show preview whenever it's visible and there's room — for files it occupies a reserved slot,
        // for directories it fills blank space left after columns run out (Finder-style).
        bool showPreview = State.Preview.IsVisible && Console.WindowWidth >= ColumnWidth * 2;
        // Only carve out a reserved column slot for files; directories fill blank space naturally.
        bool filePreview = State.Preview.IsVisible && SelectedItemIsFileOrDrive();

        int displayX = 0;
        int drawnColumnsWidth = 0;
        int columnDrawLimit = filePreview ? width - ColumnWidth : width;

        for (int i = State.HorizontalScroll;
             i < Columns.Count && displayX + ColumnWidth <= columnDrawLimit;
             i++)
        {
            bool isFirstVisible = (i == State.HorizontalScroll);
            DrawColumnToLines(lines, Columns[i], displayX, i == State.ActiveColumn, i < State.ActiveColumn, width, height, isFirstVisible);
            displayX += ColumnWidth;
            drawnColumnsWidth += ColumnWidth;
        }

        // Preview pane
        if (showPreview)
        {
            int previewWidth = width - drawnColumnsWidth;
            PreviewContent content;
            if (State.Preview.IsLoading || State.Preview.Content == null)
            {
                content = new PreviewContent("Loading…", "", "", ["⠋"], true);
            }
            else
            {
                content = State.Preview.Content;
            }

            if (previewWidth >= ColumnWidth)
                DrawPreviewToLines(lines, content, drawnColumnsWidth, previewWidth, height);
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

        // Override status with preview-hidden hint only when terminal width is the limiting factor
        if (State.Preview.IsVisible && SelectedItemIsFileOrDrive() && Console.WindowWidth < ColumnWidth * 2)
        {
            _lastErrorMessage = "[preview hidden: narrow terminal]";
        }

        // Status line
        if (State.Search.Active)
        {
            List<int> matchSnapshot;
            bool done;
            lock (_searchLock)
            {
                matchSnapshot = new List<int>(State.Search.Matches);
                done = State.Search.SearchDone;
            }
            string searchBar = SearchHelper.BuildSearchStatusBar(State.Search, matchSnapshot, done, width);
            frame[height - 1] = searchBar; // already padded to width
        }
        else if (_lastErrorMessage != null)
        {
            string status = _lastErrorMessage;
            _lastErrorMessage = null;  // Clear error after displaying
            status = CharacterWidth.SmartTruncate(status, width);
            frame[height - 1] = CharacterWidth.PadToWidth(status, width);
        }
        else
        {
            string previewHint = State.Preview.IsVisible ? "Shift+V=Preview[on]" : "Shift+V=Preview[off]";
            string status;
            if (IsWindows())
                status = $"Esc=Quit | ↑↓=move | PgUp/PgDn=page | Home/End g/G=jump | /=search | Ctrl+Enter=Open | Shift+Enter=Menu | Ctrl+L/F5=Refresh | {previewHint}";
            else
                status = $"Esc=Quit | ↑↓=move | PgUp/PgDn=page | Home/End g/G=jump | /=search | Ctrl+Enter=Open File | Ctrl+L/F5=Refresh | {previewHint}";
            status = CharacterWidth.SmartTruncate(status, width);
            frame[height - 1] = CharacterWidth.PadToWidth(status, width);
        }

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

            int entryAbsIndex = scrollOffset + i;
            bool isCurrentMatch = inSearch && entryAbsIndex == currentMatchEntry;
            bool isOtherMatch   = inSearch && !isCurrentMatch && matchSet.Contains(entryAbsIndex);

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

            if (isCurrentMatch)
            {
                // Current match: green background, black text
                string bgColor = "\x1b[42m";
                string textColor = "\x1b[30m";
                string reset = "\x1b[0m";

                string displayText = prefix + entry;
                int displayTextWidth = CharacterWidth.GetStringWidth(displayText);
                int paddingNeeded = Math.Max(0, contentSlot - displayTextWidth);

                string fullLine = bgColor + textColor + displayText + new string(' ', paddingNeeded) + reset;
                int totalDisplayWidth = displayTextWidth + paddingNeeded;

                lines[startRow + i].AddColumn(fullLine, totalDisplayWidth, ColumnWidth);
            }
            else if (isSelected)
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
            else if (isOtherMatch)
            {
                // Other match: green foreground text on default background
                int displayWidth = 2 + CharacterWidth.GetStringWidth(entry);
                string coloredEntry = "\x1b[32m" + entry + "\x1b[0m";
                lines[startRow + i].AddColumn(prefix + coloredEntry, displayWidth, ColumnWidth);
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

    static void DrawPreviewToLines(Line[] lines, PreviewContent content, int startX, int previewWidth, int frameHeight)
    {
        int visibleHeight = frameHeight - 3; // same as column body height

        // Row 0: type label
        string headerLabel = CharacterWidth.SmartTruncate(content.TypeLabel, previewWidth - 1);
        int headerW = CharacterWidth.GetStringWidth(headerLabel);
        lines[0].AddColumn(headerLabel, headerW, previewWidth);

        if (visibleHeight < 1) return;

        // Row 1: info line (size / dimensions / duration etc.)
        if (!string.IsNullOrEmpty(content.InfoLine))
        {
            string info = CharacterWidth.SmartTruncate(content.InfoLine, previewWidth - 1);
            int infoW = CharacterWidth.GetStringWidth(info);
            string infoColored = AnsiColors.Colorize(info, AnsiColors.Yellow);
            lines[1].AddColumn(infoColored, infoW, previewWidth);
        }
        else
            lines[1].AddColumn("", 0, previewWidth);

        // Row 2: modified date
        if (!string.IsNullOrEmpty(content.Modified))
        {
            string mod = CharacterWidth.SmartTruncate(content.Modified, previewWidth - 1);
            lines[2].AddColumn(mod, CharacterWidth.GetStringWidth(mod), previewWidth);
        }
        else
            lines[2].AddColumn("", 0, previewWidth);

        // Row 3: divider
        if (visibleHeight >= 3)
        {
            string divider = new string('─', previewWidth - 1);
            lines[3].AddColumn(divider, CharacterWidth.GetStringWidth(divider), previewWidth);
        }

        // Row 4 (optional): extension mismatch warning
        int bodyStart = 4;
        if (!string.IsNullOrEmpty(content.ExtMismatch) && visibleHeight >= bodyStart)
        {
            string warn = CharacterWidth.SmartTruncate(content.ExtMismatch, previewWidth - 1);
            string warnColored = AnsiColors.Colorize(warn, AnsiColors.Yellow);
            lines[bodyStart].AddColumn(warnColored, CharacterWidth.GetStringWidth(warn), previewWidth);
            bodyStart = 5;
        }

        // Body lines
        if (content.PixelLines != null)
        {
            // Pixel render: pass ANSI-escaped strings verbatim with their visual width
            for (int i = 0; i < visibleHeight - bodyStart + 1 && i < content.PixelLines.Length; i++)
            {
                int row = bodyStart + i;
                if (row >= visibleHeight + 1) break;
                lines[row].AddColumn(content.PixelLines[i], content.PixelWidth, previewWidth);
            }
            int lastPixelRow = bodyStart + Math.Min(content.PixelLines.Length, visibleHeight - bodyStart + 1);
            for (int i = lastPixelRow; i < visibleHeight + 1; i++)
                lines[i].AddColumn("", 0, previewWidth);
        }
        else
        {
            for (int i = 0; i < visibleHeight - bodyStart + 1 && i < content.BodyLines.Length; i++)
            {
                int row = bodyStart + i;
                if (row >= visibleHeight + 1) break;
                string bodyLine = CharacterWidth.SmartTruncate(content.BodyLines[i], previewWidth - 1);
                int bodyW = CharacterWidth.GetStringWidth(bodyLine);
                lines[row].AddColumn(bodyLine, bodyW, previewWidth);
            }
            int lastBodyRow = bodyStart + Math.Min(content.BodyLines.Length, visibleHeight - bodyStart + 1);
            for (int i = lastBodyRow; i < visibleHeight + 1; i++)
                lines[i].AddColumn("", 0, previewWidth);
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
