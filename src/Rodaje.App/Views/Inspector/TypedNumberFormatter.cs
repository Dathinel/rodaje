using Rodaje.App.Services.Inspector;
using Windows.Globalization.NumberFormatting;

namespace Rodaje.App.Views.Inspector;

/// <summary>
/// Formato de los <c>NumberBox</c>: coma decimal como el resto de la app y lectura de «29,97» y «29.97» en cualquier idioma
/// de Windows (<see cref="TypedNumber.Parse"/>).
/// </summary>
internal sealed partial class TypedNumberFormatter(int decimals) : INumberFormatter2, INumberParser
{
    public string FormatDouble(double value) => TypedNumber.Format(value, decimals);

    public string FormatInt(long value) => TypedNumber.Format(value, decimals);

    public string FormatUInt(ulong value) => TypedNumber.Format(value, decimals);

    public double? ParseDouble(string text) => TypedNumber.Parse(text);

    public long? ParseInt(string text) =>
        TypedNumber.Parse(text) is double v && v == Math.Floor(v) && v is >= long.MinValue and < long.MaxValue ? (long)v : null;

    public ulong? ParseUInt(string text) =>
        TypedNumber.Parse(text) is double v && v == Math.Floor(v) && v is >= 0 and < ulong.MaxValue ? (ulong)v : null;
}
