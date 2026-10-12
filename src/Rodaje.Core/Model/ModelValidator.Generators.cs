using Rodaje.Core.Effects;
using Rodaje.Core.Text;

namespace Rodaje.Core.Model;

/// <summary>Reglas de los clips generadores y de los roles de pista (formato 5).</summary>
public static partial class ModelValidator
{
    public const int MaxTextLength = 10_000;
    public const int MaxTextLines = 200;
    public const int MaxProjectTextLength = 2_000_000;
    public const int MaxStyleKeys = 64;
    public const int MaxFallbackFonts = 8;
    private const int MaxNameLength = 128;
    private const int MaxReferenceSide = 16_384;
    private const double MaxOffset = 1000;
    private const double MaxAnimationSeconds = 60;
    private const double MaxEdgeOffsetSeconds = 360_000;
    public const int MaxExtraStrokes = 4;
    public const int MaxExtraShadows = 4;

    private static readonly string[] AnimatedParameters =
    [
        BuiltInEffects.TransformParameters.X,
        BuiltInEffects.TransformParameters.Y,
        BuiltInEffects.TransformParameters.Scale,
        BuiltInEffects.TransformParameters.Rotation,
        BuiltInEffects.TransformParameters.Opacity,
    ];

    /// <summary>
    /// Un generador va solo en pistas de video, sin velocidad ni medio, con un texto que admite <c>\n</c>, tabulación y
    /// marcas bidi (que no son de control) y, si es de texto, con un estilo completo dentro de sus topes.
    /// </summary>
    private static void ValidateGenerator(Clip clip, ClipGenerator generator, Track track, string where, List<string> errors)
    {
        if (track.Kind != TrackKind.Video)
        {
            errors.Add($"{where} es un generador y está en una pista de audio.");
        }

        if (clip.Speed is not null)
        {
            errors.Add($"{where} es un generador y tiene velocidad, que no admite.");
        }

        if (!IsToken(generator.Kind, 64, allowDot: true))
        {
            errors.Add($"{where} tiene un tipo de generador no válido.");
        }

        string text = generator.Text;
        if (text.Length > MaxTextLength)
        {
            errors.Add($"{where} tiene un texto de más de {MaxTextLength} caracteres.");
        }

        // DirectWrite también parte la línea en los separadores de Unicode, que no son de control.
        if (text.Count(c => c is '\n' or '\u2028' or '\u2029') >= MaxTextLines)
        {
            errors.Add($"{where} tiene un texto de más de {MaxTextLines} líneas.");
        }

        if (text.Any(c => char.IsControl(c) && c is not '\n' and not '\t'))
        {
            errors.Add($"{where} tiene un texto con caracteres de control (solo admite saltos de línea y tabulaciones).");
        }

        if (!InRange(generator.Edges.Before.ToSeconds(), 0, MaxEdgeOffsetSeconds) || !InRange(generator.Edges.After.ToSeconds(), 0, MaxEdgeOffsetSeconds))
        {
            errors.Add($"{where} tiene en sus bordes un desfase negativo o de más de {MaxEdgeOffsetSeconds} s.");
        }

        if (generator.Kind == GeneratorKinds.Text && generator.Style is null)
        {
            errors.Add($"{where} es un texto sin estilo.");
        }

        if (generator.Style is { } style)
        {
            ValidateStyle(style, where, errors);
        }

        if (generator.Box is { } box
            && ((box.Width is { } width && !InRange(width, double.Epsilon, MaxReferenceSide))
                || (box.WidthFraction is { } fraction && !InRange(fraction, double.Epsilon, 1))))
        {
            errors.Add($"{where} tiene una caja de texto con un ancho fuera de rango.");
        }

        if (generator.Box?.HeightFraction is { } heightFraction && !InRange(heightFraction, double.Epsilon, 1))
        {
            errors.Add($"{where} tiene una caja de texto con un alto fuera de rango.");
        }

        if (generator.Spans is { } spans)
        {
            ValidateSpans(spans, text.Length, where, errors);
        }

        ValidateWords(generator, where, errors);
    }

