namespace Rodaje.App.Views.Player;

/// <summary>
/// Los controles de la pantalla completa: aparecen al mover el ratón y se esconden tras <see cref="Delay"/> sin movimiento
/// (salvo con el puntero encima de ellos).
/// </summary>
public sealed class OverlayAutoHide(TimeSpan delay)
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(2.5);

    private TimeSpan _lastMove = TimeSpan.MinValue;

    public TimeSpan Delay { get; } = delay;

    /// <summary>El puntero está sobre los controles: no se esconden.</summary>
    public bool Hovered { get; set; }

    /// <summary>Se movió el ratón en <paramref name="now"/> (cualquier reloj monótono).</summary>
    public void Moved(TimeSpan now) => _lastMove = now;

    /// <summary>Esconde ya (por ejemplo, al entrar en pantalla completa sin mover el ratón).</summary>
    public void Hide() => _lastMove = TimeSpan.MinValue;

    public bool IsVisible(TimeSpan now) => Hovered || (_lastMove != TimeSpan.MinValue && now - _lastMove < Delay);

    /// <summary>Fracción de la barra de posición (0 a 1) para un instante de la secuencia.</summary>
    public static double Fraction(double positionSeconds, double durationSeconds) =>
        durationSeconds > 0 && double.IsFinite(positionSeconds) ? Math.Clamp(positionSeconds / durationSeconds, 0, 1) : 0;
}
