# Preview System Design

**Date:** 2026-06-11  
**Status:** Approved

## Context

The file manager currently shows only directory listings — selecting a file gives no information beyond its name. This adds a preview pane that activates when a file is selected, occupying the rightmost column slot plus all remaining terminal width (matching macOS Finder column-view behaviour). Drive entries also get a dedicated properties view. The system must work out-of-the-box with zero external dependencies, stay non-blocking, and remain easily hideable.

---

## Layout

The preview pane appears to the right of all directory columns when a file (or drive entry) is selected and preview is enabled.

**Width formula** (no hardcoded column width):
```
previewWidth = terminalWidth − drawnColumnsDisplayWidth
```
`drawnColumnsDisplayWidth` is the sum of actual drawn column widths, so the formula is correct when columns become resizable.

**Visible directory columns when preview is active:**
```
visibleDirCols = floor(terminalWidth / colWidth) − 1
```
`UpdateHorizontalScroll()` is made preview-aware: it subtracts 1 from `maxVisible` when `preview.IsVisible && selectedItemIsFile`. This means toggling the preview on/off automatically slides columns left/right, identical to navigating into/out of a directory.

**Width breakpoints:**

| Terminal width | Behaviour |
|---|---|
| `≥ colWidth × 2` (≥ 64 at default) | Show `visibleDirCols` dir columns + preview pane |
| `< colWidth × 2` | Preview hidden silently; status bar hint; toggle state preserved |

Minimum useful preview width is `MinPreviewWidth = ColumnWidth`. At that width the hex dump and all metadata still render correctly (see adaptive hex below). The preview auto-returns when the terminal widens past `colWidth × 2` — no user action needed.

---

## Shift+V toggle behaviour

| Situation | Result |
|---|---|
| Preview off, any position | Turn preview on; call `UpdateHorizontalScroll()` → columns shift left, preview slides in |
| Preview on, any position | Turn preview off; call `UpdateHorizontalScroll()` → columns fill back |
| Preview on, terminal too narrow | Toggle stored but pane stays hidden; status bar shows `[preview hidden: narrow terminal]` |

Shift+V is a global toggle — behaviour is position-independent. Scrolling into/focusing the preview pane is out of scope for this version.

---

## Architecture — three new units

All added to `Program.cs` to maintain the single-file structure.

### 1. `FileTypeDetector` (static class)

```
FileTypeDetector.Detect(string path, ReadOnlySpan<byte> magic) → FileType
```

- Checks first 16 magic bytes against a signature table (~80 entries)
- Falls back to file extension if no magic match
- Returns `FileType { Category, Label, MimeType }`
- Zero allocations on the hot path (span-based comparison)

`Category` enum: `Text | Image | Video | Audio | Archive | Executable | Pdf | Drive | Binary`

`Label` examples: `"JPEG Image"`, `"MP4 Video"`, `"SQLite 3 Database"`, `"ZIP Archive"`, `"PE64 Executable"`

### 2. `PreviewLoader` (static class)

```
PreviewLoader.LoadAsync(string path, FileType type, int width, int height, CancellationToken ct)
    → Task<PreviewContent>
```

One private static async method per category. Returns a `PreviewContent` immediately with partial data where possible (same incremental pattern as directory loading). Body lines are pre-truncated to `width` using the existing `SmartTruncate` — no re-measuring at render time.

### 3. `PreviewPane` (state object on `ScreenState`)

```csharp
class PreviewPane {
    bool IsVisible;
    string? CurrentPath;        // path of file currently loaded/loading
    PreviewContent? Content;
    bool IsLoading;
    CancellationTokenSource? Cts;
}
```

Added as a field to `ScreenState` alongside `Columns`. Reset when selected file changes; cancelled on navigation away.

`SelectedItemIsFile()` is a new helper that returns true when the active column's selected entry does **not** end with `/` — this naturally includes drive entries (`C:\`) since they end with `\`, not `/`. When `column.Path == "::DRIVES::"` and a drive is selected, `PreviewLoader` receives the drive root path (e.g. `C:\`) and routes to the Drive handler using `DriveInfo`.

---

## `PreviewContent` structure

```csharp
record PreviewContent(
    string   TypeLabel,   // "MP4 Video"
    string   InfoLine,    // "1920×1080 · 00:03:42 · 87.4 MB"
    string   Modified,    // "2026-02-10 18:55"
    string[] BodyLines,   // content lines, pre-truncated to previewWidth
    bool     IsPartial    // true while still loading
);
```

---

## Content per file category

### Text / source code
`.txt .md .json .xml .csv .py .js .ts .c .cs .sh .log` and any file detected as UTF-8/UTF-16 text

- Info line: encoding, line count (up to 10 000 lines counted; `> 10k` otherwise)
- Body: first `height − 4` lines of file content

### Image (metadata only — no pixel rendering)
`.png .jpg .jpeg .gif .bmp .webp .ico .svg`

- Dimensions parsed from raw header bytes (no decoder needed):
  - PNG: bytes 16–23 (IHDR)
  - JPEG: scan for SOF marker (0xFF 0xC0 / 0xC2)
  - GIF: bytes 6–9
  - BMP: bytes 18–25 (DIB header)
  - WebP: bytes 24–27
  - ICO: entry header
  - SVG: scan `width=`/`height=`/`viewBox=` attributes
- Info line: `W×H px · bit depth · color space`
- Body: empty (no pixel art)

### Video
`.mp4 .mov .avi .mkv .wmv .webm`

- MP4/MOV: parse ISO Base Media boxes (`moov → mvhd` for duration, `tkhd` for dimensions)
- AVI: RIFF `avih` chunk (width, height, total frames, microseconds/frame → duration)
- MKV/others: format label + size only (EBML too complex without deps)
- Info line: `W×H · duration · size`

