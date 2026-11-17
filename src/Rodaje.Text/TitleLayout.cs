using System.Globalization;
using System.Text;
using HarfBuzzSharp;
using Rodaje.Core.Model;
using Rodaje.Core.Text;
using Rodaje.Playback.Composition;
using Buffer = HarfBuzzSharp.Buffer;

namespace Rodaje.Text;

/// <summary>Un grupo de caracteres que se dibuja junto (letra con sus acentos, ligadura…), como los clústeres de DirectWrite.</summary>
/// <param name="X">Borde de entrada en la caja del texto, ya alineado.</param>
/// <param name="Advance">Avance, con el espaciado entre letras.</param>
public readonly record struct TitleCluster(int Start, int Length, int Line, float X, float Advance, bool IsWhitespace, bool IsNewline, ShapingFont Font);

/// <param name="Width">Ancho sin el espacio en blanco del final.</param>
/// <param name="WidthWithTrailing">Ancho con el espacio en blanco del final.</param>
public readonly record struct TitleLine(int Start, int Length, float Top, float Height, float Baseline, float Left, float Width, float WidthWithTrailing);

/// <summary>
/// El texto de un título armado fuera de Windows: lo mismo que el layout de DirectWrite que arma <c>TitleRasterizer</c>
/// (mayúsculas, tamaño, ajuste a la caja, saltos de línea, alineación, interlineado uniforme, espaciado, kerning y respaldo
/// de fuentes), con HarfBuzz para dar forma a cada tramo (D-066, fase 5).
/// </summary>
/// <remarks>
/// Shortcut: escritura de izquierda a derecha, sin tramos con otro peso, cursiva o tamaño y con el interlineado uniforme de
/// siempre (DirectWrite usa el proporcional si hay letras de varios tamaños). Cuando la interfaz de Linux dibuje títulos con
/// tramos o texto bidireccional, se añade aquí con sus casos de aceptación.
/// </remarks>
public sealed class TitleLayout
{
    private const float Unbounded = 1_000_000;

    private TitleLayout(string drawn, float size, float width, float height, IReadOnlyList<TitleLine> lines, IReadOnlyList<TitleCluster> clusters,
        bool fontMissing)
    {
        Drawn = drawn;
        Size = size;
        Width = width;
        Height = height;
        Lines = lines;
        Clusters = clusters;
        FontMissing = fontMissing;
    }

    /// <summary>El texto con las mayúsculas aplicadas.</summary>
    public string Drawn { get; }

    public float Size { get; }

    /// <summary>Ancho de la caja del texto: el de la caja del estilo si hay, si no el del texto con su espacio final.</summary>
    public float Width { get; }

    public float Height { get; }

    public IReadOnlyList<TitleLine> Lines { get; }

    public IReadOnlyList<TitleCluster> Clusters { get; }

    public bool FontMissing { get; }

    /// <summary>Ancho del texto sin el espacio final de cada línea (<c>DWRITE_TEXT_METRICS.width</c>).</summary>
    public float TextWidth => Lines.Count == 0 ? 0 : Lines.Max(l => l.Width);

