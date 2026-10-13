using System.Numerics;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using O = Rodaje.Core.Model.BuiltInEffects.TrackOptions;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Services.Tracking;

/// <summary>Lo que se hace con un seguimiento.</summary>
public enum TrackUse
{
    /// <summary>Otro clip (título, pegatina, imagen) sigue la posición, con su desfase.</summary>
    FollowPosition,

    /// <summary>Otro clip sigue posición, escala y giro (dos puntos o más).</summary>
    FollowAll,

    /// <summary>Las cuatro esquinas de otro clip van a los cuatro puntos, con perspectiva.</summary>
    CornerPin,

    /// <summary>Una máscara del clip seguido se mueve (y se deforma) con lo seguido.</summary>
    Mask,

    /// <summary>El clip seguido se mueve al revés para que lo seguido quede quieto, con recorte automático.</summary>
    Stabilize,

    /// <summary>Una zona desenfocada que sigue lo seguido (caras, matrículas).</summary>
    BlurZone,

    /// <summary>Una zona pixelada que sigue lo seguido.</summary>
    PixelateZone,
}

/// <summary>El resultado de preparar una aplicación: el comando (un paso del historial) y lo que conviene contar.</summary>
public sealed record TrackApplication(SequenceEditCommand Command, int Keyframes, double Zoom = 1);

/// <summary>
/// De un seguimiento guardado (<see cref="BuiltInEffects.Track"/>) a keyframes normales, editables y en un paso del
/// historial: los de la transformación de otro clip, los de un anclaje de esquinas, los de una máscara o los de la
/// transformación del propio clip para estabilizarlo. El cuadro de referencia es el del cursor: ahí lo que se aplica
/// queda como está (ese es el desfase) y en los demás cuadros se mueve como lo seguido. Los cuadros perdidos no ponen
/// keyframe: entre los vecinos se interpola.
/// </summary>
public static class TrackApply
{
    /// <summary>Lo más que acerca la estabilización con recorte automático.</summary>
    public const double MaxStabilizeZoom = 2.5;

    /// <summary>Si el tipo de seguimiento sirve para <paramref name="use"/>.</summary>
    public static bool Supports(PointTrackKind kind, TrackUse use) => use switch
    {
        TrackUse.FollowAll => kind != PointTrackKind.Point,
        TrackUse.CornerPin => kind is PointTrackKind.Corners or PointTrackKind.Region,
        _ => true,
    };

    /// <summary>Prepara la aplicación; nula (con el motivo en <paramref name="problem"/>) si no se puede.</summary>
    /// <param name="project">El proyecto.</param>
    /// <param name="sequence">La secuencia del clip.</param>
    /// <param name="tracked">El clip seguido.</param>
    /// <param name="trackId">El efecto del seguimiento en <paramref name="tracked"/>.</param>
    /// <param name="use">Qué hacer.</param>
    /// <param name="reference">Instante de la línea de tiempo que queda como está (el cursor).</param>
    /// <param name="targetId">El otro clip (seguir, anclar) o la máscara (máscara); nulo para una máscara nueva.</param>
    /// <param name="autoCrop">Estabilizar con recorte automático.</param>
    /// <param name="problem">Por qué no se puede.</param>
    public static TrackApplication? Prepare(Project project, Sequence sequence, Clip tracked, string trackId, TrackUse use, Time reference,
        string? targetId, bool autoCrop, out string problem)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(tracked);
        problem = "";
        if (tracked.Effects.Find(e => e.Id == trackId && e.Type == BuiltInEffects.Track) is not { } effect
            || PointTrack.Parse(effect.Options.GetValueOrDefault(O.Samples)) is not { Count: >= 2 } track)
        {
            problem = "Primero sigue algo: hacen falta al menos dos cuadros seguidos.";
            return null;
        }

