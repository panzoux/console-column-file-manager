using Xunit;

public class HexFormatterTests
{
    [Theory]
    [InlineData(32,  4)]
    [InlineData(48,  8)]
    [InlineData(64, 12)]
    [InlineData(80, 16)]
    [InlineData(20,  4)]  // below minimum clamps to 4
    public void BytesPerRow_MatchesWidth(int previewWidth, int expected)
    {
        int actual = PreviewLoader.CalcBytesPerRow(previewWidth);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FormatHexLines_FourBytesPerRow_FormatsCorrectly()
    {
        byte[] data = [0x53, 0x51, 0x4C, 0x69];
        string[] lines = PreviewLoader.FormatHexLines(data, 4, 32);
        Assert.Single(lines);
        Assert.Equal("00000000  53 51 4C 69  SQLi", lines[0]);
    }

    [Fact]
    public void FormatHexLines_NonPrintableAscii_ShowsDot()
    {
        byte[] data = [0x00, 0x01, 0x41, 0x42];
        string[] lines = PreviewLoader.FormatHexLines(data, 4, 32);
        Assert.Single(lines);
        Assert.Contains("..AB", lines[0]);
    }
}