### Audio
`.wav .flac .mp3 .aac .m4a .ogg`

- WAV: `fmt ` chunk → sample rate, channels, bit depth; `data` chunk size → exact duration
- FLAC: STREAMINFO block → sample rate, channels, bit depth, total samples → exact duration
- MP3: frame header → bitrate; Xing/VBRI header if present → VBR-accurate duration
- Info line: `duration · sample rate · channels · bit depth`

### Archive
`.zip .tar .gz .bz2 .7z .rar`

- ZIP: `System.IO.Compression.ZipArchive` (BCL) → entry count + first `min(height − 4, count)` entries with sizes
- TAR: fixed 512-byte records → entry listing
- GZ: header bytes → original filename
- 7z / RAR: type label + size only (proprietary formats)

### Executable / library
`.exe .dll .sys .ocx`

- PE header: magic (MZ → PE), machine type (x86/x64/ARM64), subsystem (GUI/CUI/driver)
- Optional header: .NET CLR header present → show runtime version
- Info line: `arch · subsystem · .NET flag`

### PDF
`.pdf`

- Version from `%PDF-x.y` header
- Page count: scan linearised `Count` entry or enumerate `Page` objects
- Title/Author: plain-text scan of `/Title` and `/Author` from `/Info` dict
- Info line: `PDF x.y · N pages`

### Drive entry (special — triggered from Drives column)
Uses `DriveInfo` (BCL):

```
C:\   NTFS · Fixed Drive
──────────────────────────
Label:      System
Total:      500.1 GB
Used:       342.3 GB (68%)
Free:       157.8 GB
████████████░░░░░░  68%
```

Usage bar width scales to `previewWidth − 8`.

### Binary / unknown
Fallback for all unrecognised files:

- Type label from magic bytes if matched, else `"Binary Data"`
- Adaptive hex dump:
  - `bytesPerRow = clamp(max(4, (previewWidth − 14) / 4), 4, 16)`
  - At 32 chars: 4 bytes/row; at 56: ~10; at 80+: 16 (standard)
  - Format: `XXXXXXXX  HH HH HH…  ASCII`

---

## Rendering integration

`BuildFrame()` is modified minimally:

1. Calculate `previewVisible = _preview.IsVisible && SelectedItemIsFile() && previewWidth >= MinPreviewWidth`
2. Adjust `visibleDirCols` by −1 when `previewVisible` (existing `UpdateHorizontalScroll` handles this)
3. After the column loop, if `previewVisible`: call `DrawPreviewToLines(lines, previewStartX, previewWidth)`
4. `DrawPreviewToLines` writes `PreviewContent` into the shared `Line[]` array, using the existing `Line.AddColumn` mechanism

`DrawPreviewToLines` renders: separator `│`, header (type label + file name), info line, date, divider `─────`, then body lines. Falls back to a loading spinner (`⠋⠙⠹…`) if `IsLoading`.

The existing diff-based renderer in `Draw()` sees no changes — it still compares `string[]` frame-to-frame.

---

## Data flow

```
Shift+V pressed
  → toggle PreviewPane.IsVisible
  → UpdateHorizontalScroll()   ← columns shift automatically
  → if now visible + file selected: CancelPrev(); StartLoadAsync()
  → Draw()

Navigation changes selected item to a file
  → CancelPrev(); StartLoadAsync(newPath)
  → Draw() shows spinner

LoadAsync completes
  → PreviewPane.Content = result; IsLoading = false
  → Draw() (triggered by completion)

Navigation moves away from file / to directory
  → CancelPrev(); PreviewPane.Content = null
  → Draw()
```

---

## Files to modify

- **`Program.cs`**: all changes (single-file codebase)
  - Add `FileTypeDetector` static class (~150 lines: signature table + Detect method)
  - Add `PreviewLoader` static class (~300 lines: one handler per category)
  - Add `PreviewPane` class (~20 lines)
  - Add `PreviewContent` record (~10 lines)
  - Modify `ScreenState`: add `PreviewPane Preview` field
  - Modify `UpdateHorizontalScroll()`: subtract 1 from maxVisible when preview active
  - Modify `BuildFrame()`: pass preview width to column loop; call `DrawPreviewToLines` after loop
  - Add `DrawPreviewToLines()` (~80 lines)
  - Modify key handler: Shift+V → toggle + scroll update + trigger load
  - Modify navigation handlers: cancel/restart preview load on selection change

---

## Verification

1. **Text file**: select a `.txt` or `.cs` — preview pane appears with encoding, line count, first N lines of content
2. **Toggle**: Shift+V hides/shows pane; columns slide left/right smoothly
3. **Shift+V at rightmost column**: pressing Shift+V while on a file with preview off — columns scroll left, preview slides in from right
4. **Narrow terminal**: resize to < 64 cols — preview hidden, status bar hint shown; widen back — preview returns
5. **Drive entry**: navigate to Drives column, select `C:\` — shows label, filesystem, size, usage bar
6. **Binary / hex**: select an `.exe` or unknown binary — hex dump shown, bytes-per-row matches pane width
7. **Image**: select a `.png` — dimensions and color info shown, no pixel art
8. **ZIP archive**: select a `.zip` — entry count and file listing shown
9. **MP4 video**: select an `.mp4` — dimensions and duration shown
10. **FLAC audio**: select a `.flac` — sample rate, channels, bit depth, duration shown
11. **Resize while preview open**: drag terminal wider/narrower — hex bytes-per-row and text line length adapt correctly without hardcoded constants
