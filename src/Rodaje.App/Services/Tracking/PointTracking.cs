using System.Numerics;
using Rodaje.Analysis.Video;
using Rodaje.Core.Effects;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Tracking;

/// <summary>Qué seguir con <see cref="PointTracking"/>: los puntos en la imagen de la capa (0–1) en el cuadro de partida.</summary>
/// <param name="MediaPath">El archivo del clip.</param>
/// <param name="Start">Instante del origen del cuadro de partida.</param>
/// <param name="Limit">Hasta dónde (incluido) en el sentido elegido.</param>
/// <param name="Forward">Hacia adelante o hacia atrás.</param>
/// <param name="Kind">Puntos sueltos (1, 2 o 4) o una región (las 4 esquinas de una caja que se mueve como un todo).</param>
/// <param name="Points">Los puntos con su patrón y su zona de búsqueda, en fracciones de la imagen.</param>
/// <param name="Threshold">Confianza bajo la que un cuadro es dudoso.</param>
/// <param name="CropAt">El recorte de la capa en un instante del origen.</param>
public sealed record PointTrackRequest(string MediaPath, Time Start, Time Limit, bool Forward, PointTrackKind Kind,
    IReadOnlyList<TrackPointSpec> Points, float Threshold, Func<Time, CropRect> CropAt);

/// <summary>Un cuadro seguido: instante del origen, los puntos en la imagen de la capa (0–1) y si ya no queda ninguno.</summary>
public sealed record PointTrackFrame(Time Time, TrackSample[] Samples, bool Gone);

/// <summary>
/// Sigue puntos (o una región) en el video de un clip, cuadro a cuadro desde el de partida, en un hilo de fondo, y entrega
/// cada cuadro en cuanto sale (el visor muestra el avance en vivo). El primer cuadro entregado es el de partida, con
/// confianza 1. Termina al llegar al límite, al cancelar, si el video se acaba o si todos los puntos llevan
/// <see cref="PointTracker.MaxLostFrames"/> cuadros perdidos.
/// </summary>
/// <remarks>
/// Por qué en la CPU: el seguidor solo trabaja en ventanas de unos cientos de píxeles alrededor de cada punto (unos 7 ms
/// por cuadro con cuatro puntos, igual en 4K): subir cada cuadro a la GPU y bajar el resultado costaría más que eso. Lo que
/// manda es decodificar.
/// </remarks>
public static class PointTracking
{
    public static Task RunAsync(PointTrackRequest request, Action<PointTrackFrame> frame, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frame);
        if (request.Points.Count != PointTrack.PointsOf(request.Kind))
        {
            throw new ArgumentException("La cantidad de puntos no corresponde al tipo de seguimiento.", nameof(request));
        }

        return Task.Run(() => Run(request, frame, progress, cancellationToken), cancellationToken);
    }

    private static void Run(PointTrackRequest request, Action<PointTrackFrame> report, IProgress<double>? progress, CancellationToken token)
    {
        using var walk = new FrameWalk(request.MediaPath);
        Rational rate = walk.Rate;
        long first = walk.FrameOf(request.Start);
        long last = walk.FrameOf(request.Limit);
        int direction = request.Forward ? 1 : -1;
        long total = Math.Max(1, Math.Abs(last - first));
        Stepper? stepper = null;
        if (!walk.Read(first, (_, luma) =>
        {
            var view = new SourceView(luma.Width, luma.Height, walk.Rotation, request.CropAt(Time.FromFrames(first, rate)));
            stepper = request.Kind == PointTrackKind.Region
                ? new RegionStepper(luma, view, request.Points)
                : new PointStepper(luma, view, request.Points, request.Threshold);
        }, token))
        {
            throw new InvalidOperationException("No se pudo leer el fotograma de partida.");
        }

        report(new PointTrackFrame(Time.FromFrames(first, rate), [.. request.Points.Select(p => new TrackSample(p.Position, 1))], false));
        if ((last - first) * direction <= 0)
        {
            progress?.Report(1);
            return;
        }

        long done = 0;
        walk.Walk(first, last, request.Forward, (index, luma) =>
        {
            Time time = Time.FromFrames(index, rate);
            var view = new SourceView(luma.Width, luma.Height, walk.Rotation, request.CropAt(time));
            (TrackSample[] samples, bool gone) = stepper!.Step(luma, view);
            report(new PointTrackFrame(time, samples, gone));
            progress?.Report(Math.Min(1, ++done / (double)total));
            return !gone;
        }, token);
    }

    /// <summary>De la imagen de la capa (0–1) a píxeles del cuadro codificado: el tamaño de una caja centrada en <paramref name="center"/>.</summary>
    internal static Vector2 CodedSize(in SourceView view, Vector2 center, Vector2 size)
    {
        Vector2 a = view.ToCoded(center - (size / 2)), b = view.ToCoded(center + (size / 2));
        return Vector2.Abs(b - a);
    }

    private abstract class Stepper
    {
        public abstract (TrackSample[] Samples, bool Gone) Step(LumaFrame luma, SourceView view);
    }

    private sealed class PointStepper : Stepper
    {
        private readonly PointTracker _tracker;

        public PointStepper(LumaFrame luma, SourceView view, IReadOnlyList<TrackPointSpec> points, float threshold)
        {
            _tracker = new PointTracker(luma.Width, luma.Height, threshold);
            PointTarget[] targets = [.. points.Select(p => new PointTarget(view.ToCoded(p.Position), CodedSize(view, p.Position, p.Pattern),
                CodedSize(view, p.Position, p.Search)))];
            _tracker.Start(luma, targets);
        }

        public override (TrackSample[] Samples, bool Gone) Step(LumaFrame luma, SourceView view)
        {
            PointResult[] results = _tracker.Step(luma);
            return ([.. results.Select(r => new TrackSample(view.ToImage(r.Position), r.State == PointState.Lost ? 0 : r.Confidence))], _tracker.IsLost);
        }
    }

    // La región de las cuatro esquinas con el seguidor de regiones: su similitud lleva las esquinas de partida.
    private sealed class RegionStepper : Stepper
    {
        private readonly RegionTracker _tracker;
        private readonly Vector2[] _corners;
        private int _lost;

        public RegionStepper(LumaFrame luma, SourceView view, IReadOnlyList<TrackPointSpec> points)
        {
            _tracker = new RegionTracker(luma.Width, luma.Height);
            _corners = [.. points.Select(p => view.ToCoded(p.Position))];
            _tracker.Start(luma, _corners);
        }

        public override (TrackSample[] Samples, bool Gone) Step(LumaFrame luma, SourceView view)
        {
            RegionStep step = _tracker.Step(luma);
            _lost = step.Lost ? _lost + 1 : 0;
            float confidence = step.Lost ? 0 : Math.Clamp(step.Inliers / 24f, 0.3f, 1);
            TrackSample[] samples = [.. _corners.Select(c =>
            {
                (double x, double y) = step.Motion.Apply(c.X, c.Y);
                return new TrackSample(view.ToImage(new Vector2((float)x, (float)y)), confidence);
            })];
            return (samples, _lost >= PointTracker.MaxLostFrames);
        }
    }
}
