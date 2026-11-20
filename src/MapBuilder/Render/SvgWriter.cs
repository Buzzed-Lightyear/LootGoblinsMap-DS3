using System.Globalization;
using System.Text;
using MapBuilder.Pipeline;
using MapCore;

namespace MapBuilder.Render;

public static class SvgWriter
{
    public static void WriteLayer(string path, Layer layer, List<(double X, double Y)[]> polysPx,
                                  List<((double X, double Y) A, (double X, double Y) B)> boundaryPx,
                                  List<Marker> markers, MapTransform t)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{t.Width}\" height=\"{t.Height}\" viewBox=\"0 0 {t.Width} {t.Height}\">");
        sb.AppendLine($"<rect width=\"{t.Width}\" height=\"{t.Height}\" fill=\"#1b1f24\"/>");

        sb.Append("<path fill=\"#8d98a6\" fill-rule=\"nonzero\" stroke=\"none\" d=\"");
        foreach (var poly in polysPx)
        {
            sb.Append('M');
            for (int i = 0; i < poly.Length; i++)
            {
                if (i > 0) sb.Append('L');
                sb.Append(F(poly[i].X)).Append(' ').Append(F(poly[i].Y));
            }
            sb.Append("Z ");
        }
        sb.AppendLine("\"/>");

        sb.Append($"<path stroke=\"#2d333b\" stroke-width=\"1.2\" fill=\"none\" d=\"");
        foreach (var (a, b) in boundaryPx)
            sb.Append($"M{F(a.X)} {F(a.Y)}L{F(b.X)} {F(b.Y)}");
        sb.AppendLine("\"/>");

        foreach (var m in markers.Where(m => m.Layer == layer.Index))
        {
            if (m.Type == "bonfire")
            {
                sb.AppendLine($"<circle cx=\"{F(m.PixelX)}\" cy=\"{F(m.PixelY)}\" r=\"6\" fill=\"#ff8c1a\" stroke=\"#4d2600\" stroke-width=\"1.5\"><title>bonfire {Esc(m.Name)} entity {m.EntityId}</title></circle>");
            }
            else
            {
                sb.AppendLine($"<rect x=\"{F(m.PixelX - 4)}\" y=\"{F(m.PixelY - 4)}\" width=\"8\" height=\"8\" transform=\"rotate(45 {F(m.PixelX)} {F(m.PixelY)})\" fill=\"#ffd633\" stroke=\"#665200\" stroke-width=\"1.2\"><title>treasure {Esc(m.Name)} itemlots [{string.Join(",", m.ItemLots)}]</title></rect>");
            }
        }

        sb.AppendLine($"<text x=\"10\" y=\"24\" fill=\"#c9d1d9\" font-family=\"sans-serif\" font-size=\"16\">layer {layer.Index}  y [{layer.MinY:F1} .. {layer.MaxY:F1}] m</text>");
        sb.AppendLine("</svg>");
        File.WriteAllText(path, sb.ToString());
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Esc(string s) =>
        s?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;") ?? "";
}
