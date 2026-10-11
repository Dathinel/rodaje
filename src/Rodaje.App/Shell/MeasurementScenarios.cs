using Rodaje.App.Services;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Shell;

/// <summary>
/// Proyectos de medida que se arman con comandos sobre el medio ya importado (<c>--escenario</c>). «efectos»: la pista
/// principal cortada en 5 con 4 transiciones distintas, y dos capas encima, todas con color y otro efecto. «velocidad»:
/// la principal a 1× y dos capas del mismo medio encima, una a 2× y otra a 0,5×. Cada uno devuelve la edición que la
/// medida repite mientras reproduce para ver cuánto tarda en verse.
/// </summary>
internal static class MeasurementScenarios
{
    public const string Effects = "efectos";
    public const string Speed = "velocidad";

    private static readonly Rational[] SpeedSteps = [new(2, 1), new(3, 1), new(3, 2), new(5, 2), new(4, 1), new(7, 4)];

    public const string Layers = "capas";
    public const string Fast = "rapido";

    public static bool IsKnown(string? name) => name is null or Effects or Speed or Layers or Fast;

    /// <summary>Arma el escenario y devuelve la edición número i que se mide al reproducir; <c>null</c> si no hay de dónde.</summary>
    public static Func<int, SequenceEditCommand>? Build(string scenario, EditorSession session) => scenario switch
    {
        Effects => BuildEffects(session),
        Speed => BuildSpeed(session),
        Layers => BuildLayers(session),
        Fast => BuildFast(session),
        _ => null,
    };

    /// <summary>
    /// «capas»: lo mismo que «velocidad» pero todo a 1× (para comparar la reproducción con varias capas sin velocidad); la
    /// edición mueve una capa en horizontal, que no cambia ningún cuadro.
    /// </summary>
    private static Func<int, SequenceEditCommand>? BuildLayers(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string seq = session.SequenceId;
        if (session.MainTrack is not { Clips.Count: > 0 } main)
        {
            return null;
        }

        Clip first = main.Clips[0];
        session.BeginGesture("Escenario de medida");
        try
        {
            string? moved = null;
            string? transform = null;
            double[] xs = [-480, 480];
            for (int i = 0; i < xs.Length; i++)
            {
                var addTrack = Edits.AddTrack(seq, TrackKind.Video, i + 1, $"V{i + 2}");
                Run(session, addTrack);
                var place = Edits.Overwrite(seq, first.MediaId, first.SourceIn, first.SourceOut, addTrack.CreatedIds[0], first.Start);
                Run(session, place);
                string clipId = place.CreatedIds[0];
                string effect = AddEffect(session, clipId, BuiltInEffects.Transform, (T.Scale, 0.45), (T.X, xs[i]));
                moved ??= clipId;
                transform ??= effect;
            }

            Time at = first.SourceIn;
            return n => Edits.SetParameter(seq, moved!, transform!, T.X, -480 + (40 * (n % 6)), at, "Mover capa");
        }
        finally
        {
            session.EndGesture();
        }
    }

    /// <summary>«rapido»: el primer clip de la principal a 2×; la edición alterna su velocidad entre 2× y 3×.</summary>
    private static Func<int, SequenceEditCommand>? BuildFast(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string seq = session.SequenceId;
        if (session.MainTrack is not { Clips.Count: > 0 } main)
        {
            return null;
        }

        string clipId = main.Clips[0].Id;
        Run(session, Edits.SetSpeed(seq, clipId, new Rational(2, 1)));
        return n => Edits.SetSpeed(seq, clipId, n % 2 == 0 ? new Rational(3, 1) : new Rational(2, 1));
    }

    /// <summary>«efectos» sobre el primer clip de la principal; la edición mueve el deslizador de exposición de una capa.</summary>
    private static Func<int, SequenceEditCommand>? BuildEffects(EditorSession session)
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

            Func<int, SequenceEditCommand>? slider = null;
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
                slider ??= n => Edits.SetParameter(seq, clipId, colorId, C.Exposure, Math.Sin(n * 0.4) * 1.5, sourceIn, "Cambiar exposición");
            }

            return slider;
        }
        finally
        {
            session.EndGesture();
        }
    }

    /// <summary>
    /// «velocidad»: el primer clip de la principal queda a 1× y el mismo tramo se pone en dos capas reducidas, una a 2× y
    /// otra a 0,5×. La edición cambia la velocidad de la capa a 2× entre 1,5× y 4×.
    /// </summary>
    private static Func<int, SequenceEditCommand>? BuildSpeed(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string seq = session.SequenceId;
        if (session.MainTrack is not { Clips.Count: > 0 } main)
        {
            return null;
        }

        Clip first = main.Clips[0];
        session.BeginGesture("Escenario de medida");
        try
        {
            string? fast = null;
            (string Name, double X, Rational Rate)[] layers = [("V2", -480, new(2, 1)), ("V3", 480, new(1, 2))];
            for (int i = 0; i < layers.Length; i++)
            {
                (string name, double x, Rational rate) = layers[i];
                var addTrack = Edits.AddTrack(seq, TrackKind.Video, i + 1, name);
                Run(session, addTrack);
                var place = Edits.Overwrite(seq, first.MediaId, first.SourceIn, first.SourceOut, addTrack.CreatedIds[0], first.Start);
                Run(session, place);
                string clipId = place.CreatedIds[0];
                AddEffect(session, clipId, BuiltInEffects.Transform, (T.Scale, 0.45), (T.X, x));
                Run(session, Edits.SetSpeed(seq, clipId, rate));
                fast ??= clipId;
            }

            return n => Edits.SetSpeed(seq, fast!, SpeedSteps[n % SpeedSteps.Length]);
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