    private static void ValidateStyle(TextStyle style, string where, List<string> errors)
    {
        void Check(bool ok, string what)
        {
            if (!ok)
            {
                errors.Add($"{where} tiene en su estilo {what}.");
            }
        }

        Check(style.Source is null || OptionDefinition.IsWellFormedReference(style.Source), "un origen que no es una referencia de paquete");
        Check(style.Name is null || IsDisplayText(style.Name, MaxNameLength), "un nombre no válido");

        StyleFont font = style.Font;
        Check(IsDisplayText(font.Family, MaxNameLength) && font.Family.Trim().Length > 0, "una familia de fuente no válida");
        Check(font.Weight is >= 100 and <= 900, "un peso de fuente fuera de 100 a 900");
        Check(font.Source is null || OptionDefinition.IsWellFormedReference(font.Source), "una fuente con un origen que no es una referencia de paquete");
        Check(font.Fallback.Count <= MaxFallbackFonts && font.Fallback.TrueForAll(f => IsDisplayText(f, MaxNameLength) && f.Trim().Length > 0),
            $"fuentes de respaldo no válidas o más de {MaxFallbackFonts}");

        Check(InRange(style.Size, double.Epsilon, 1000), "un tamaño fuera de rango (mayor que 0 y hasta 1000)");
        Check(style.Reference is { Width: > 0 and <= MaxReferenceSide, Height: > 0 and <= MaxReferenceSide }, "un cuadro de referencia fuera de rango");
        Check(InRange(style.LineHeight, double.Epsilon, 10), "un interlineado fuera de rango");
        Check(InRange(style.LetterSpacing, -MaxOffset, MaxOffset), "un espaciado fuera de rango");
        Check(Enum.IsDefined(style.Align), "una alineación desconocida");
        Check(IsColor(style.Fill.Color), "un color de relleno no válido");
        if (style.Fill.Gradient is { } gradient)
        {
            bool stops = gradient.Stops.Count is >= 2 and <= StyleGradient.MaxStops;
            for (int i = 0; stops && i < gradient.Stops.Count; i++)
            {
                ColorStop stop = gradient.Stops[i];
                stops = stop is not null && InRange(stop.Position, 0, 1) && IsColor(stop.Color) && (i == 0 || stop.Position >= gradient.Stops[i - 1].Position);
            }

            Check(stops && Enum.IsDefined(gradient.Kind) && InRange(gradient.Angle, -360, 360),
                $"un degradado con menos de 2 o más de {StyleGradient.MaxStops} paradas, fuera de orden o de rango");
        }

        Check(style.ExtraStrokes is not { Count: > MaxExtraStrokes } && style.ExtraShadows is not { Count: > MaxExtraShadows },
            $"más de {MaxExtraStrokes} bordes o sombras adicionales");
        foreach (StyleStroke stroke in style.AllStrokes())
        {
            Check(stroke is not null && IsColor(stroke.Color) && Enum.IsDefined(stroke.Join), "un borde con color o unión no válidos");
            Check(stroke is not null && InRange(stroke.Width, 0, style.Size / 2), "un borde más ancho que la mitad del tamaño");
        }

        foreach (StyleShadow shadow in style.AllShadows())
        {
            Check(shadow is not null && IsColor(shadow.Color) && InRange(shadow.Dx, -MaxOffset, MaxOffset) && InRange(shadow.Dy, -MaxOffset, MaxOffset)
                && InRange(shadow.Blur, 0, 64), "una sombra fuera de rango");
        }

        if (style.Typography is { } typography)
        {
            Check(Enum.IsDefined(typography.Case) && Enum.IsDefined(typography.Fit), "unas mayúsculas o un ajuste desconocidos");
            Check(typography.Features.Count <= StyleTypography.MaxFeatures && typography.Features.TrueForAll(StyleTypography.IsFeature),
                $"funciones OpenType no válidas (etiquetas de 4 letras, hasta {StyleTypography.MaxFeatures})");
        }

        if (style.Path is { } path)
        {
            Check(Enum.IsDefined(path.Kind) && InRange(path.Bend, -360, 360), "una trayectoria fuera de rango (de −360 a 360 grados)");
        }

        if (style.Background is { } background)
        {
            Check(IsColor(background.Color) && background.Padding.Count == 2 && background.Padding.TrueForAll(p => InRange(p, 0, MaxOffset))
                && InRange(background.Radius, 0, MaxOffset), "un fondo fuera de rango");
        }

        if (style.Highlight is { } highlight)
        {
            Check(IsColor(highlight.Color) && Enum.IsDefined(highlight.Mode), "un color o un modo de palabra resaltada no válidos");
        }

        ValidateAnimation(style.In, "entrada", Check);
        ValidateAnimation(style.Out, "salida", Check);
    }

