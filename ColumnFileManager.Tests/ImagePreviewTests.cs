using Xunit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

public class ImagePreviewTests
{
    [Fact]
    public void RenderPixelLines_2x2Image_ProducesCorrectAnsiEscapes()
    {
        // Arrange: 2x2 opaque image — upper row red, lower row blue
        using var image = new Image<Rgba32>(2, 2);
        image[0, 0] = new Rgba32(255, 0, 0, 255); // upper-left  = red
        image[1, 0] = new Rgba32(255, 0, 0, 255); // upper-right = red
        image[0, 1] = new Rgba32(0, 0, 255, 255); // lower-left  = blue
        image[1, 1] = new Rgba32(0, 0, 255, 255); // lower-right = blue

        // Act: tgtW=2, tgtH=2 → 1 terminal row
        string[] lines = PreviewLoader.RenderPixelLines(image, 2, 2);

        // Assert
        Assert.Single(lines);
        string expected =
            "\x1b[48;2;255;0;0m\x1b[38;2;0;0;255m▄" +
            "\x1b[48;2;255;0;0m\x1b[38;2;0;0;255m▄" +
            "\x1b[0m";
        Assert.Equal(expected, lines[0]);
    }

    [Fact]
    public void RenderPixelLines_AlphaBlend_MultipliesAgainstBlack()
    {
        // Arrange: 1x2 image — upper pixel 50% transparent red → blends to (127,0,0)
        using var image = new Image<Rgba32>(1, 2);
        image[0, 0] = new Rgba32(255, 0, 0, 127); // 50% transparent red
        image[0, 1] = new Rgba32(0, 255, 0, 255); // opaque green

        string[] lines = PreviewLoader.RenderPixelLines(image, 1, 2);

        Assert.Single(lines);
        // upR = 255 * 127 / 255 = 127; loG = 255 * 255 / 255 = 255
        Assert.Contains("\x1b[48;2;127;0;0m", lines[0]); // background (upper pixel blended)
        Assert.Contains("\x1b[38;2;0;255;0m", lines[0]); // foreground (lower pixel opaque)
    }

    [Fact]
    public async Task LoadImagePreviewAsync_SvgLabel_ReturnsMetaWithoutPixelLines()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "test_img_preview.svg");
        File.WriteAllText(tempPath, "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'/>");
        try
        {
            var fileType = new FileType(FileCategory.Image, "SVG Image", "image/svg+xml");
            var result = await PreviewLoader.LoadImagePreviewAsync(
                tempPath, fileType, "2024-01-01 00:00", 80, 20, CancellationToken.None);

            Assert.Null(result.PixelLines);
            Assert.Equal(0, result.PixelWidth);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public async Task LoadImagePreviewAsync_IconLabel_ReturnsMetaWithoutPixelLines()
    {
        // ICO files are excluded from pixel rendering (same guard as SVG)
        string tempPath = Path.Combine(Path.GetTempPath(), "test_img_preview.ico");
        // Minimal fake ICO — LoadImageMeta will fail gracefully; we only need the type guard
        File.WriteAllBytes(tempPath, [0x00, 0x00, 0x01, 0x00, 0x00, 0x00]);
        try
        {
            var fileType = new FileType(FileCategory.Image, "Icon", "image/x-icon");
            var result = await PreviewLoader.LoadImagePreviewAsync(
                tempPath, fileType, "2024-01-01 00:00", 80, 20, CancellationToken.None);

            Assert.Null(result.PixelLines);
            Assert.Equal(0, result.PixelWidth);
        }
        finally { File.Delete(tempPath); }
    }
}
