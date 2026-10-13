using Rodaje.Core.Effects;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Settings;

/// <summary>La transición por defecto (Ctrl+T y «Usar como transición por defecto»).</summary>
public sealed partial class AppSettings
{
    public TransitionPreferences Transitions { get; set; } = new();
}

/// <summary>
/// Lo que pone Ctrl+T en un corte: el tipo del motor o la referencia de un paquete, la duración, la alineación y la curva.
/// En una pista de audio va <see cref="AudioReference"/>.
/// </summary>
public sealed class TransitionPreferences
{
    public const double MinSeconds = 0.05;

    public const double MaxSeconds = 10;

    public string Reference { get; set; } = BuiltInTransitions.Crossfade;

    public string AudioReference { get; set; } = BuiltInTransitions.AudioPower;

    public double Seconds { get; set; } = EffectRegistry.DefaultTransitionDuration.ToSeconds();

    public TransitionAlignment Alignment { get; set; }

    public Interpolation? Curve { get; set; }

    public BezierEase? Ease { get; set; }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Reference) || Reference == BuiltInTransitions.Preset || BuiltInTransitions.IsAudio(Reference)
            || (EffectRegistry.Find(Reference) is { } video && !video.IsTransition))
        {
            Reference = BuiltInTransitions.Crossfade;
        }

        if (!BuiltInTransitions.IsAudio(AudioReference))
        {
            AudioReference = BuiltInTransitions.AudioPower;
        }

        Seconds = double.IsFinite(Seconds) ? Math.Clamp(Seconds, MinSeconds, MaxSeconds) : EffectRegistry.DefaultTransitionDuration.ToSeconds();
        if (!Enum.IsDefined(Alignment))
        {
            Alignment = TransitionAlignment.Center;
        }

        if (Curve is { } curve && !Enum.IsDefined(curve))
        {
            Curve = null;
        }

        Ease = Curve == Interpolation.Bezier ? (Ease ?? BezierEase.Smooth).Clamped() : null;
    }
}
