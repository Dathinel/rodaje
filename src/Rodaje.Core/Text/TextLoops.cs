using Rodaje.Core.Model;

namespace Rodaje.Core.Text;

/// <summary>Bucles sin estado ni azar: un delta del bloque entero, sin reservar memoria.</summary>
public static class TextLoops
{
    public static UnitPose Delta(StyleLoop loop, double seconds)
    {
        ArgumentNullException.ThrowIfNull(loop);
        double speed = loop.Speed;
        if (!double.IsFinite(seconds) || !double.IsFinite(loop.Amount) || !double.IsFinite(speed)
            || speed < StyleLoop.MinSpeed || speed > StyleLoop.MaxSpeed)
        {
            return UnitPose.Rest;
        }

        double amount = Math.Clamp(loop.Amount, StyleLoop.MinAmount, StyleLoop.MaxAmount);
        // Reducir antes de multiplicar evita desbordamientos incluso con tiempos finitos enormes.
        double wave = Wave(seconds, speed);
        return loop.Kind switch
        {
            TextLoopKind.Wobble => new(12 * amount * wave, 0, 1, 5 * amount * wave, 1, 0),
            TextLoopKind.Shake => new(1.5 * amount * (wave + Wave(seconds, speed * Math.Sqrt(2))),
                1.5 * amount * (wave + Wave(seconds, speed * Math.Sqrt(3))), 1, 0, 1, 0),
            TextLoopKind.Pulse => new(0, 0, 1 + 0.06 * amount * wave, 0, 1, 0),
            TextLoopKind.Float => new(0, 10 * amount * wave, 1, 0, 1, 0),
            _ => UnitPose.Rest,
        };
    }

    private static double Wave(double seconds, double speed) => Math.Sin(Math.Tau * (seconds % (1 / speed)) * speed);
}
