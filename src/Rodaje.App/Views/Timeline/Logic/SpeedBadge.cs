using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// El indicador de velocidad sobre el clip: «2,0×», «◀ 0,5×», «❚❚ Congelado» o «Curva». La mini curva de la fase 5c.2
/// va en el mismo indicador, a la derecha del texto.
/// </summary>
public static class SpeedBadge
{
    public const string Frozen = "❚❚ Congelado";
    public const string Reverse = "◀";
    public const string Curve = "Curva";

    /// <summary>El texto del indicador, o <c>null</c> si el clip va a 1× hacia adelante.</summary>
    public static string? Text(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Speed is not { } speed || speed.IsIdentity)
        {
            return null;
        }

        if (speed.Freeze)
        {
            return Frozen;
        }

        string rate = speed.Curve is not null ? Curve : speed.Rate == new Rational(1, 1) ? "" : Format(speed.Rate.ToDouble());
        return speed.Reverse ? (rate.Length == 0 ? Reverse : $"{Reverse} {rate}") : rate;
    }

    /// <summary>Una velocidad con al menos un decimal y como mucho dos: «2,0×», «0,25×», «1,37×».</summary>
    /// <remarks>La app va con globalización invariable (AOT): la coma se pone a mano, como en el resto de la interfaz.</remarks>
    public static string Format(double rate) => rate.ToString("0.0#", CultureInfo.InvariantCulture).Replace('.', ',') + "×";
}
