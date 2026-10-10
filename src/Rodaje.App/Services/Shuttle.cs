namespace Rodaje.App.Services;

/// <summary>Teclas de lanzadera J, K y L.</summary>
public enum ShuttleKey
{
    /// <summary>J: hacia atrás; cada pulsación dobla la velocidad.</summary>
    Reverse,

    /// <summary>K: detener.</summary>
    Stop,

    /// <summary>L: hacia adelante; cada pulsación dobla la velocidad.</summary>
    Forward,
}

/// <summary>Velocidades de la lanzadera: 0 es pausa, 1 la reproducción normal y el resto, recorridos a saltos.</summary>
public static class Shuttle
{
    public const int MaxRate = 8;

    /// <summary>
    /// La velocidad que sigue a <paramref name="current"/> al pulsar <paramref name="key"/>: L y J empiezan a ×1 en su
    /// sentido (desde la pausa o desde el sentido contrario) y luego doblan hasta ×8; K detiene.
    /// </summary>
    public static int Next(int current, ShuttleKey key) => key switch
    {
        ShuttleKey.Forward => current <= 0 ? 1 : Math.Min(current * 2, MaxRate),
        ShuttleKey.Reverse => current >= 0 ? -1 : Math.Max(current * 2, -MaxRate),
        _ => 0,
    };

    /// <summary>Texto corto para el reproductor: «◀ ×2», «×4»; vacío en pausa o a velocidad normal.</summary>
    public static string Label(int rate) => rate switch
    {
        0 or 1 => "",
        < 0 => $"◀ ×{-rate}",
        _ => $"▶ ×{rate}",
    };
}
