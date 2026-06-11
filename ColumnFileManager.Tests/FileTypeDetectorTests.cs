using Xunit;

public class FileTypeDetectorTests
{
    [Fact]
    public void Detect_PngMagic_ReturnsImage()
    {
        byte[] magic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0,0,0,0,0,0,0,0];
        var t = FileTypeDetector.Detect("file.png", magic);
        Assert.Equal(FileCategory.Image, t.Category);
        Assert.Equal("PNG Image", t.Label);
    }

    [Fact]
    public void Detect_JpegMagic_ReturnsImage()
    {
        byte[] magic = [0xFF, 0xD8, 0xFF, 0xE0, 0,0,0,0,0,0,0,0,0,0,0,0];
        var t = FileTypeDetector.Detect("photo.jpg", magic);
        Assert.Equal(FileCategory.Image, t.Category);
        Assert.Equal("JPEG Image", t.Label);
    }

    [Fact]
    public void Detect_Mp4FtypMagic_ReturnsVideo()
    {
        // bytes 4-7 = "ftyp", bytes 8-11 = "mp42"
        byte[] magic = [0,0,0,0x20, 0x66,0x74,0x79,0x70, 0x6D,0x70,0x34,0x32, 0,0,0,0];
        var t = FileTypeDetector.Detect("clip.mp4", magic);
        Assert.Equal(FileCategory.Video, t.Category);
    }

    [Fact]
    public void Detect_RiffWave_ReturnsAudio()
    {
        byte[] magic = [0x52,0x49,0x46,0x46, 0,0,0,0, 0x57,0x41,0x56,0x45, 0,0,0,0];
        var t = FileTypeDetector.Detect("sound.wav", magic);
        Assert.Equal(FileCategory.Audio, t.Category);
        Assert.Equal("WAV Audio", t.Label);
    }

    [Fact]
    public void Detect_RiffAvi_ReturnsVideo()
    {
        byte[] magic = [0x52,0x49,0x46,0x46, 0,0,0,0, 0x41,0x56,0x49,0x20, 0,0,0,0];
        var t = FileTypeDetector.Detect("video.avi", magic);
        Assert.Equal(FileCategory.Video, t.Category);
    }

    [Fact]
    public void Detect_ZipMagic_ReturnsArchive()
    {
        byte[] magic = [0x50,0x4B,0x03,0x04, 0,0,0,0,0,0,0,0,0,0,0,0];
        var t = FileTypeDetector.Detect("file.zip", magic);
        Assert.Equal(FileCategory.Archive, t.Category);
    }

    [Fact]
    public void Detect_MzExe_ReturnsExecutable()
    {
        byte[] magic = [0x4D,0x5A, 0,0,0,0,0,0,0,0,0,0,0,0,0,0];
        var t = FileTypeDetector.Detect("app.exe", magic);
        Assert.Equal(FileCategory.Executable, t.Category);
    }

    [Fact]
    public void Detect_ExtensionFallback_CsSource()
    {
        byte[] magic = new byte[16]; // no recognisable magic
        var t = FileTypeDetector.Detect("Program.cs", magic);
        Assert.Equal(FileCategory.Text, t.Category);
        Assert.Equal("C# Source", t.Label);
    }

    [Fact]
    public void Detect_UnknownBinary_ReturnsBinary()
    {
        byte[] magic = [0x00,0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0A,0x0B,0x0C,0x0D,0x0E,0x0F];
        var t = FileTypeDetector.Detect("data.bin", magic);
        Assert.Equal(FileCategory.Binary, t.Category);
    }

    [Fact]
    public void Detect_PdfMagic_ReturnsPdf()
    {
        byte[] magic = [0x25,0x50,0x44,0x46,0x2D,0x31,0x2E,0x37, 0,0,0,0,0,0,0,0];
        var t = FileTypeDetector.Detect("doc.pdf", magic);
        Assert.Equal(FileCategory.Pdf, t.Category);
    }
}
