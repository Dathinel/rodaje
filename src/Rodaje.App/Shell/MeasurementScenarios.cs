using Rodaje.App.Services;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Shell;

/// <summary>Un deslizador del inspector: el parÃ¡metro que la medida mueve al reproducir.</summary>
internal readonly record struct SliderTarget(string ClipId, string EffectId, string Parameter, Time At);

/// <summary>
/// Proyectos de medida que se arman con comandos sobre el medio ya importado (<c>--escenario</c>). Â«efectosÂ»: la pista
/// principal cortada en 5 con 4 transiciones distintas, y dos capas encima, todas con color y otro efecto.
/// </summary>
internal static class MeasurementScenarios
{
    public const string Effects = "efectos";

    public static bool IsKnown(string? name) => name is null or Effects;

    /// <summary>Arma el escenario Â«efectosÂ» sobre el primer clip de la principal; <c>null</c> si no hay de dÃ³nde.</summary>
    public static SliderTarget? BuildEffects(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string seq = session.SequenceId;
        if (session.MainTrack is not { Clips.Count: > 0 } main)
        {
            return null;
        }

        Clip first = main.Clips[0];
        string mediaId = first.MediaId;
        Time sourceIn = first.SourceIn;
        Time sourceOut = first.SourceOut;
        Time length = first.Duration;
        Rational rate = session.Sequence.FrameRate;
        session.BeginGesture("Escenario de medida");
        try
        {
            for (int k = 1; k <= 4; k++)
            {
                Time at = (first.Start + Time.FromTicks(length.Ticks / 5 * k)).SnapToFrame(rate);
                Run(session, Edits.Split(seq, main.Clips[^1].Id, at));
            }

            string[] transitions = [BuiltInTransitions.Crossfade, BuiltInTransitions.Dip, BuiltInTransitions.Wipe, BuiltInTransitions.Push];
            for (int i = 0; i < transitions.Length; i++)
            {
                Run(session, Edits.AddTransition(seq, main.Clips[i].Id, transitions[i]));
            }

            foreach (Clip clip in main.Clips.ToList())
            {
                AddEffect(session, clip.Id, BuiltInEffects.Color, (C.Saturation, 0.2), (C.Contrast, 0.1));
            }

            SliderTarget? slider = null;
            (string Name, double X, string Second, string SecondParameter, double SecondValue)[] layers =
            [
                ("V2", -480, BuiltInEffects.Vignette, BuiltInEffects.VignetteParameters.Amount, 0.6),
                ("V3", 480, BuiltInEffects.Blur, BuiltInEffects.BlurParameters.Radius, 6),
            ];
            for (int i = 0; i < layers.Length; i++)
            {
                (string name, double x, string second, string secondParameter, double secondValue) = layers[i];
                var addTrack = Edits.AddTrack(seq, TrackKind.Video, i + 1, name);
                Run(session, addTrack);
                var place = Edits.Overwrite(seq, mediaId, sourceIn, sourceOut, addTrack.CreatedIds[0], first.Start);
                Run(session, place);
                string clipId = place.CreatedIds[0];
                AddEffect(session, clipId, BuiltInEffects.Transform, (T.Scale, 0.45), (T.X, x));
                string colorId = AddEffect(session, clipId, BuiltInEffects.Color, (C.Exposure, 0.2), (C.Temperature, 0.2));
                AddEffect(session, clipId, second, (secondParameter, secondValue));
                slider ??= new SliderTarget(clipId, colorId, C.Exposure, sourceIn);
            }

            return slider;
        }
        finally
        {
            session.EndGesture();
        }
    }

    private static string AddEffect(EditorSession session, string clipId, string type, params (string Name, double Value)[] values)
    {
        var add = Edits.AddEffect(session.SequenceId, clipId, type);
        Run(session, add);
        string effectId = add.CreatedIds[0];
        Time at = session.Sequence.GetClip(clipId).Clip.SourceIn;
        foreach ((string name, double value) in values)
        {
            Run(session, Edits.SetParameter(session.SequenceId, clipId, effectId, name, value, at));
        }

        return effectId;
    }

    private static void Run(EditorSession session, SequenceEditCommand command)
    {
        if (!session.Execute(command))
        {
            throw new InvalidOperationException($"El escenario de medida no se pudo armar: {command.Description}.");
        }
    }
}
