using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Runtime.CompilerServices;
using System.Text;

// All ImageSharp type references are confined here so callers can be JIT-compiled
// without the DLL present. A missing DLL raises TypeLoadException on first call to
// any method below, which the callers' catch-all handlers treat as graceful fallback.
internal static class ImageSharpProxy
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static (string[] Lines, int Width) LoadAndRender(
        string filePath, int pixelCols, int pixelRows, CancellationToken ct)
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
        return (RenderPixelLines(image, tgtW, tgtH), tgtW);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static (string[] Lines, int Width) LoadFrameAndRender(
        string cachePath, int pixelCols, int pixelRows, CancellationToken ct)
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
        return (RenderPixelLines(image, tgtW, tgtH), tgtW);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string[] RenderPixelLines(Image<Rgba32> image, int tgtW, int tgtH)
    {
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

                byte upR = (byte)(up.R * up.A / 255);
                byte upG = (byte)(up.G * up.A / 255);
                byte upB = (byte)(up.B * up.A / 255);
                byte loR = (byte)(lo.R * lo.A / 255);
                byte loG = (byte)(lo.G * lo.A / 255);
                byte loB = (byte)(lo.B * lo.A / 255);

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
}
