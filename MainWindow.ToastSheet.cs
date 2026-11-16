using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// "--cover --run toasts &lt;list&gt; &lt;folder&gt; [--lang xx]": shows every notice from the list on the island (demo data,
/// nothing of the user's is touched), saves them as numbered sheets and writes a report of what did not fit.
/// A line of the list: kind, icon, badge, title, text — separated by tabs.
///   kind:  toast (a notice on the island) or key (the small Caps Lock / layout pill)
///   icon:  the glyph, "" style escapes allowed, may be empty
///   badge: empty, #RRGGBB, a brush from the theme (Accent, Warm…) or "logo" (a program's picture)
///   title, text: a Russian UI string (translated like in the app), "=text" as it is, "string¦a¦b" with
///                values for {0} {1}; parts joined with " ++ "
/// </summary>
public partial class MainWindow
{
    private async Task ShootToastsAsync(string listFile, string dir)
    {
        Directory.CreateDirectory(dir);
        Island.IsHitTestVisible = false;
        _settings.ExpandOnHover = false;
        _settings.IslandScale = 100;
        ApplyIslandScale();
        await Task.Delay(5000);   // the island settles after the start
        SetMode(Mode.Compact);
        await Task.Delay(800);

        string lang = Loc.Lang;
        var report = new StringBuilder();
        var cells = new List<(int No, BitmapSource Shot)>();
        int no = 0, cut = 0;

        foreach (var raw in File.ReadAllLines(listFile, Encoding.UTF8))
        {
            if (raw.Length == 0 || raw.StartsWith('#')) continue;
            var f = raw.Split('\t');
            if (f.Length < 5) { report.AppendLine("bad line: " + raw); continue; }
            no++;
            string glyph = System.Text.RegularExpressions.Regex.Unescape(f[1]), title = SheetText(f[3]), text = SheetText(f[4]);

            if (f[0] == "key")
            {
                Color? accent = f[2].Length > 0 ? (Color)ColorConverter.ConvertFromString(f[2]) : null;
                SetMode(Mode.Compact);
                await Task.Delay(700);
                ShowKeyPill(glyph, title, text.Length > 0 ? text : null, accent);
                await Task.Delay(500);
                UpdateLayout();
                var all = new RenderTargetBitmap((int)(Root.ActualWidth * 1.5), (int)(Root.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                all.Render(Root);
                var at = _keyPill!.TranslatePoint(new Point(0, 0), Root);
                double right = at.X + _keyPill.ActualWidth;
                bool inside = right <= Root.ActualWidth + 0.5 && at.X >= 0;
                if (!inside) cut++;
                report.AppendLine($"{no:00}  key   {(inside ? "ok " : "CUT")}  pill {at.X:0}…{right:0} of {Root.ActualWidth:0}  | {title} | {text}");
                cells.Add((no, SheetCrop(all, new Rect(Math.Max(0, right + 10 - 476), 0, 476, 82))));   // the pill sits to the right of the island
                await Task.Delay(1500);   // the pill fades by itself
                continue;
            }

            Brush? badge = f[2] switch
            {
                "" => null,
                "logo" => new ImageBrush(DemoArt()) { Stretch = Stretch.UniformToFill },
                var c when c.StartsWith('#') => new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)),
                var name => (Brush)FindResource(name),
            };
            ShowToast(glyph, title, text.Length > 0 ? text : null, null, badge, ms: 60000);
            await Task.Delay(450);

            // the same freeze as Snap: the final size, nothing mid-morph
            _w.Snap(_w.Target); _h.Snap(_h.Target); _y.Snap(0);
            Island.Width = _w.Value; Island.Height = _h.Value;
            UpdateLayout(); UpdateShape(false); UpdateLayout();

            bool cutTitle = SheetCut(ToastTitle), cutText = SheetCut(ToastText);
            bool noIcon = glyph.Length == 0 && badge == null;
            // the island must have taken the width this notice asks for, not the one of the notice before it
            double wanted = TargetSize(Mode.Toast).Width;
            bool stale = Math.Abs(wanted - Island.Width) > 1;
            bool bad = cutTitle || cutText || noIcon || stale;
            if (bad) cut++;
            report.AppendLine($"{no:00}  toast {(cutTitle ? "CUT-TITLE " : "")}{(cutText ? "CUT-TEXT " : "")}{(noIcon ? "NO-ICON " : "")}{(stale ? $"WRONG-WIDTH(wants {wanted:0}) " : "")}{(bad ? "" : "ok ")} w={Island.Width:0}  | {title} | {text}");

            var rtb = new RenderTargetBitmap((int)(Root.ActualWidth * 1.5), (int)(Root.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            rtb.Render(Root);
            var b = Island.TransformToAncestor(Root).TransformBounds(new Rect(Island.RenderSize));
            cells.Add((no, SheetCrop(rtb, new Rect(b.X + b.Width / 2 - 238, Math.Max(0, b.Y - 4), 476, 82))));
        }

        // sheets: two columns, twelve rows, each cell numbered like the report
        const int cols = 2, rows = 12, cw = 714, ch = 123;
        for (int page = 0; page * cols * rows < cells.Count; page++)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40)), null, new Rect(0, 0, cols * cw, rows * ch));
                for (int i = 0; i < cols * rows && page * cols * rows + i < cells.Count; i++)
                {
                    var (n, shot) = cells[page * cols * rows + i];
                    double x = i % cols * cw, y = i / cols * ch;
                    dc.DrawImage(shot, new Rect(x, y, cw, ch));
                    dc.DrawText(new FormattedText(n.ToString("00"), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 15, Brushes.Gold, 1), new Point(x + 6, y + 4));
                }
            }
            var sheet = new RenderTargetBitmap(cols * cw, rows * ch, 96, 96, PixelFormats.Pbgra32);
            sheet.Render(dv);
            CaptureService.SavePng(sheet, Path.Combine(dir, $"{lang}-sheet{page + 1}.png"));
        }

        report.Insert(0, $"{lang}: {no} notices, {cut} with something that does not fit{Environment.NewLine}");
        File.WriteAllText(Path.Combine(dir, $"{lang}-report.txt"), report.ToString(), Encoding.UTF8);
        Application.Current.Shutdown();
    }

    /// <summary>A text of the list: translated like the app does it, with the values for {0} filled in.</summary>
    private static string SheetText(string spec) => string.Concat(spec.Split(" ++ ").Select(part =>
    {
        if (part.StartsWith('=')) return part[1..];
        var p = part.Split('¦');
        return p.Length == 1 ? L.T(p[0]) : L.F(p[0], p.Skip(1).Select(a => (object?)SheetText(a)).ToArray());
    }));

    /// <summary>The text is wider than the room it got: it ends in "…".</summary>
    private bool SheetCut(TextBlock t)
    {
        if (t.Visibility != Visibility.Visible || t.Text.Length == 0) return false;
        // the same text with all the room it wants, measured the way the island measures it
        var free = new TextBlock { Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, FontWeight = t.FontWeight, FontStyle = t.FontStyle, FontStretch = t.FontStretch };
        TextOptions.SetTextFormattingMode(free, TextOptions.GetTextFormattingMode(t));
        free.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return free.DesiredSize.Width > t.ActualWidth + 0.5;
    }

    private static BitmapSource SheetCrop(BitmapSource all, Rect dips)
    {
        const double s = 1.5;
        int x = (int)Math.Clamp(dips.X * s, 0, all.PixelWidth - 1), y = (int)Math.Clamp(dips.Y * s, 0, all.PixelHeight - 1);
        int w = (int)Math.Min(dips.Width * s, all.PixelWidth - x), h = (int)Math.Min(dips.Height * s, all.PixelHeight - y);
        var crop = new CroppedBitmap(all, new Int32Rect(x, y, w, h));
        crop.Freeze();
        return crop;
    }
}
