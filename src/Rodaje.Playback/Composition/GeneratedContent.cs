using System.Globalization;
using System.Text;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Playback.Composition;

/// <summary>
/// Lo que hay que dibujar en una capa generada (un título): texto, copia del estilo, caja y escala a la que se rasteriza.
/// El motor lo arma una vez por clip al armar el plan; lo rasteriza quien tiene el dispositivo (Render). Dos descriptores
/// con la misma <see cref="Key"/> dan la misma imagen, aunque vengan de planes distintos.
/// </summary>
/// <remarks>
/// Medidas en píxeles de la secuencia: <see cref="UnitScale"/> lleva las del estilo (en píxeles de su
/// <see cref="TextStyle.Reference"/>) a la secuencia. La textura se rasteriza a la secuencia × <see cref="RasterScale"/>,
/// la mayor escala con que se llega a ver, para que no se vea borrosa al crecer. <see cref="Style"/> no se modifica.
/// </remarks>
public sealed class GeneratedContent : IEquatable<GeneratedContent>
{
    private readonly int _hash;

    /// <summary>Con <c>highlightStart</c> ≥ 0 y un estilo con <see cref="TextStyle.Highlight"/>, esa palabra va resaltada (karaoke).</summary>
    public GeneratedContent(string kind, string text, TextStyle style, double boxWidth, int sequenceWidth, int sequenceHeight, double rasterScale,
        int highlightStart = -1, int highlightLength = 0)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequenceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequenceHeight);
        Kind = kind;
        Text = text;
        Style = style;
        BoxWidth = double.IsFinite(boxWidth) && boxWidth > 0 ? boxWidth : 0;
        SequenceWidth = sequenceWidth;
        SequenceHeight = sequenceHeight;
        RasterScale = double.IsFinite(rasterScale) && rasterScale > 0 ? rasterScale : 1;
        UnitScale = UnitScaleFor(style.Reference, sequenceWidth, sequenceHeight);
        bool highlighted = style.Highlight is not null && highlightStart >= 0 && highlightLength > 0 && highlightStart + highlightLength <= text.Length;
        HighlightStart = highlighted ? highlightStart : -1;
        HighlightLength = highlighted ? highlightLength : 0;
        Key = BuildKey();
        _hash = StringComparer.Ordinal.GetHashCode(Key);

        // Una referencia que nadie suelta: cada capa toma y devuelve una, y el índice cambia con el contenido (la app
        // compara índices para saber si el cuadro cambió).
        Handle = new DecodedFrame(_hash & int.MaxValue, Time.Zero, Time.FromTicks(1), null, 0);
    }

    /// <summary><see cref="GeneratorKinds.Text"/> hoy; un tipo desconocido no se dibuja.</summary>
    public string Kind { get; }

    public string Text { get; }

    public TextStyle Style { get; }

    /// <summary>Ancho de la caja de ajuste de línea en píxeles de la secuencia; 0 si el texto solo corta en <c>\n</c>.</summary>
    public double BoxWidth { get; }

    public int SequenceWidth { get; }

    public int SequenceHeight { get; }

    /// <summary>Píxeles de la secuencia por píxel de la referencia del estilo: <c>min(ancho/refAncho, alto/refAlto)</c>.</summary>
    public double UnitScale { get; }

    /// <summary>Mayor escala con que se ve (animación del estilo × transformación del clip): la de la textura.</summary>
    public double RasterScale { get; }

    /// <summary>Primer carácter de la palabra que se pinta con <see cref="TextStyle.Highlight"/>; -1 si ninguna.</summary>
    public int HighlightStart { get; }

    public int HighlightLength { get; }

    /// <summary>Clip del que salió, para los avisos (por ejemplo, la fuente que falta); no forma parte de <see cref="Key"/>.</summary>
    public string ClipId { get; init; } = "";

    /// <summary>Todo lo que cambia la imagen, en texto; sirve de clave de caché.</summary>
    public string Key { get; }

    internal DecodedFrame Handle { get; }

    /// <summary>Factor de la referencia del estilo a la secuencia; 1 si la referencia no es válida.</summary>
    public static double UnitScaleFor(StyleReference reference, int sequenceWidth, int sequenceHeight)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.Width > 0 && reference.Height > 0
            ? Math.Min((double)sequenceWidth / reference.Width, (double)sequenceHeight / reference.Height)
            : 1;
    }

    public bool Equals(GeneratedContent? other) =>
        other is not null && (ReferenceEquals(this, other) || (_hash == other._hash && string.Equals(Key, other.Key, StringComparison.Ordinal)));

    public override bool Equals(object? obj) => Equals(obj as GeneratedContent);

    public override int GetHashCode() => _hash;

    public override string ToString() => $"{Kind}: {(Text.Length > 32 ? Text[..32] + "…" : Text)}";

    /// <summary>Una referencia más del cuadro de esta capa (nunca falla: el descriptor guarda la suya).</summary>
    internal DecodedFrame Retain()
    {
        Handle.TryRetain();
        return Handle;
    }

    private string BuildKey()
    {
        TextStyle s = Style;
        var b = new StringBuilder(128 + Text.Length);
        Append(b, Kind).Append(Text.Length).Append(':').Append(Text).Append('\u001f');
        Append(b, s.Font.Family).Append(s.Font.Weight).Append(s.Font.Italic ? 'i' : 'n').Append('\u001f');
        Append(b, s.Font.Source ?? "");
        foreach (string fallback in s.Font.Fallback)
        {
            Append(b, fallback);
        }

        Number(b, s.Size).Append(s.Reference.Width).Append('x').Append(s.Reference.Height).Append('|');
        Number(b, s.LineHeight);
        Number(b, s.LetterSpacing).Append((int)s.Align).Append('|');
        Append(b, s.Fill.Color);
        if (s.Stroke is { } stroke)
        {
            Append(b, "S" + stroke.Color);
            Number(b, stroke.Width).Append((int)stroke.Join).Append('|');
        }

        if (s.Shadow is { } shadow)
        {
            Append(b, "D" + shadow.Color);
            Number(Number(Number(b, shadow.Dx), shadow.Dy), shadow.Blur);
        }

        if (s.Background is { } background)
        {
            Append(b, "B" + background.Color);
            Number(b, background.Radius);
            foreach (double padding in background.Padding)
            {
                Number(b, padding);
            }
        }

        if (HighlightLength > 0)
        {
            Append(b, "H" + s.Highlight!.Color).Append(HighlightStart).Append('+').Append(HighlightLength).Append('|');
        }

        Number(Number(Number(b, BoxWidth), UnitScale), RasterScale).Append(SequenceWidth).Append('x').Append(SequenceHeight);
        return b.ToString();
    }

    private static StringBuilder Append(StringBuilder b, string value) => b.Append(value.Length).Append(':').Append(value).Append('|');

    private static StringBuilder Number(StringBuilder b, double value) => b.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('|');
}

/// <summary>
/// Quien rasteriza las capas generadas (en la app y en la exportación, sobre su propio dispositivo). El motor le avisa de
/// lo que se verá pronto; nunca espera.
/// </summary>
public interface IGeneratedContentCache
{
    /// <summary>
    /// Verdadero si <paramref name="content"/> ya está listo para componer; si no, empieza a prepararlo fuera del hilo y
    /// devuelve falso. No bloquea.
    /// </summary>
    bool Prepare(GeneratedContent content);
}
