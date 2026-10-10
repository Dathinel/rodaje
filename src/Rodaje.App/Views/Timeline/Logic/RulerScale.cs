using System.Globalization;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>Una marca de la regla: las mayores llevan el tiempo escrito.</summary>
public readonly record struct RulerTick(Time Time, double X, bool IsMajor);

/// <summary>El paso de la regla para un zoom: cada cuánto va una marca mayor y en cuántas partes se divide.</summary>
public readonly record struct RulerStep(Time Major, int Divisions, bool ShowsFrames)
{
    public Time Minor => Time.FromTicks(Major.Ticks / Divisions);
}

/// <summary>Elige el paso de la regla según el zoom y genera las marcas del tramo visible.</summary>
public static class RulerScale
{
    /// <summary>Separación mínima entre dos tiempos escritos en la regla.</summary>
    public const double MinLabelSpacing = 84;

    /// <summary>Separación mínima entre dos marcas menores.</summary>
    public const double MinTickSpacing = 7;

    private static readonly int[] FrameSteps = [1, 2, 5, 10];

    private static readonly (int Seconds, int[] Divisions)[] SecondSteps =
    [
        (1, [10, 5, 2]),
        (2, [4, 2]),
        (5, [5]),
        (10, [10, 5, 2]),
        (15, [3]),
        (30, [6, 3]),
        (60, [6, 4, 2]),
        (120, [4, 2]),
        (300, [5]),
        (600, [10, 5, 2]),
        (900, [3]),
        (1800, [6, 3]),
        (3600, [6, 4, 2]),
        (7200, [4, 2]),
        (18000, [5]),
    ];

    public static RulerStep ChooseStep(double pixelsPerSecond, Rational frameRate)
    {
        double frameSeconds = frameRate.Den / (double)frameRate.Num;
        foreach (int frames in FrameSteps)
        {
            double px = frames * frameSeconds * pixelsPerSecond;
            if (px >= MinLabelSpacing && frames * frameSeconds < 1)
            {
                int divisions = frames > 1 && px / frames >= MinTickSpacing ? frames : 1;
                return new RulerStep(Time.FromFrames(frames, frameRate), divisions, true);
            }
        }

        foreach ((int seconds, int[] divisions) in SecondSteps)
        {
            double px = seconds * pixelsPerSecond;
            if (px >= MinLabelSpacing || seconds == SecondSteps[^1].Seconds)
            {
                int chosen = divisions.FirstOrDefault(d => px / d >= MinTickSpacing, 1);
                return new RulerStep(Time.FromSeconds(seconds), chosen, false);
            }
        }

        throw new InvalidOperationException("Inalcanzable: el último paso siempre se elige.");
    }

    /// <summary>Las marcas entre el borde izquierdo y el derecho del área visible.</summary>
    public static List<RulerTick> Ticks(TimelineViewport viewport, RulerStep step)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        long minor = Math.Max(1, step.Minor.Ticks);
        long first = viewport.VisibleStart.Ticks / minor;
        long last = (viewport.VisibleEnd.Ticks / minor) + 1;
        var ticks = new List<RulerTick>((int)Math.Min(4096, last - first + 1));
        for (long i = first; i <= last && ticks.Count < 4096; i++)
        {
            var time = Time.FromTicks(i * minor);
            ticks.Add(new RulerTick(time, viewport.TimeToX(time), i % step.Divisions == 0));
        }

        return ticks;
    }

    /// <summary>
    /// El texto de una marca mayor: «m:ss» (o «h:mm:ss» pasada la hora) y, cuando el paso baja de un segundo, el cuadro
    /// («m:ss:ff»).
    /// </summary>
    public static string FormatLabel(Time time, RulerStep step, Rational frameRate)
    {
        long fps = Math.Max(1, (long)Math.Round(frameRate.ToDouble()));
        long totalFrames = time.ToFrames(frameRate);
        long seconds = (long)Math.Floor(time.ToSeconds() + 1e-9);
        long frame = step.ShowsFrames ? Math.Clamp(totalFrames - (long)Math.Round(seconds * frameRate.ToDouble()), 0, fps - 1) : 0;
        long h = seconds / 3600;
        long m = seconds / 60 % 60;
        long s = seconds % 60;
        string clock = h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
        return step.ShowsFrames ? string.Create(CultureInfo.InvariantCulture, $"{clock}:{frame:00}") : clock;
    }
}
