using System.Globalization;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.App.Views.Timeline;

/// <summary>Cuál de las dos asas de fundido de un clip de audio.</summary>
public enum FadeHandle
{
    In,
    Out,
}

/// <summary>
/// Las asas de fundido en los extremos de un clip de audio: dos cuadritos en el borde de arriba que se arrastran hacia
/// adentro para fijar <c>fadeIn</c> y <c>fadeOut</c> (en segundos, hasta la mitad del clip), como en los editores de
/// escritorio. La curva (<c>fadeShape</c>: potencia constante, lineal o exponencial) se elige en Propiedades y se
/// dibuja sobre el clip con las mismas fórmulas que usa el motor.
/// </summary>
public static class FadeHandles
{
    /// <summary>Lado del cuadrito que se dibuja.</summary>
    public const double Size = 8;

    /// <summary>Distancia en píxeles a la que se agarra un asa.</summary>
    public const double Grab = 10;

    /// <summary>Margen del bloque del clip dentro de la fila (el mismo con que se dibuja).</summary>
    private const double ClipInset = 2;

    /// <summary>Centro del asa, medido desde el borde de arriba del bloque del clip.</summary>
    public const double TopOffset = 5;

    /// <summary>Un clip más angosto que esto no muestra asas: no habría dónde agarrarlas sin tapar el cuerpo.</summary>
    public const double MinClipWidth = 3 * Size;