    private static void ValidateAnimation(StyleAnimation? animation, string name, Action<bool, string> check)
    {
        if (animation is null)
        {
            return;
        }

        double duration = animation.DurationSeconds;
        check(InRange(duration, 0, MaxAnimationSeconds), $"una {name} con una duración fuera de 0 a {MaxAnimationSeconds} s");
        check(Enum.IsDefined(animation.Unit) && Enum.IsDefined(animation.Order) && InRange(animation.Stagger, 0, 1),
            $"una {name} con una unidad, un orden o un desfase (de 0 a 1) no válidos");
        ParameterDefinition[] limits = [.. AnimatedParameters.Select(p => EffectRegistry.Find(BuiltInEffects.Transform)!.FindParameter(p)!)];
        foreach ((string parameter, List<StyleKey> keys) in animation.Curves)
        {
            int index = Array.IndexOf(AnimatedParameters, parameter);
            bool blur = parameter == StyleAnimation.Blur;
            if (index < 0 && !blur)
            {
                check(false, $"una {name} que anima «{parameter}», que no es x, y, scale, rotation, opacity ni blur");
                continue;
            }

            double min = blur ? 0 : limits[index].Minimum;
            double max = blur ? TextAnimator.MaxBlur : limits[index].Maximum;
            bool ordered = keys.Count is > 0 and <= MaxStyleKeys;
            for (int i = 0; ordered && i < keys.Count; i++)
            {
                StyleKey key = keys[i];
                ordered = key is not null && InRange(key.T, 0, duration) && (i == 0 || key.T > keys[i - 1].T)
                    && InRange(key.Value, min, max) && Enum.IsDefined(key.Interp);
            }

            check(ordered, $"una {name} con la curva «{parameter}» vacía, de más de {MaxStyleKeys} puntos, fuera de orden o de rango");
        }
    }

    /// <summary>Tramos con formato: dentro del texto, en orden, sin solaparse, con algún formato y dentro de sus topes.</summary>
    private static void ValidateSpans(List<TextSpan> spans, int textLength, string where, List<string> errors)
    {
        bool ok = spans.Count <= TextSpans.MaxSpans;
        for (int i = 0; ok && i < spans.Count; i++)
        {
            TextSpan span = spans[i];
            ok = span is not null && span.Start >= 0 && span.Length > 0 && span.End <= textLength && !span.IsPlain
                && (i == 0 || span.Start >= spans[i - 1].End)
                && span.Weight is null or (>= 100 and <= 900)
                && (span.Color is null || IsColor(span.Color))
                && (span.Scale is null || InRange(span.Scale.Value, TextSpans.MinScale, TextSpans.MaxScale));
        }

        if (!ok)
        {
            errors.Add($"{where} tiene tramos de formato fuera del texto, solapados, vacíos, de más de {TextSpans.MaxSpans} o con valores fuera de rango.");
        }
    }

    /// <summary>Rol de pista: minúsculas ASCII, dígitos y guiones; <c>subtitles</c> solo en pistas de video.</summary>

    private static void ValidateRole(Track track, List<string> errors)
    {
        if (track.Role is not { } role)
        {
            return;
        }

        if (!IsToken(role, 32, allowDot: false))
        {
            errors.Add($"La pista «{track.Name}» tiene un rol no válido.");
        }
        else if (role == TrackRoles.Subtitles && track.Kind != TrackKind.Video)
        {
            errors.Add($"La pista «{track.Name}» tiene el rol de subtítulos y no es de video.");
        }
    }

    private static bool IsToken(string? value, int maxLength, bool allowDot) =>
        !string.IsNullOrEmpty(value) && value.Length <= maxLength
        && value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' || (allowDot && c == '.'));

    private static bool IsDisplayText(string? value, int maxLength) =>
        value is not null && value.Length <= maxLength && !value.Any(char.IsControl);

    private static bool IsColor(string? value) =>
        value is { Length: 7 or 9 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit);

    private static bool InRange(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}
