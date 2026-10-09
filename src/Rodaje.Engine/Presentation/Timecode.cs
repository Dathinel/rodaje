using System.Globalization;
using Rodaje.Core.Timing;

namespace Rodaje.Engine.Presentation;

public static class Timecode
{
    /// <summary><c>m:ss.mmm</c>, o <c>h:mm:ss.mmm</c> desde una hora; trunca a milisegundos.</summary>
    public static string Format(Time time)
    {
        bool negative = time < Time.Zero;
        long ms = (long)(Int128.Abs((Int128)time.Ticks) * 1000 / Time.TicksPerSecond);
        long hours = ms / 3_600_000;
        long minutes = ms / 60_000 % 60;
        long seconds = ms / 1000 % 60;
        long millis = ms % 1000;
        string sign = negative ? "-" : "";
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{hours}:{minutes:00}:{seconds:00}.{millis:000}")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{minutes}:{seconds:00}.{millis:000}");
    }
}