        PointTrackKind kind = PointTrack.ParseKind(effect.Options.GetValueOrDefault(O.Kind));
        if (!Supports(kind, use))
        {
            problem = use == TrackUse.CornerPin ? "El anclaje de esquinas necesita un seguimiento de 4 esquinas o de región." : "Hace falta un seguimiento de dos puntos o más para seguir la escala y el giro.";
            return null;
        }

        if (project.FindMedia(tracked.MediaId) is not { Width: > 0, Height: > 0 } media)
        {
            problem = "El clip seguido no tiene imagen.";
            return null;
        }

        var context = new Context(project, sequence, tracked, track, kind, media.Width, media.Height, Reference(tracked, track, reference));
        return use switch
        {
            TrackUse.FollowPosition or TrackUse.FollowAll => Follow(context, targetId, use == TrackUse.FollowAll, out problem),
            TrackUse.CornerPin => Pin(context, targetId, out problem),
            TrackUse.Mask => Mask(context, targetId, null, out problem),
            TrackUse.BlurZone => Mask(context, null, ZoneEffect.Blur, out problem),
            TrackUse.PixelateZone => Mask(context, null, ZoneEffect.Pixelate, out problem),
            _ => Stabilize(context, autoCrop, out problem),
        };
    }

    /// <summary>El índice del cuadro seguido más cercano al instante <paramref name="timeline"/> de la línea de tiempo.</summary>
    internal static int Reference(Clip tracked, PointTrack track, Time timeline)
    {
        Time source = tracked.ToEffectTime(timeline);
        int index = track.IndexOf(source);
        if (index >= 0)
        {
            return index;
        }

        int next = Math.Clamp(~index, 0, track.Count - 1);
        int previous = Math.Max(0, next - 1);
        return (source - track.TimeAt(previous)).Ticks <= (track.TimeAt(next) - source).Ticks ? previous : next;
    }

    /// <summary>
    /// La transformación del clip en un instante del origen como la arma el motor (posición, escala, giro, opacidad, forma y
    /// recorte; sin el anclaje de esquinas).
    /// </summary>
    public static LayerTransform TransformAt(Clip clip, Time source)
    {
        ArgumentNullException.ThrowIfNull(clip);
        LayerTransform result = LayerTransform.Identity;
        if (clip.Effects.Find(e => e.Enabled && e.Type == BuiltInEffects.Transform) is { } transform)
        {
            double Value(string name) => transform.Parameters.TryGetValue(name, out Parameter? p) ? p.Evaluate(source) : T.DefaultValue(name);
            result = new LayerTransform(Value(T.X), Value(T.Y), Value(T.Scale), Value(T.Rotation), Value(T.Opacity));
            var shape = new LayerShape(Value(T.AnchorX), Value(T.AnchorY), Value(T.ScaleX), Value(T.ScaleY), Value(T.SkewX), Value(T.SkewY),
                transform.Options.GetValueOrDefault(BuiltInEffects.TransformOptions.FlipX) == "on",
                transform.Options.GetValueOrDefault(BuiltInEffects.TransformOptions.FlipY) == "on");
            result = shape.IsNone ? result : result with { Shape = shape };
        }

        if (clip.Effects.Find(e => e.Enabled && e.Type == BuiltInEffects.Crop) is { } crop
            && CropGeometry.Evaluate(crop, source) is { IsNone: false } rect)
        {
            result = result with { Crop = CropGeometry.Sanitize(rect) };
        }

        return result;
    }

    /// <summary>Dónde cae la imagen de un clip de medio en la secuencia en un instante del origen; nula si no se ve.</summary>
    public static LayerPlacement? PlacementAt(Project project, Sequence sequence, Clip clip, Time source)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clip);
        return project.FindMedia(clip.MediaId) is { Width: > 0, Height: > 0 } media
            ? LayerLayout.Place(sequence.Width, sequence.Height, media.Width, media.Height, TransformAt(clip, source))
            : null;
    }

    /// <summary>La deformación del cuadro de referencia a otro según el tipo: traslación, similitud u homografía.</summary>
    public static Homography Motion(PointTrackKind kind, ReadOnlySpan<Vector2> from, ReadOnlySpan<Vector2> to) => kind switch
    {
        PointTrackKind.Point => Homography.Translation(to[0].X - from[0].X, to[0].Y - from[0].Y),
        PointTrackKind.Two => Homography.SimilarityFrom(from[0], from[1], to[0], to[1]),
        _ => Homography.FromQuads(from, to) ?? Homography.SimilarityFrom(from[0], from[2], to[0], to[2]),
    };

    /// <summary>Solo posición, escala y giro de la deformación: con 4 puntos, la similitud de la diagonal.</summary>
    public static Homography Similarity(PointTrackKind kind, ReadOnlySpan<Vector2> from, ReadOnlySpan<Vector2> to) => kind switch
    {
        PointTrackKind.Point => Homography.Translation(to[0].X - from[0].X, to[0].Y - from[0].Y),
        PointTrackKind.Two => Homography.SimilarityFrom(from[0], from[1], to[0], to[1]),
        _ => Recentered(Homography.SimilarityFrom((from[0] + from[3]) / 2, (from[1] + from[2]) / 2, (to[0] + to[3]) / 2, (to[1] + to[2]) / 2), from, to),
    };

    // La similitud de los lados izquierdo y derecho, corrida para que el centro de las cuatro vaya al centro de las cuatro.
    private static Homography Recentered(Homography s, ReadOnlySpan<Vector2> from, ReadOnlySpan<Vector2> to)
    {
        Vector2 moved = s.Apply(Center(from)), target = Center(to);
        return Homography.Translation(target.X - moved.X, target.Y - moved.Y).After(s);
    }

    private static Vector2 Center(ReadOnlySpan<Vector2> points)
    {
        Vector2 sum = Vector2.Zero;
        foreach (Vector2 p in points)
        {
            sum += p;
        }

        return sum / points.Length;
    }

    private sealed record Context(Project Project, Sequence Sequence, Clip Tracked, PointTrack Track, PointTrackKind Kind, int MediaWidth,
        int MediaHeight, int ReferenceIndex)
    {
        /// <summary>Los cuadros que no se perdieron (algún punto con confianza 0 deja el cuadro afuera).</summary>
        public IEnumerable<int> Usable() => Enumerable.Range(0, Track.Count).Where(i => Track.FrameAt(i).All(s => !s.IsLost));

        /// <summary>Los puntos del cuadro <paramref name="index"/> en la secuencia; nulo si el clip no se ve.</summary>
        public Vector2[]? InSequence(int index)
        {
            if (PlacementAt(Project, Sequence, Tracked, Track.TimeAt(index)) is not { } layer)
            {
                return null;
            }

            return [.. Track.FrameAt(index).Select(s => layer.Map(s.Position.X * layer.LocalWidth, s.Position.Y * layer.LocalHeight))];
        }

        /// <summary>Los puntos en píxeles de la imagen de la capa (para las máscaras, que viven ahí).</summary>
        public Vector2[] InImage(int index, Vector2 size) => [.. Track.FrameAt(index).Select(s => s.Position * size)];

        /// <summary>El instante de la línea de tiempo del cuadro <paramref name="index"/>.</summary>
        public Time Timeline(int index) => Tracked.TimeMap.TimelineOf(Track.TimeAt(index));
    }

    private static TrackApplication? Follow(Context c, string? targetId, bool all, out string problem)
    {
        problem = "";
        if (targetId is null || c.Sequence.FindClip(targetId) is not { Track.Kind: TrackKind.Video } found || found.Clip.Id == c.Tracked.Id)
        {
            problem = "Elige otro clip de video, título o imagen que se superponga al seguido.";
            return null;
        }

        Clip target = found.Clip;
        if (c.InSequence(c.ReferenceIndex) is not { } reference)
        {
            problem = "El clip seguido no se ve en el cuadro de referencia.";
            return null;
        }

        Time referenceSource = target.ToEffectTime(c.Timeline(c.ReferenceIndex));
        LayerTransform start = TransformAt(target, referenceSource);
        var center = new Vector2((float)((c.Sequence.Width / 2.0) + start.X), (float)((c.Sequence.Height / 2.0) + start.Y));
        var keys = new List<(Time Time, double X, double Y, double Scale, double Rotation)>();
        double previousRotation = 0;
        foreach (int i in c.Usable())
        {
            Time timeline = c.Timeline(i);
            if (timeline < target.Start || timeline >= target.End || c.InSequence(i) is not { } now)
            {
                continue;
            }

            Homography motion = all ? Similarity(c.Kind, reference, now) : Motion(c.Kind, reference, now);
            Vector2 moved = motion.Apply(center);
            double rotation = Math.Atan2(motion.D, motion.A) * 180 / Math.PI;
            rotation += 360 * Math.Round((previousRotation - rotation) / 360);
            previousRotation = rotation;
            double scale = Math.Sqrt((motion.A * motion.A) + (motion.D * motion.D));
            keys.Add((target.ToEffectTime(timeline), moved.X - (c.Sequence.Width / 2.0), moved.Y - (c.Sequence.Height / 2.0),
                start.Scale * scale, start.RotationDegrees + rotation));
        }

        keys = Distinct(keys, k => k.Time);
        if (keys.Count < 2)
        {
            problem = "El seguimiento no se cruza con el otro clip en la línea de tiempo.";
            return null;
        }

        string clipId = target.Id;
        var command = new SequenceEditCommand(all ? "Seguir con otro clip (posición, escala y giro)" : "Seguir con otro clip", c.Sequence.Id, ctx =>
        {
            Clip clip = ctx.Sequence.FindClip(clipId)!.Value.Clip;
            string id = EffectEdits.EnsureEffect(ctx, clipId, BuiltInEffects.Transform) ?? clip.Effects.First(e => e.Type == BuiltInEffects.Transform).Id;
            WriteKeys(ctx, clipId, id, T.X, [.. keys.Select(k => (k.Time, k.X))]);
            WriteKeys(ctx, clipId, id, T.Y, [.. keys.Select(k => (k.Time, k.Y))]);
            if (all)
            {
                WriteKeys(ctx, clipId, id, T.Scale, [.. keys.Select(k => (k.Time, k.Scale))]);
                WriteKeys(ctx, clipId, id, T.Rotation, [.. keys.Select(k => (k.Time, k.Rotation))]);
            }
        });
        return new TrackApplication(command, keys.Count);
    }

    private static TrackApplication? Pin(Context c, string? targetId, out string problem)
    {
        problem = "";
        if (targetId is null || c.Sequence.FindClip(targetId) is not { Track.Kind: TrackKind.Video } found || found.Clip.Id == c.Tracked.Id
            || found.Clip.Generator is not null || found.Clip.IsCompound)
        {
            problem = "Elige otro clip de video o imagen (no un título) para llevar sus esquinas a las del seguimiento.";
            return null;
        }

        Clip target = found.Clip;
        var keys = new List<(Time Time, double[] Offsets)>();
        foreach (int i in c.Usable())
        {
            Time timeline = c.Timeline(i);
            if (timeline < target.Start || timeline >= target.End || c.InSequence(i) is not { } quad)
            {
                continue;
            }

            Time source = target.ToEffectTime(timeline);
            if (PlacementAt(c.Project, c.Sequence, target, source) is not { } layer || !Homography.IsConvex(quad))
            {
                continue;
            }

            Vector2[] corners = [new(0, 0), new(layer.LocalWidth, 0), new(layer.LocalWidth, layer.LocalHeight), new(0, layer.LocalHeight)];
            double[] offsets = new double[8];
            for (int k = 0; k < 4; k++)
            {
                Vector2 local = (layer with { Projective = null }).Unmap(quad[k]) ?? corners[k];
                offsets[2 * k] = local.X - corners[k].X;
                offsets[(2 * k) + 1] = local.Y - corners[k].Y;
            }

            keys.Add((source, offsets));
        }

        keys = Distinct(keys, k => k.Time);
        if (keys.Count < 2)
        {
            problem = "El seguimiento no se cruza con el otro clip en la línea de tiempo.";
            return null;
        }

        string clipId = target.Id;
        var command = new SequenceEditCommand("Anclar las esquinas al seguimiento", c.Sequence.Id, ctx =>
        {
            Clip clip = ctx.Sequence.FindClip(clipId)!.Value.Clip;
            string id = EffectEdits.EnsureEffect(ctx, clipId, BuiltInEffects.CornerPin) ?? clip.Effects.First(e => e.Type == BuiltInEffects.CornerPin).Id;
            for (int p = 0; p < 8; p++)
            {
                WriteKeys(ctx, clipId, id, BuiltInEffects.CornerPinParameters.All[p], [.. keys.Select(k => (k.Time, k.Offsets[p]))]);
            }
        });
        return new TrackApplication(command, keys.Count);
    }

    private static TrackApplication? Mask(Context c, string? maskId, ZoneEffect? zone, out string problem)
    {
        problem = "";
        if (PlacementAt(c.Project, c.Sequence, c.Tracked, c.Track.TimeAt(c.ReferenceIndex)) is not { } layer)
        {
            problem = "El clip seguido no se ve en el cuadro de referencia.";
            return null;
        }

        var size = new Vector2(layer.LocalWidth, layer.LocalHeight);
        Time at = c.Track.TimeAt(c.ReferenceIndex);
        MaskSpec spec;
        MaskVertex[] vertices = [];
        EffectInstance? mask = maskId is null ? null : c.Tracked.Effects.Find(e => e.Id == maskId && e.Type == BuiltInEffects.Mask);
        if (maskId is not null && mask is null)
        {
            problem = "La máscara ya no está.";
            return null;
        }

        if (mask is not null)
        {
            spec = MaskGeometry.Evaluate(mask, at);
            vertices = spec.HasVertices ? MaskGeometry.Vertices(mask, at) : [];
        }
        else
        {
            // Una elipse que abarca los puntos (con un punto, el patrón agrandado).
            Vector2[] points = [.. c.Track.FrameAt(c.ReferenceIndex).Select(s => s.Position)];
            Vector2 min = points.Aggregate(Vector2.Min), max = points.Aggregate(Vector2.Max);
            Vector2 extent = Vector2.Max(max - min, new Vector2(0.12f * size.Y / size.X, 0.12f) * (points.Length == 1 ? 1.4f : 0.3f));
            Vector2 middle = (min + max) / 2;
            spec = MaskSpec.Default with { Shape = MaskShape.Ellipse, X = middle.X, Y = middle.Y, Width = extent.X * 1.3, Height = extent.Y * 1.3 };
        }

        Vector2[] reference = c.InImage(c.ReferenceIndex, size);
        bool shape = vertices.Length > 0 && c.Kind is PointTrackKind.Corners;
        MaskTrackParts parts = c.Kind == PointTrackKind.Point ? MaskTrackParts.Position : MaskTrackParts.Similarity;
        parts |= shape ? MaskTrackParts.Shape : 0;
        var keys = new List<MaskTrackKey>();
        double rotation = spec.Rotation;
        foreach (int i in c.Usable())
        {
            Homography motion = Motion(c.Kind, reference, c.InImage(i, size));
            Vector2 Map(Vector2 image) => motion.Apply(image * size) / size;
            MaskTrackKey key = MaskTracking.Key(spec, vertices, c.Track.TimeAt(i), Map, shape ? null : [], size.X, size.Y, rotation);
            rotation = key.Rotation;
            if (key.Time >= c.Tracked.SourceIn && key.Time < c.Tracked.SourceOut)
            {
                keys.Add(key);
            }
        }

        if (keys.Count < 2)
        {
            problem = "No hay cuadros seguidos dentro del clip.";
            return null;
        }

        string clipId = c.Tracked.Id;
        MaskSpec zoneSpec = spec;
        string label = zone switch
        {
            ZoneEffect.Blur => "Desenfocar la zona seguida",
            ZoneEffect.Pixelate => "Pixelar la zona seguida",
            _ => mask is null ? "Máscara que sigue el seguimiento" : "Seguir con la máscara",
        };
        var command = new SequenceEditCommand(label, c.Sequence.Id, ctx =>
        {
            string id = maskId ?? (zone is { } kind
                ? MaskEdits.AddZone(ctx, clipId, kind, zoneSpec.X, zoneSpec.Y, zoneSpec.Width, zoneSpec.Height).MaskId
                : MaskEdits.AddMask(ctx, clipId, MaskShape.Ellipse, zoneSpec.X, zoneSpec.Y, zoneSpec.Width, zoneSpec.Height));
            MaskEdits.ApplyTrack(ctx, clipId, id, keys, parts);
            return [id];
        });
        return new TrackApplication(command, keys.Count);
    }

    private static TrackApplication? Stabilize(Context c, bool autoCrop, out string problem)
    {
        problem = "";
        Time referenceSource = c.Track.TimeAt(c.ReferenceIndex);
        if (PlacementAt(c.Project, c.Sequence, c.Tracked, referenceSource) is not { } layer || c.InSequence(c.ReferenceIndex) is not { } reference)
        {
            problem = "El clip seguido no se ve en el cuadro de referencia.";
            return null;
        }

        Matrix3x2 m = layer.LocalToSequence;
        var base0 = new Homography(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1);
        var frames = new List<(Time Time, Homography Inverse)>();
        foreach (int i in c.Usable())
        {
            Time source = c.Track.TimeAt(i);
            if (source < c.Tracked.SourceIn || source >= c.Tracked.SourceOut || c.InSequence(i) is not { } now)
            {
                continue;
            }

            // Lo que se movió desde la referencia, deshecho: la capa con la transformación de la referencia y la similitud inversa.
            if (Similarity(c.Kind, reference, now).Inverse() is { } inverse)
            {
                frames.Add((source, inverse.After(base0)));
            }
        }

        if (frames.Count < 2)
        {
            problem = "No hay cuadros seguidos dentro del clip.";
            return null;
        }

        double zoom = autoCrop ? CoverZoom(frames.Select(f => f.Inverse), layer.LocalWidth, layer.LocalHeight, c.Sequence.Width, c.Sequence.Height) : 1;
        Homography z = Homography.Translation(c.Sequence.Width / 2.0, c.Sequence.Height / 2.0)
            .After(Homography.Similarity(zoom, 0, 0, 0)).After(Homography.Translation(-c.Sequence.Width / 2.0, -c.Sequence.Height / 2.0));
        var keys = new List<(Time Time, double X, double Y, double Scale, double Rotation)>();
        double previous = 0;
        var localCenter = new Vector2(layer.LocalWidth / 2, layer.LocalHeight / 2);
        foreach ((Time time, Homography placed) in frames)
        {
            Homography n = z.After(placed);
            Vector2 center = n.Apply(localCenter);
            double rotation = Math.Atan2(n.D, n.A) * 180 / Math.PI;
            rotation += 360 * Math.Round((previous - rotation) / 360);
            previous = rotation;
            // Los píxeles locales son los de la secuencia a escala 1: la escala de la matriz es la de la transformación.
            keys.Add((time, center.X - (c.Sequence.Width / 2.0), center.Y - (c.Sequence.Height / 2.0),
                Math.Sqrt((n.A * n.A) + (n.D * n.D)), rotation));
        }

        string clipId = c.Tracked.Id;
        var command = new SequenceEditCommand("Estabilizar con el seguimiento", c.Sequence.Id, ctx =>
        {
            Clip clip = ctx.Sequence.FindClip(clipId)!.Value.Clip;
            string id = EffectEdits.EnsureEffect(ctx, clipId, BuiltInEffects.Transform) ?? clip.Effects.First(e => e.Type == BuiltInEffects.Transform).Id;
            WriteKeys(ctx, clipId, id, T.X, [.. keys.Select(k => (k.Time, k.X))]);
            WriteKeys(ctx, clipId, id, T.Y, [.. keys.Select(k => (k.Time, k.Y))]);
            if (c.Kind != PointTrackKind.Point || zoom != 1)
            {
                WriteKeys(ctx, clipId, id, T.Scale, [.. keys.Select(k => (k.Time, k.Scale))]);
            }

            if (c.Kind != PointTrackKind.Point)
            {
                WriteKeys(ctx, clipId, id, T.Rotation, [.. keys.Select(k => (k.Time, k.Rotation))]);
            }
        });
        return new TrackApplication(command, keys.Count, zoom);
    }

    /// <summary>
    /// El menor acercamiento (sobre el centro del cuadro) con el que la imagen cubre todo el cuadro en todos los cuadros
    /// (búsqueda binaria hasta <see cref="MaxStabilizeZoom"/>).
    /// </summary>
    internal static double CoverZoom(IEnumerable<Homography> placements, float localWidth, float localHeight, int width, int height)
    {
        Homography[] all = [.. placements];
        bool Covers(double zoom)
        {
            Homography z = Homography.Translation(width / 2.0, height / 2.0).After(Homography.Similarity(zoom, 0, 0, 0))
                .After(Homography.Translation(-width / 2.0, -height / 2.0));
            foreach (Homography p in all)
            {
                if (z.After(p).Inverse() is not { } back)
                {
                    return false;
                }

                foreach (Vector2 corner in new Vector2[] { new(0, 0), new(width, 0), new(width, height), new(0, height) })
                {
                    Vector2 local = back.Apply(corner);
                    if (local.X < -0.01f || local.Y < -0.01f || local.X > localWidth + 0.01f || local.Y > localHeight + 0.01f)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        if (Covers(1))
        {
            return 1;
        }

        if (!Covers(MaxStabilizeZoom))
        {
            return MaxStabilizeZoom;
        }

        double low = 1, high = MaxStabilizeZoom;
        for (int i = 0; i < 40; i++)
        {
            double mid = (low + high) / 2;
            (low, high) = Covers(mid) ? (low, mid) : (mid, high);
        }

        return high;
    }

    /// <summary>
    /// Reemplaza los keyframes del parámetro entre el primero y el último de <paramref name="keys"/> por ellos (lineales); los
    /// de afuera quedan. Un parámetro fijo hasta ahora queda animado solo en ese tramo.
    /// </summary>
    internal static void WriteKeys(EditContext ctx, string clipId, string effectId, string name, IReadOnlyList<(Time Time, double Value)> keys)
    {
        Clip clip = ctx.Sequence.FindClip(clipId)!.Value.Clip;
        EffectInstance effect = clip.Effects.First(e => e.Id == effectId);
        Time first = keys[0].Time, last = keys[^1].Time;
        var list = new List<Keyframe>();
        if (effect.Parameters.TryGetValue(name, out Parameter? parameter))
        {
            list.AddRange(parameter.Keyframes.Where(k => k.Time < first || k.Time > last));
        }

        list.AddRange(keys.Select(k => new Keyframe(k.Time, k.Value, Interpolation.Linear)));
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        EffectEdits.ReplaceKeyframes(ctx, clipId, effectId, name, list);
    }

    private static List<TItem> Distinct<TItem>(List<TItem> items, Func<TItem, Time> time)
    {
        var result = new List<TItem>(items.Count);
        foreach (TItem item in items.OrderBy(time))
        {
            if (result.Count == 0 || time(result[^1]) != time(item))
            {
                result.Add(item);
            }
        }

        return result;
    }
}
