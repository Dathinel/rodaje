using Rodaje.Core.Model;

namespace Rodaje.Text;

/// <summary>Una unidad de un título animado por unidades (letra, palabra o línea), en píxeles con el origen en el centro de la caja.</summary>
public readonly record struct TitleUnitBox(int Start, int End, int Line, int Word, float Left, float Right, float Top, float Bottom);

/// <summary>Las unidades de un título como las parte <c>TitleRasterizer.Segment</c> en Windows: sin espacios, con su palabra y su línea.</summary>
public static class TitleUnits
{
    public static IReadOnlyList<TitleUnitBox> Segment(TitleLayout layout, TextUnit granularity)
    {
        ArgumentNullException.ThrowIfNull(layout);
        float offsetX = layout.Width / 2;
        float offsetY = layout.Height / 2;
        var units = new List<TitleUnitBox>();
        int word = -1;
        bool previousBlank = true;
        int previousLine = -1;
        foreach (TitleCluster cluster in layout.Clusters)
        {
            bool blank = cluster.IsWhitespace || cluster.IsNewline;
            if (!blank)
            {
                if (previousBlank || cluster.Line != previousLine)
                {
                    word++;
                }

                TitleLine line = layout.Lines[cluster.Line];
                float left = cluster.X - offsetX;
                float right = cluster.X + cluster.Advance - offsetX;
                // Como HitTestTextPosition: el alto de la letra alrededor de la línea base, con las métricas de su fuente.
                float scale = layout.Size / cluster.Font.UnitsPerEm;
                float baseline = line.Top + line.Baseline - offsetY;
                float top = baseline - (cluster.Font.Ascent * scale);
                float bottom = baseline + (cluster.Font.Descent * scale);
                bool joins = units.Count > 0 && granularity switch
                {
                    TextUnit.Letter => false,
                    TextUnit.Word => units[^1].Word == word,
                    TextUnit.Line => units[^1].Line == cluster.Line,
                    _ => true,
                };
                if (joins)
                {
                    TitleUnitBox last = units[^1];
                    units[^1] = last with
                    {
                        End = cluster.Start + cluster.Length,
                        Left = Math.Min(last.Left, left),
                        Right = Math.Max(last.Right, right),
                        Top = Math.Min(last.Top, top),
                        Bottom = Math.Max(last.Bottom, bottom),
                    };
                }
                else
                {
                    units.Add(new TitleUnitBox(cluster.Start, cluster.Start + cluster.Length, cluster.Line, word, left, right, top, bottom));
                }

                previousLine = cluster.Line;
            }

            previousBlank = blank;
        }

        return units;
    }
}
