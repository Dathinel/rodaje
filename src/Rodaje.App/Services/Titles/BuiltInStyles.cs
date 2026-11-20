using Rodaje.Core.Model;
using Rodaje.Core.Text;

namespace Rodaje.App.Services.Titles;

/// <summary>Un estilo de texto de fábrica: su referencia estable y cómo se ve en la biblioteca.</summary>
public sealed record BuiltInStyle(string Reference, string Name, string Description, Func<TextStyle> Style);

/// <summary>
/// Los estilos de texto que trae el programa en la categoría «Texto» de la biblioteca: el «Texto» de siempre y cuatro
/// de subtítulo animados «palabra por palabra», armados con la entrada por palabra de <see cref="TextAnimationPresets"/>
/// y el resaltado de karaoke de <see cref="StyleHighlight"/> sobre la letra legible de <see cref="SubtitleStyles"/>.
/// Un resaltado solo se ve en un subtítulo con palabras transcritas; sin ellas, el estilo es un subtítulo normal.
/// <see cref="Find"/> devuelve una copia nueva en cada llamada: editarla no toca el de fábrica.
/// </summary>
public static class BuiltInStyles
{
    /// <summary>La palabra que suena se pinta de amarillo mientras suena (o el barrido la va llenando).</summary>
    public const string HighlightColor = "#FFD400";

    public const string WordsReference = "rodaje.subtitle.words";
    public const string KaraokeReference = "rodaje.subtitle.karaoke";
    public const string PopReference = "rodaje.subtitle.pop";
    public const string LinesReference = "rodaje.subtitle.lines";

    /// <summary>Los cinco, en el orden en que salen en la biblioteca.</summary>
    public static IReadOnlyList<BuiltInStyle> All { get; } =
    [
        new(TitleStyles.BuiltInReference, "Texto", "Un título en el cursor: blanco, en negrita y con sombra.", TitleStyles.Default),
        new(WordsReference, "Palabra a palabra", "Subtítulo que entra palabra a palabra y pinta la que suena.", Words),
        new(KaraokeReference, "Karaoke", "Subtítulo con un barrido de color que sigue la voz.", Karaoke),
        new(PopReference, "Pop", "Subtítulo con borde grueso; cada palabra entra como un resorte.", Pop),
        new(LinesReference, "Líneas", "Subtítulo que entra línea a línea y se desvanece.", Lines),
    ];

    /// <summary>Una copia del estilo de fábrica de <paramref name="reference"/>; <c>null</c> si no es de fábrica.</summary>
    public static TextStyle? Find(string? reference) => All.FirstOrDefault(s => s.Reference == reference)?.Style();

    /// <summary>El estilo de subtítulos con el nombre del de fábrica (sin origen: es de fábrica, como «Texto»).</summary>
    private static TextStyle Subtitle(string name)
    {
        TextStyle style = SubtitleStyles.Default();
        style.Name = name;
        return style;
    }

    /// <summary>«Palabra a palabra»: cada palabra sube al entrar y la que suena se pinta de amarillo.</summary>
    private static TextStyle Words()
    {
        TextStyle style = Subtitle("Palabra a palabra");
        style.In = TextAnimationPresets.Find("subir")!.In(TextUnit.Word, 0.5, 0.5);
        style.Highlight = new StyleHighlight { Color = HighlightColor, Mode = HighlightMode.Word };
        return style;
    }

    /// <summary>«Karaoke»: sin entrada; el color recorre cada palabra al ritmo de la voz.</summary>
    private static TextStyle Karaoke()
    {
        TextStyle style = Subtitle("Karaoke");
        style.Highlight = new StyleHighlight { Color = HighlightColor, Mode = HighlightMode.Sweep };
        return style;
    }

    /// <summary>«Pop»: borde negro grueso en vez de caja y cada palabra entra como un resorte.</summary>
    private static TextStyle Pop()
    {
        TextStyle style = Subtitle("Pop");
        style.Background = null;
        style.Stroke = new StyleStroke { Color = "#000000", Width = 6 };
        style.In = TextAnimationPresets.Find("elastico")!.In(TextUnit.Word, seconds: 0.6);
        style.Highlight = new StyleHighlight { Color = HighlightColor, Mode = HighlightMode.Word };
        return style;
    }

    /// <summary>«Líneas»: cada línea sube al entrar y el bloque se desvanece al salir, sin resaltado.</summary>
    private static TextStyle Lines()
    {
        TextStyle style = Subtitle("Líneas");
        style.In = TextAnimationPresets.Find("lineas")!.In(TextUnit.Line);
        style.Out = TextAnimationPresets.Find("desvanecer")!.Out(TextUnit.Block);
        return style;
    }
}
