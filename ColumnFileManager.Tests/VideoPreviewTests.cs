using Xunit;
using System.Diagnostics;

public class VideoPreviewTests
{
    private static bool IsFfmpegAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                Arguments             = "-version",
                UseShellExecute       = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow        = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    [Fact]
    public async Task LoadVideoPreviewAsync_NarrowPane_ReturnsMeta()
    {
        // width=1 → pixelCols=0 → early return, no FFmpeg, PixelLines stays null
        string tempPath = Path.Combine(Path.GetTempPath(), "cfm_test_meta.mp4");
        File.WriteAllBytes(tempPath, [0x00, 0x00, 0x00, 0x08]);
        try
        {
            var fileType = new FileType(FileCategory.Video, "MP4 Video", "video/mp4");
            var result = await PreviewLoader.LoadVideoPreviewAsync(
                tempPath, fileType, "2024-01-01 00:00", 1, 20, CancellationToken.None);

            Assert.Null(result.PixelLines);
            Assert.Equal(0, result.PixelWidth);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public async Task LoadVideoPreviewAsync_CancelledToken_ThrowsOperationCancelled()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "cfm_test_cancel.mp4");
        File.WriteAllBytes(tempPath, [0x00, 0x00, 0x00, 0x08]);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var fileType = new FileType(FileCategory.Video, "MP4 Video", "video/mp4");
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                PreviewLoader.LoadVideoPreviewAsync(tempPath, fileType, "", 80, 20, cts.Token));
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public void GetVideoCachePath_SameFile_ReturnsSamePath()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "cfm_test_cachekey.mp4");
        File.WriteAllBytes(tempPath, [0x00, 0x00]);
        try
        {
            string path1 = PreviewLoader.GetVideoCachePath(tempPath);
            string path2 = PreviewLoader.GetVideoCachePath(tempPath);
            Assert.Equal(path1, path2);
            Assert.EndsWith(".png", path1);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public void GetVideoCachePath_DifferentMtime_ReturnsDifferentPath()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "cfm_test_mtime.mp4");
        File.WriteAllBytes(tempPath, [0x00, 0x00]);
        try
        {
            string path1 = PreviewLoader.GetVideoCachePath(tempPath);
            File.SetLastWriteTimeUtc(tempPath, DateTime.UtcNow.AddSeconds(2));
            string path2 = PreviewLoader.GetVideoCachePath(tempPath);
            Assert.NotEqual(path1, path2);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public async Task LoadVideoPreviewAsync_WithRealVideo_HasPixelLines()
    {
        if (!IsFfmpegAvailable())
            return; // skip — FFmpeg not installed

        // Locate a real video file to test with
        string? videoPath = null;
        string[] searchDirs = [
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonVideos),
        ];
        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            videoPath = Directory.EnumerateFiles(dir, "*.mp4", SearchOption.AllDirectories).FirstOrDefault()
                     ?? Directory.EnumerateFiles(dir, "*.mkv", SearchOption.AllDirectories).FirstOrDefault();
            if (videoPath != null) break;
        }

        if (videoPath is null)
            return; // skip — no video files found to test with

        var fi = new FileInfo(videoPath);
        var fileType = new FileType(FileCategory.Video, "MP4 Video", "video/mp4");
        var result = await PreviewLoader.LoadVideoPreviewAsync(
            videoPath, fileType, fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), 80, 20, CancellationToken.None);

        Assert.NotNull(result.PixelLines);
        Assert.True(result.PixelWidth > 0);
        Assert.False(string.IsNullOrEmpty(result.InfoLine));

        // Cache file should now exist
        string cachePath = PreviewLoader.GetVideoCachePath(videoPath);
        Assert.True(File.Exists(cachePath));

        // Second call should hit cache (same result, no FFmpeg re-run)
        var result2 = await PreviewLoader.LoadVideoPreviewAsync(
            videoPath, fileType, fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), 80, 20, CancellationToken.None);
        Assert.NotNull(result2.PixelLines);
    }
}