    /// <summary>Arma <paramref name="content"/> como <c>TitleRasterizer.LayOut</c>: mayúsculas, tamaño y, si el estilo lo pide, ajuste a la caja.</summary>
    public static TitleLayout Create(GeneratedContent content, TitleFontSet fonts)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fonts);
        TextStyle style = content.Style;
        StyleTypography? typography = style.Typography;
        string drawn = TextCasing.Apply(content.Text, typography?.Case ?? TextCase.None);
        float size = (float)(style.Size * content.UnitScale);
        (ShapingFont font, bool missing) = fonts.Resolve(style.Font);
        float factor = 1;
        if (typography is { Fit: not TextFit.None } && (content.BoxWidth > 0 || content.BoxHeight > 0))
        {
            bool Fits(float f)
            {
                TitleLayout trial = Lay(content, fonts, font, missing, drawn, size * f, f);
                return (content.BoxWidth <= 0 || trial.TextWidth <= content.BoxWidth + 0.5) && (content.BoxHeight <= 0 || trial.Height <= content.BoxHeight + 0.5);
            }

            bool grow = typography.Fit == TextFit.Fill;
            if (grow || !Fits(1))
            {
                float lo = 0.02f;
                float hi = grow ? 16 : 1;
                if (Fits(lo))
                {
                    for (int i = 0; i < 16; i++)
                    {
                        float mid = (lo + hi) / 2;
                        (lo, hi) = Fits(mid) ? (mid, hi) : (lo, mid);
                    }
                }

                factor = lo;
            }
        }

        return Lay(content, fonts, font, missing, drawn, size * factor, factor);
    }

    private static TitleLayout Lay(GeneratedContent content, TitleFontSet fonts, ShapingFont primary, bool missing, string drawn, float size, float factor)
    {
        TextStyle style = content.Style;
        bool wrap = content.BoxWidth > 0;
        float maxWidth = wrap ? (float)content.BoxWidth : Unbounded;
        float letterSpacing = (float)(style.LetterSpacing * content.UnitScale * factor);
        if (!float.IsFinite(letterSpacing))
        {
            letterSpacing = 0;
        }

        double lineHeight = double.IsFinite(style.LineHeight) && style.LineHeight > 0 ? style.LineHeight : 1.2;
        float spacing = (float)(lineHeight * size);
        Feature[] features = Features(style.Typography);

        List<Shaped> shaped = Shape(drawn, size, letterSpacing, primary, fonts, features);
        List<(int First, int Count)> lineClusters = Break(shaped, wrap, maxWidth);
        if (lineClusters.Count == 0 || (drawn.Length > 0 && IsNewline(drawn[^1])))
        {
            lineClusters.Add((shaped.Count, 0));
        }

        var lines = new List<TitleLine>(lineClusters.Count);
        var widths = new (float Width, float WithTrailing)[lineClusters.Count];
        for (int l = 0; l < lineClusters.Count; l++)
        {
            (int first, int count) = lineClusters[l];
            float total = 0;
            float visible = 0;
            for (int c = first; c < first + count; c++)
            {
                total += shaped[c].Advance;
                if (!shaped[c].IsWhitespace)
                {
                    visible = total;
                }
            }

            widths[l] = (visible, total);
        }

        float layoutWidth = wrap ? maxWidth : Math.Max(1, widths.Max(w => w.WithTrailing));
        float layoutHeight = Math.Max(1, spacing * lineClusters.Count);
        var clusters = new List<TitleCluster>(shaped.Count);
        for (int l = 0; l < lineClusters.Count; l++)
        {
            (int first, int count) = lineClusters[l];
            float left = style.Align switch
            {
                TextAlign.Left => 0,
                TextAlign.Right => layoutWidth - widths[l].Width,
                _ => (layoutWidth - widths[l].Width) / 2,
            };
            float x = left;
            for (int c = first; c < first + count; c++)
            {
                Shaped s = shaped[c];
                clusters.Add(new TitleCluster(s.Start, s.Length, l, x, s.Advance, s.IsWhitespace, s.IsNewline, s.Font));
                x += s.Advance;
            }

            int start = count > 0 ? shaped[first].Start : drawn.Length;
            int end = count > 0 ? shaped[first + count - 1].Start + shaped[first + count - 1].Length : drawn.Length;
            lines.Add(new TitleLine(start, end - start, l * spacing, spacing, spacing * 0.8f, left, widths[l].Width, widths[l].WithTrailing));
        }

        return new TitleLayout(drawn, size, layoutWidth, layoutHeight, lines, clusters, missing);
    }

    private static Feature[] Features(StyleTypography? typography)
    {
        if (typography is null)
        {
            return [];
        }

        var features = new List<Feature>();
        if (!typography.Kerning)
        {
            features.Add(Feature.Parse("kern=0"));
        }

        foreach (string feature in typography.Features.Where(StyleTypography.IsFeature))
        {
            features.Add(Feature.Parse(feature[0] == '-' ? feature[1..] + "=0" : feature + "=1"));
        }

        return [.. features];
    }

    private static bool IsNewline(char c) => c is '\n' or '\r' or ' ' or ' ' or '\u0085' or '\u000B' or '\u000C';

    /// <summary>Los clústeres del texto entero, en orden: cada párrafo partido en tramos de una sola fuente y cada salto aparte, con ancho cero.</summary>
    private static List<Shaped> Shape(string text, float size, float letterSpacing, ShapingFont primary, TitleFontSet fonts, Feature[] features)
    {
        var result = new List<Shaped>(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (IsNewline(text[i]))
            {
                int length = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                result.Add(new Shaped(i, length, 0, IsWhitespace: true, IsNewline: true, primary));
                i += length;
                continue;
            }

            int end = i;
            while (end < text.Length && !IsNewline(text[end]))
            {
                end++;
            }

            foreach ((int start, int length, ShapingFont font) in Runs(text, i, end, primary, fonts))
            {
                ShapeRun(text, start, length, font, size, letterSpacing, features, result);
            }

            i = end;
        }

        return result;
    }

    /// <summary>Tramos de una sola fuente: la del título si tiene la letra; si no, la de respaldo. Las marcas siguen a su letra.</summary>
    private static IEnumerable<(int Start, int Length, ShapingFont Font)> Runs(string text, int start, int end, ShapingFont primary, TitleFontSet fonts)
    {
        int runStart = start;
        ShapingFont? runFont = null;
        int i = start;
        while (i < end)
        {
            Rune rune = Rune.GetRuneAt(text, i);
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            ShapingFont font;
            if (runFont is not null && category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format
                && (runFont.Has(rune.Value) || !primary.Has(rune.Value)))
            {
                font = runFont;
            }
            else if (primary.Has(rune.Value) || Rune.IsWhiteSpace(rune) || Rune.IsControl(rune))
            {
                font = primary;
            }
            else
            {
                font = fonts.FallbackFor(rune.Value) ?? primary;
            }

            if (runFont is not null && !ReferenceEquals(font, runFont))
            {
                yield return (runStart, i - runStart, runFont);
                runStart = i;
            }

            runFont = font;
            i += rune.Utf16SequenceLength;
        }

        if (runFont is not null)
        {
            yield return (runStart, end - runStart, runFont);
        }
    }

    private static void ShapeRun(string text, int start, int length, ShapingFont font, float size, float letterSpacing, Feature[] features, List<Shaped> result)
    {
        using var buffer = new Buffer();
        // Todo el texto como contexto, para que los clústeres salgan con su índice en el texto entero.
        buffer.AddUtf16(text, start, length);
        buffer.Direction = Direction.LeftToRight;
        buffer.Language = new Language("es");
        buffer.GuessSegmentProperties();
        font.Font.Shape(buffer, features);
        GlyphInfo[] infos = buffer.GlyphInfos;
        GlyphPosition[] positions = buffer.GlyphPositions;
        float scale = size / font.UnitsPerEm;

        // Cada clúster va de su primer índice al del siguiente; su avance es la suma de sus glifos.
        var starts = new SortedDictionary<int, float>();
        for (int g = 0; g < infos.Length; g++)
        {
            int cluster = (int)infos[g].Cluster;
            starts[cluster] = (starts.TryGetValue(cluster, out float sum) ? sum : 0) + (positions[g].XAdvance * scale);
        }

        int[] keys = [.. starts.Keys.Where(k => k >= start && k < start + length)];
        if (keys.Length == 0 || keys[0] != start)
        {
            keys = [start, .. keys.Where(k => k != start)];
        }

        for (int k = 0; k < keys.Length; k++)
        {
            int clusterStart = keys[k];
            int clusterEnd = k + 1 < keys.Length ? keys[k + 1] : start + length;
            float advance = (starts.TryGetValue(clusterStart, out float a) ? a : 0) + letterSpacing;
            bool blank = true;
            for (int c = clusterStart; c < clusterEnd; c++)
            {
                blank &= char.IsWhiteSpace(text[c]);
            }

            result.Add(new Shaped(clusterStart, clusterEnd - clusterStart, advance, blank, IsNewline: false, font));
        }
    }

    /// <summary>
    /// Las líneas, como índices de clústeres: un párrafo por salto y, con caja, palabra a palabra mientras quepan sin contar el
    /// espacio final; una palabra que no cabe sola se parte por clústeres.
    /// </summary>
    private static List<(int First, int Count)> Break(List<Shaped> clusters, bool wrap, float maxWidth)
    {
        var lines = new List<(int First, int Count)>();
        int i = 0;
        while (i < clusters.Count)
        {
            int paragraphEnd = i;
            while (paragraphEnd < clusters.Count && !clusters[paragraphEnd].IsNewline)
            {
                paragraphEnd++;
            }

            // El salto va al final de la línea que cierra.
            int closing = paragraphEnd < clusters.Count ? paragraphEnd + 1 : paragraphEnd;
            if (!wrap || paragraphEnd == i)
            {
                lines.Add((i, closing - i));
                i = closing;
                continue;
            }

            int lineStart = i;
            float lineTotal = 0;
            int c = i;
            while (c < paragraphEnd)
            {
                // Una palabra: sus clústeres visibles y los espacios que la siguen.
                int wordEnd = c;
                float wordVisible = 0;
                while (wordEnd < paragraphEnd && !clusters[wordEnd].IsWhitespace)
                {
                    wordVisible += clusters[wordEnd].Advance;
                    wordEnd++;
                }

                int spacesEnd = wordEnd;
                float spaces = 0;
                while (spacesEnd < paragraphEnd && clusters[spacesEnd].IsWhitespace)
                {
                    spaces += clusters[spacesEnd].Advance;
                    spacesEnd++;
                }

                bool empty = c == lineStart;
                if (empty || lineTotal + wordVisible <= maxWidth + 0.001f)
                {
                    if (empty && wordVisible > maxWidth + 0.001f)
                    {
                        // Ni sola cabe: se corta por clústeres, al menos uno por línea.
                        float run = 0;
                        int cut = c;
                        while (cut < wordEnd && (cut == c || run + clusters[cut].Advance <= maxWidth + 0.001f))
                        {
                            run += clusters[cut].Advance;
                            cut++;
                        }

                        if (cut < wordEnd)
                        {
                            lines.Add((lineStart, cut - lineStart));
                            lineStart = cut;
                            lineTotal = 0;
                            c = cut;
                            continue;
                        }
                    }

                    lineTotal += wordVisible + spaces;
                    c = spacesEnd;
                    continue;
                }

                lines.Add((lineStart, c - lineStart));
                lineStart = c;
                lineTotal = 0;
            }

            lines.Add((lineStart, closing - lineStart));
            i = closing;
        }

        return lines;
    }

    private readonly record struct Shaped(int Start, int Length, float Advance, bool IsWhitespace, bool IsNewline, ShapingFont Font);
}
