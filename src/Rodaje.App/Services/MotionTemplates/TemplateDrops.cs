using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.MotionTemplates;

/// <summary>Soltar una plantilla de títulos en una pista: cabe si la pista tiene libre toda su duración.</summary>
public static class TemplateDrops
{
    public static bool FitsIn(Track? track, Time at, double durationSeconds)
    {
        if (track is null)
        {
            return false;
        }

        Time end = at + Time.FromSeconds(durationSeconds);
        return !track.Clips.Exists(c => c.Start < end && c.End > at);
    }
}
