using System.Text.RegularExpressions;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

public static partial class ModelValidator
{
    public const int MaxReelLength = 256;

    /// <summary>
    /// <c>HH:MM:SS:FF</c> o con <c>;</c> (también <c>.</c> o <c>,</c>). Si <paramref name="rate"/> es una tasa de TC SMPTE,
    /// además tiene que ser un TC válido a esa tasa; una tasa que el TC no admite, o un medio sin tasa, solo se comprueba
    /// por la forma, porque así lo trajo el archivo.
    /// </summary>
    internal static bool IsTimecodeText(string text, Rational? rate) =>
        text.Length == 11
        && TimecodeShape().IsMatch(text)
        && (rate is not { } r || !SmpteTimecode.TryParse("00:00:00:00", r, out _) || SmpteTimecode.TryParse(text, r, out _));

    private static string Shorten(string text) => text.Length <= 32 ? text : text[..32] + "…";

    [GeneratedRegex(@"^\d\d:\d\d:\d\d[:;.,]\d\d$", RegexOptions.CultureInvariant)]
    private static partial Regex TimecodeShape();
}