    /// <summary>Si el clip tiene asas: de una pista de audio sin bloquear y con medio (un compuesto o un generador no funden así).</summary>
    public static bool HasHandles(Clip clip, Track track)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(track);
        return track.Kind == TrackKind.Audio && !track.Locked && clip.Generator is null && !clip.IsCompound;
    }

    /// <summary>Los fundidos del clip en segundos (0 sin efecto de volumen), acotados a su duración.</summary>
    public static (double In, double Out) Seconds(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        EffectInstance? audio = ClipDecorations.VolumeOf(clip);
        double limit = clip.Duration.ToSeconds();
        return (Read(audio, A.FadeIn, limit), Read(audio, A.FadeOut, limit));
    }

    /// <summary>La curva de los fundidos del clip; sin opción, potencia constante.</summary>
    public static string Shape(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        string? shape = ClipDecorations.VolumeOf(clip)?.Options.GetValueOrDefault(BuiltInEffects.AudioOptions.FadeShape);
        return shape is not null && EffectRegistry.FadeShapes.Contains(shape) ? shape : BuiltInEffects.FadeShapes.Power;
    }

    /// <summary>Hasta dónde llega un fundido arrastrado: la mitad del clip, como el deslizador de Propiedades.</summary>
    public static double MaxSeconds(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return Math.Max(0, clip.Duration.ToSeconds() / 2);
    }

    /// <summary>
    /// La ganancia (de 0 a 1) a lo largo del avance <paramref name="progress"/> (de 0 a 1) de un fundido de entrada con
    /// esa curva; la de salida es la misma leída al revés. Las mismas fórmulas que <c>Fade.Factor</c> en el motor.
    /// </summary>
    public static double Factor(string shape, double progress)
    {
        double e = Math.Clamp(progress, 0, 1);
        return shape switch
        {
            BuiltInEffects.FadeShapes.Linear => e,
            BuiltInEffects.FadeShapes.Exponential => e <= 0 ? 0 : e >= 1 ? 1 : (Math.Pow(1000, e) - 1) / 999,
            _ => Math.Sin(e * Math.PI / 2),
        };
    }

    /// <summary>La x (en el área visible) del asa: la de entrada va desde el inicio del clip y la de salida, desde su final.</summary>
    public static double HandleX(Clip clip, TimelineViewport viewport, FadeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(viewport);
        (double fadeIn, double fadeOut) = Seconds(clip);
        return handle == FadeHandle.In
            ? viewport.TimeToX(clip.Start) + (fadeIn * viewport.PixelsPerSecond)
            : viewport.TimeToX(clip.End) - (fadeOut * viewport.PixelsPerSecond);
    }

    /// <summary>La y del contenido del centro de las asas sobre la fila del clip.</summary>
    public static double HandleY(TrackRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Top + ClipInset + TopOffset;
    }

    /// <summary>El asa bajo el puntero, si hay una cerca; con las dos encima (clip muy corto), la más cercana.</summary>
    public static FadeHandle? Hit(Clip clip, TrackRow row, TimelineViewport viewport, double x, double contentY)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(viewport);
        if (!HasHandles(clip, row.Track) || viewport.TimeToX(clip.End) - viewport.TimeToX(clip.Start) < MinClipWidth
            || Math.Abs(HandleY(row) - contentY) > Grab)
        {
            return null;
        }

        double dIn = Math.Abs(HandleX(clip, viewport, FadeHandle.In) - x);
        double dOut = Math.Abs(HandleX(clip, viewport, FadeHandle.Out) - x);
        if (dIn > Grab && dOut > Grab)
        {
            return null;
        }

        return dIn <= dOut ? FadeHandle.In : FadeHandle.Out;
    }

    /// <summary>Los segundos de fundido que corresponden a una x arrastrada, acotados y redondeados a centésimas.</summary>
    public static double SecondsAt(Clip clip, TimelineViewport viewport, FadeHandle handle, double x)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(viewport);
        double pixels = handle == FadeHandle.In ? x - viewport.TimeToX(clip.Start) : viewport.TimeToX(clip.End) - x;
        double seconds = Math.Round(pixels / viewport.PixelsPerSecond, 2);
        return Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, MaxSeconds(clip));
    }

    /// <summary>El parámetro de un asa.</summary>
    public static string ParameterOf(FadeHandle handle) => handle == FadeHandle.In ? A.FadeIn : A.FadeOut;

    /// <summary>Texto del historial de un asa.</summary>
    public static string Description(FadeHandle handle) => handle == FadeHandle.In ? "Fundido de entrada" : "Fundido de salida";

    /// <summary>
    /// Fija el fundido de un clip (crea el efecto de volumen si falta). Los pasos seguidos del mismo asa y el mismo clip
    /// se funden: un arrastre deja un paso del historial.
    /// </summary>
    public static SequenceEditCommand Set(string sequenceId, string clipId, FadeHandle handle, double seconds, IIdGenerator? ids = null) =>
        Edits.SetParameterOnClips(sequenceId, [(clipId, Time.Zero)], BuiltInEffects.Audio, ParameterOf(handle), seconds, Description(handle), ids);

    /// <summary>
    /// Fundidos hasta el cursor en varios clips a la vez, cada uno con sus segundos, en un paso: de entrada (del inicio
    /// del clip al cursor) o de salida (del cursor al final).
    /// </summary>
    public static SequenceEditCommand SetMany(string sequenceId, IReadOnlyList<(string ClipId, double Seconds)> targets, FadeHandle handle, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        (string ClipId, double Seconds)[] points = [.. targets];
        string name = ParameterOf(handle);
        return new SequenceEditCommand(
            handle == FadeHandle.In ? "Fundido de entrada hasta el cursor" : "Fundido de salida desde el cursor",
            sequenceId,
            ctx =>
            {
                foreach ((string clipId, double seconds) in points)
                {
                    Clip clip = ctx.Sequence.GetClip(clipId).Clip;
                    string effectId = EffectEdits.EnsureEffect(ctx, clipId, BuiltInEffects.Audio)
                        ?? (clip.Effects.Find(e => e.Type == BuiltInEffects.Audio && e.Enabled) ?? clip.Effects.Find(e => e.Type == BuiltInEffects.Audio))!.Id;
                    EffectEdits.SetParameter(ctx, clipId, effectId, name, seconds, Time.Zero);
                }
            },
            ids);
    }

    /// <summary>«Entrada 0,5 s» para el globo que acompaña el arrastre.</summary>
    public static string Balloon(FadeHandle handle, double seconds) =>
        (handle == FadeHandle.In ? "Entrada " : "Salida ") + seconds.ToString("0.0#", CultureInfo.InvariantCulture).Replace('.', ',') + " s";

    private static double Read(EffectInstance? audio, string parameter, double limit)
    {
        if (audio?.Parameters.GetValueOrDefault(parameter) is not { } p || !double.IsFinite(p.Value) || p.Value <= 0)
        {
            return 0;
        }

        return Math.Min(p.Value, limit);
    }
}
