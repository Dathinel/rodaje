using System.Numerics;
using Rodaje.Analysis.Video;
using Rodaje.Core.Effects;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Tracking;

/// <summary>Qué seguir: la región y los puntos sueltos en la imagen de la capa (0–1) en el cuadro de partida.</summary>
/// <param name="MediaPath">El archivo del clip.</param>
/// <param name="Start">Instante del origen del cuadro de partida.</param>
/// <param name="Limit">Hasta dónde (incluido) en el sentido elegido: la salida o la entrada del clip.</param>
/// <param name="Forward">Hacia adelante o hacia atrás.</param>
/// <param name="Region">El contorno que se sigue.</param>
/// <param name="Anchors">Puntos que se siguen cada uno con su zona (vértices de una máscara, esquinas de un título).</param>
/// <param name="CropAt">El recorte de la capa en un instante del origen (para pasar de la imagen al cuadro codificado).</param>
public sealed record TrackRequest(string MediaPath, Time Start, Time Limit, bool Forward, IReadOnlyList<Vector2> Region,
    IReadOnlyList<Vector2> Anchors, Func<Time, CropRect> CropAt);

/// <summary>
/// Seguimiento de una región en el video de un clip, cuadro a cuadro desde el actual, hacia adelante o hacia atrás, con
/// <see cref="RegionTracker"/>. Lo usan las máscaras y sirve igual para títulos o pegatinas: devuelve en cada cuadro dónde
/// quedó cualquier punto de la imagen de partida (<see cref="TrackedFrame.Map"/>). Decodifica en la CPU fuera del hilo de
/// la interfaz; hacia atrás lee tramos hacia adelante y los recorre al revés.
/// </summary>
public static class RegionTracking
{
    /// <summary>
    /// Sigue la región y llama a <paramref name="frame"/> con cada cuadro (desde el hilo de fondo). Termina al llegar a
    /// <see cref="TrackRequest.Limit"/>, al cancelar o si el video se acaba; lo seguido hasta ahí ya se entregó.
    /// </summary>
    public static Task RunAsync(TrackRequest request, Action<TrackedFrame> frame, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frame);
        if (request.Region.Count < 3)
        {
            throw new ArgumentException("La región necesita al menos tres puntos.", nameof(request));
        }

        return Task.Run(() => Run(request, frame, progress, cancellationToken), cancellationToken);
    }

    private static void Run(TrackRequest request, Action<TrackedFrame> report, IProgress<double>? progress, CancellationToken token)
    {
        using var walk = new FrameWalk(request.MediaPath);
        Rational rate = walk.Rate;
        long first = walk.FrameOf(request.Start);
        long last = walk.FrameOf(request.Limit);
        int direction = request.Forward ? 1 : -1;
        long total = Math.Max(1, Math.Abs(last - first));
        if ((last - first) * direction <= 0)
        {
            progress?.Report(1);
            return;
        }

        RegionTracker? tracker = null;
        SourceView start = default;
        if (!walk.Read(first, (_, luma) =>
        {
            start = new SourceView(luma.Width, luma.Height, walk.Rotation, request.CropAt(Time.FromFrames(first, rate)));
            tracker = new RegionTracker(luma.Width, luma.Height);
            tracker.Start(luma, [.. request.Region.Select(start.ToCoded)], [.. request.Anchors.Select(start.ToCoded)]);
        }, token))
        {
            throw new InvalidOperationException("No se pudo leer el cuadro de partida.");
        }

        long done = 0;
        walk.Walk(first, last, request.Forward, (index, luma) =>
        {
            RegionStep step = tracker!.Step(luma);
            Time time = Time.FromFrames(index, rate);
            var now = new SourceView(luma.Width, luma.Height, walk.Rotation, request.CropAt(time));
            report(new TrackedFrame(time, step.Motion, [.. step.Anchors.Select(now.ToImage)], start, now, step.Lost));
            done++;
            progress?.Report(Math.Min(1, done / (double)total));
            return true;
        }, token);
    }
}
