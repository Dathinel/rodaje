using Rodaje.Core.Effects;

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

        if (style.Stroke is { } stroke)
        {
            Check(IsColor(stroke.Color) && Enum.IsDefined(stroke.Join), "un borde con color o unión no válidos");
            Check(InRange(stroke.Width, 0, style.Size / 2), "un borde más ancho que la mitad del tamaño");
        }

        if (style.Shadow is { } shadow)
        {
            Check(IsColor(shadow.Color) && InRange(shadow.Dx, -MaxOffset, MaxOffset) && InRange(shadow.Dy, -MaxOffset, MaxOffset)
                && InRange(shadow.Blur, 0, 64), "una sombra fuera de rango");
        }

        if (style.Background is { } background)
        {
            Check(IsColor(background.Color) && background.Padding.Count == 2 && background.Padding.TrueForAll(p => InRange(p, 0, MaxOffset))
                && InRange(background.Radius, 0, MaxOffset), "un fondo fuera de rango");
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
        ParameterDefinition[] limits = [.. AnimatedParameters.Select(p => EffectRegistry.Find(BuiltInEffects.Transform)!.FindParameter(p)!)];
        foreach ((string parameter, List<StyleKey> keys) in animation.Curves)
        {
            int index = Array.IndexOf(AnimatedParameters, parameter);
            if (index < 0)
            {
                check(false, $"una {name} que anima «{parameter}», que no es x, y, scale, rotation ni opacity");
                continue;
            }

            bool ordered = keys.Count is > 0 and <= MaxStyleKeys;
            for (int i = 0; ordered && i < keys.Count; i++)
            {
                StyleKey key = keys[i];
                ordered = InRange(key.T, 0, duration) && (i == 0 || key.T > keys[i - 1].T)
                    && InRange(key.Value, limits[index].Minimum, limits[index].Maximum) && Enum.IsDefined(key.Interp);
            }

            check(ordered, $"una {name} con la curva «{parameter}» vacía, de más de {MaxStyleKeys} puntos, fuera de orden o de rango");
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
