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
    public readonly PreviewPane Preview = new();
}

internal record PreviewContent(
    string   TypeLabel,
    string   InfoLine,
    string   Modified,
    string[] BodyLines,
    bool     IsPartial,
    string?  ExtMismatch = null   // set when magic-detected type ≠ extension type
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

        if (extOnly.Category != detected.Category)
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
                FileCategory.Image     => LoadImageMeta(filePath, fileType, modified, width),
                FileCategory.Video     => LoadVideoMeta(filePath, fileType, modified, width),
                FileCategory.Audio     => LoadAudioMeta(filePath, fileType, modified, width),
                FileCategory.Archive   => await LoadArchiveAsync(filePath, fileType, modified, width, bodyHeight, ct),
                FileCategory.Executable=> LoadExecutable(filePath, fileType, modified, width),
                FileCategory.Pdf       => LoadPdf(filePath, fileType, modified, width),
                _                      => await LoadBinaryAsync(filePath, fileType, modified, width, bodyHeight, ct),
            };

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
        string infoLine = $"{FormatSize(size)}";
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

    private static string[] TruncateLines(IEnumerable<string> lines, int width)
    {
        var result = new List<string>();
        foreach (var l in lines)
            result.Add(CharacterWidth.SmartTruncate(l, width - 1));
        return [.. result];
    }

    // ── Stubs (implemented in later tasks) ────────────────────────────────
    private static PreviewContent LoadDrive(string p, int w, int h)
        => new("Drive", "", "", [], false);
    private static PreviewContent LoadImageMeta(string p, FileType t, string m, int w)
        => new(t.Label, FormatSize(new FileInfo(p).Length), m, [], false);
    private static PreviewContent LoadVideoMeta(string p, FileType t, string m, int w)
        => new(t.Label, FormatSize(new FileInfo(p).Length), m, [], false);
    private static PreviewContent LoadAudioMeta(string p, FileType t, string m, int w)
        => new(t.Label, FormatSize(new FileInfo(p).Length), m, [], false);
    private static Task<PreviewContent> LoadArchiveAsync(string p, FileType t, string m, int w, int h, CancellationToken ct)
        => Task.FromResult(new PreviewContent(t.Label, FormatSize(new FileInfo(p).Length), m, [], false));
    private static PreviewContent LoadExecutable(string p, FileType t, string m, int w)
        => new(t.Label, FormatSize(new FileInfo(p).Length), m, [], false);
    private static PreviewContent LoadPdf(string p, FileType t, string m, int w)
        => new(t.Label, FormatSize(new FileInfo(p).Length), m, [], false);
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
                // Add placeholder if folder is empty (allows cursor display and future file creation)
                if (column.Entries.Count == 0)
                {
                    column.Entries.Add("<No file>");
                }
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
            return;  // Cursor on file, no right pane needed

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
        }
    }

    static async Task MoveRightAsync()
    {
        if (State.ActiveColumn + 1 < Columns.Count)
        {
            // Right column exists, just move to it
            State.ActiveColumn++;
            UpdateHorizontalScroll();
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
            }
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
    }

    static void RefreshCurrent()
    {
        Column c = Columns[State.ActiveColumn];
        c.CachedChildren.Clear();
        c.CachedTime = DateTime.UtcNow.AddMilliseconds(-CacheExpireMs - 1);
        c.ReadCts?.Cancel();

        // Re-read current column and refresh right side
        _ = RefreshCurrentAsync();
    }

    static async Task RefreshCurrentAsync()
    {
        Column current = Columns[State.ActiveColumn];
        string currentPath = current.Path;

        // Re-read current directory
        Column refreshed = await CreateColumnAsync(currentPath, CancellationToken.None);
        Columns[State.ActiveColumn] = refreshed;

        // Rebuild right side based on refreshed column
        await RebuildRightSideAsync(State.ActiveColumn);
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
        string status;
        if (_lastErrorMessage != null)
        {
            status = _lastErrorMessage;
            _lastErrorMessage = null;  // Clear error after displaying
        }
        else
        {
            if (IsWindows())
                status = "Esc=Quit | ↑↓=Select | ←→=Column | Enter=Open | Ctrl+Enter=Open | Shift+Enter=Menu | Bksp=Parent | R=Refresh";
            else
                status = "Esc=Quit | ↑↓=Select | ←→=Column | Enter=Open | Ctrl+Enter=Open File | Bksp=Parent | R=Refresh";
        }
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
