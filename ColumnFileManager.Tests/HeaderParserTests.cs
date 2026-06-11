using Xunit;

public class HeaderParserTests
{
    [Fact]
    public void ParsePng_ReturnsCorrectDimensions()
    {
        // PNG IHDR: bytes 16-19 = width (BE), 20-23 = height (BE)
        byte[] header = new byte[30];
        // PNG signature
        byte[] sig = [0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A];
        sig.CopyTo(header, 0);
        // IHDR length + type
        header[8]=0; header[9]=0; header[10]=0; header[11]=16;
        header[12]=0x49; header[13]=0x48; header[14]=0x44; header[15]=0x52; // IHDR
        // width = 1920 = 0x00000780
        header[16]=0; header[17]=0; header[18]=0x07; header[19]=0x80;
        // height = 1080 = 0x00000438
        header[20]=0; header[21]=0; header[22]=0x04; header[23]=0x38;
        header[24]=8; // bit depth
        header[25]=2; // color type = RGB

        var (w, h, bits, color) = PreviewLoader.ParsePngHeader(header);
        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
        Assert.Equal(8, bits);
        Assert.Equal("RGB", color);
    }

    [Fact]
    public void ParseGif_ReturnsCorrectDimensions()
    {
        // GIF header: bytes 6-7 = width (LE), 8-9 = height (LE)
        byte[] header = new byte[16];
        header[0]=0x47; header[1]=0x49; header[2]=0x46; header[3]=0x38; // GIF8
        header[4]=0x39; header[5]=0x61; // 9a
        header[6]=0x80; header[7]=0x02; // width = 640 (LE)
        header[8]=0xE0; header[9]=0x01; // height = 480 (LE)

        var (w, h) = PreviewLoader.ParseGifHeader(header);
        Assert.Equal(640, w);
        Assert.Equal(480, h);
    }

    [Fact]
    public void ParseBmp_ReturnsCorrectDimensions()
    {
        // BMP: bytes 18-21 = width (LE int32), 22-25 = height (LE int32)
        byte[] header = new byte[30];
        header[0]=0x42; header[1]=0x4D; // BM
        // width = 800 at offset 18
        byte[] wBytes = BitConverter.GetBytes(800);
        byte[] hBytes = BitConverter.GetBytes(600);
        wBytes.CopyTo(header, 18);
        hBytes.CopyTo(header, 22);

        var (w, h) = PreviewLoader.ParseBmpHeader(header);
        Assert.Equal(800, w);
        Assert.Equal(600, h);
    }

    [Fact]
    public void ParsePdf_DetectsVersion()
    {
        byte[] header = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n");
        string ver = PreviewLoader.ParsePdfVersion(header);
        Assert.Equal("1.7", ver);
    }
}
