using Rodaje.Core.Model;
using Rodaje.Packages.Content;

namespace Rodaje.App.Services.Titles;

/// <summary>
/// El estilo de un título nuevo y la copia de un <c>rodaje.textstyle</c> de paquete al modelo (D-029). Paquetes no
/// conoce el núcleo: el paso de uno a otro vive aquí.
/// </summary>
public static class TitleStyles
{
    /// <summary>Referencia del estilo que trae el programa (el elemento «Texto» de la biblioteca).</summary>
    public const string BuiltInReference = "rodaje.text";

    /// <summary>Texto con que nace un título.</summary>
    public const string DefaultText = "Texto";

    /// <summary>Blanco, en negrita, con una sombra suave: se lee sobre casi cualquier imagen.</summary>
    public static TextStyle Default() => new()
    {
        Name = "Texto",
        Font = new StyleFont { Family = "Segoe UI", Weight = 700, Fallback = ["Arial"] },
        Size = 96,
        Fill = new StyleFill { Color = "#FFFFFF" },
        Shadow = new StyleShadow { Color = "#00000099", Dx = 0, Dy = 4, Blur = 8 },
    };

    /// <summary>Un título nuevo con el texto y el estilo dados (o los de fábrica).</summary>
    public static ClipGenerator NewTitle(TextStyle? style = null, string text = DefaultText) => new()
    {
        Kind = GeneratorKinds.Text,
        Text = text,
        Style = style?.Clone() ?? Default(),
    };

    /// <summary>La copia del estilo de paquete para el clip, con <see cref="TextStyle.Source"/> = <paramref name="reference"/>.</summary>
    public static TextStyle FromPreset(TextStylePreset preset, string? reference)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new TextStyle
        {
            Source = reference,
            Name = preset.Name,
            Font = new StyleFont
            {
                Family = preset.Font.Family,
                Weight = preset.Font.Weight,
                Italic = preset.Font.Italic,
                Source = preset.Font.Source,
                Fallback = [.. preset.Font.Fallback],
            },
            Size = preset.Size,
            Reference = new StyleReference { Width = preset.ReferenceWidth, Height = preset.ReferenceHeight },
            LineHeight = preset.LineHeight,
            LetterSpacing = preset.LetterSpacing,
            Align = preset.Align switch
            {
                TextStyleAlign.Left => TextAlign.Left,
                TextStyleAlign.Right => TextAlign.Right,
                _ => TextAlign.Center,
            },
            Fill = new StyleFill { Color = preset.FillColor },
            Stroke = preset.Stroke is { } s
                ? new StyleStroke
                {
                    Color = s.Color,
                    Width = s.Width,
                    Join = s.Join switch
                    {
                        TextStyleJoin.Miter => StrokeJoin.Miter,
                        TextStyleJoin.Bevel => StrokeJoin.Bevel,
                        _ => StrokeJoin.Round,
                    },
                }
                : null,
            Shadow = preset.Shadow is { } sh ? new StyleShadow { Color = sh.Color, Dx = sh.Dx, Dy = sh.Dy, Blur = sh.Blur } : null,
            Background = preset.Background is { } b
                ? new StyleBackground { Color = b.Color, Padding = [b.PaddingX, b.PaddingY], Radius = b.Radius }
                : null,
            Highlight = preset.HighlightColor is { } highlight ? new StyleHighlight { Color = highlight } : null,
            In = Animation(preset.In),
            Out = Animation(preset.Out),
        };
    }

    private static StyleAnimation? Animation(TextStyleAnimation? animation)
    {
        if (animation is null)
        {
            return null;
        }

        var result = new StyleAnimation { DurationSeconds = animation.DurationSeconds };
        foreach (PresetCurve curve in animation.Curves)
        {
            result.Curves[curve.Parameter] = [.. curve.Keys.Select(k => new StyleKey(k.T, k.Value, Interp(k.Interpolation)))];
        }

        return result;
    }

    private static Interpolation Interp(PresetInterpolation interpolation) => interpolation switch
    {
        PresetInterpolation.Hold => Interpolation.Hold,
        PresetInterpolation.EaseIn => Interpolation.EaseIn,
        PresetInterpolation.EaseOut => Interpolation.EaseOut,
        PresetInterpolation.EaseInOut => Interpolation.EaseInOut,
        _ => Interpolation.Linear,
    };
}
