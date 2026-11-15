using System.Windows.Media.Effects;

namespace DynamicIsland;

/// <summary>
/// Liquid glass (the "Жидкое стекло" theme): the island is clear frosted glass over what's really behind it,
/// and near its edge the glass acts like a lens — it bends the picture from just outside the rim inwards,
/// and splits the colours a little (red bends more than blue), the way thick curved glass does.
/// Done on the small screen grab the frosted glass already takes, so it costs next to nothing.
/// </summary>
public partial class MainWindow
{
    private bool LiquidGlass => _settings.Scene == "liquid" && _settings.Glass && !Lite;

    private byte[]? _liquidPixels;

    // The lens is worked out for the island's resting shape. While the island reaches for the cursor its
    // outline swells away from that shape, and the bent band would stay behind as an edge across the
    // swollen part. So the lens relaxes as the island reaches (1 = full bend, 0 = flat glass) and comes
    // back when it settles; the picture is small, so redoing it for a frame costs next to nothing.
    private (int W, int H, double X0, double Iw, double Ih, double Rr) _lens;
    private double _lensShown = 1;
    private double LensNow => 1 - Math.Clamp(_reach.Value, 0, 1);

    /// <summary>Every frame while the island moves: bend the backdrop as much as the reach allows right now.</summary>
    private void RelaxLens()
    {
        if (!LiquidGlass || _frostBmp == null || _frostPixels == null || _lens.W == 0) return;
        double lens = LensNow;
        if (Math.Abs(lens - _lensShown) < 0.02) return;
        if (_frostPixels.Length != _lens.W * _lens.H * 4 || _frostBmp.PixelWidth != _lens.W || _frostBmp.PixelHeight != _lens.H) return;
        _lensShown = lens;
        var shown = Refract(_frostPixels, _lens.W, _lens.H, _lens.X0, _lens.Iw, _lens.Ih, _lens.Rr, lens);
        _frostBmp.WritePixels(new System.Windows.Int32Rect(0, 0, _lens.W, _lens.H), shown, _lens.W * 4, 0);
    }

    /// <summary>Lighter blur than plain frost: matte, yet you can tell what's behind and see it bend.</summary>
    private void ApplyLiquidBlur()
    {
        if (Frost.Effect is BlurEffect b) b.Radius = LiquidGlass ? 6 : 36;
    }

    /// <summary>
    /// The grab (BGRX, w×h) covers the island plus a margin around it; the island is the rounded rectangle
    /// at (x0, 0) of size iw×ih with corner radius rr (all in grab pixels). Returns the bent picture.
    /// </summary>
    private byte[] Refract(byte[] src, int w, int h, double x0, double iw, double ih, double rr, double lens = 1)
    {
        if (_liquidPixels == null || _liquidPixels.Length != src.Length) _liquidPixels = new byte[src.Length];
        var dst = _liquidPixels;
        Buffer.BlockCopy(src, 0, dst, 0, src.Length);

        double cx = x0 + iw / 2, cy = ih / 2, bx = iw / 2, by = ih / 2;
        rr = Math.Min(rr, Math.Min(bx, by));
        double band = Math.Max(3, Math.Min(ih * 0.42, rr * 1.2 + 3));   // how far in from the rim the glass bends
        double push = band * 0.9;                                       // how far the picture is pulled towards the rim

        int yEnd = Math.Min(h, (int)Math.Ceiling(ih));
        int xStart = Math.Max(0, (int)x0), xEnd = Math.Min(w, (int)Math.Ceiling(x0 + iw));
        for (int y = 0; y < yEnd; y++)
        {
            double py = y + 0.5, dy = py - cy, qy = Math.Abs(dy) - (by - rr);
            for (int x = xStart; x < xEnd; x++)
            {
                double px = x + 0.5, dx = px - cx, qx = Math.Abs(dx) - (bx - rr);
                double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
                double d = Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - rr;   // < 0 inside
                int i = (y * w + x) * 4;
                if (d >= 0) continue;

                double t = -d / band;
                double nx, ny;
                if (qx > 0 && qy > 0) { double l = Math.Sqrt(qx * qx + qy * qy); nx = Math.Sign(dx) * qx / l; ny = Math.Sign(dy) * qy / l; }
                else if (qx > qy) { nx = Math.Sign(dx); ny = 0; }
                else { nx = 0; ny = Math.Sign(dy); }

                if (t < 1)
                {
                    // the lens: near the rim the picture under the glass is pulled out to the edge and squeezed,
                    // as if it curls over the rounded rim; red bends a bit more than blue — a thin rainbow fringe.
                    // (it reads from inside the glass, so it never runs off the grabbed picture)
                    double f = (1 - t) * (1 - t);
                    double off = f * push * lens;
                    dst[i + 2] = Sample(src, w, h, px - nx * off * 1.12, py - ny * off * 1.12, 2);
                    dst[i + 1] = Sample(src, w, h, px - nx * off, py - ny * off, 1);
                    dst[i + 0] = Sample(src, w, h, px - nx * off * 0.88, py - ny * off * 0.88, 0);
                    // light gathers in the rim
                    int glow = (int)(f * 18 * lens);
                    dst[i] = (byte)Math.Min(255, dst[i] + glow);
                    dst[i + 1] = (byte)Math.Min(255, dst[i + 1] + glow);
                    dst[i + 2] = (byte)Math.Min(255, dst[i + 2] + glow);
                }
                // glass makes colours a little richer
                int r = dst[i + 2], g = dst[i + 1], b = dst[i];
                int gray = (r * 77 + g * 151 + b * 28) >> 8;
                dst[i + 2] = Clamp(gray + (r - gray) * 5 / 4);
                dst[i + 1] = Clamp(gray + (g - gray) * 5 / 4);
                dst[i] = Clamp(gray + (b - gray) * 5 / 4);
            }
        }
        return dst;
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    private static byte Sample(byte[] src, int w, int h, double x, double y, int channel)
    {
        int ix = Math.Clamp((int)x, 0, w - 1), iy = Math.Clamp((int)y, 0, h - 1);
        return src[(iy * w + ix) * 4 + channel];
    }
}
