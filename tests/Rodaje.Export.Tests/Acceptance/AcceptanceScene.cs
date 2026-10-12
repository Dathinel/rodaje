using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.TestMedia;

namespace Rodaje.Export.Tests.Acceptance;

/// <summary>Dónde se lee el número dibujado de una capa en la salida de 1920×1080.</summary>
internal sealed record CodeRegion(int X, int Y, int Width, int Height);

/// <summary>Una capa de la escena: sus clips, su medio y los intervalos en que una transición tapa el número.</summary>
internal sealed record SceneLayer(string Name, CodeRegion Region, Rational MediaRate, IReadOnlyList<Clip> Clips, IReadOnlyList<(Time From, Time To)> Covered);

/// <summary>
/// La escena de la aceptación de la fase 6: 1080p30, tres capas con efectos, velocidades y transiciones, y el audio del
/// medio principal a 1× en toda la duración. Cada capa deja su número de cuadro legible en un lugar fijo de la salida:
/// la principal arriba a la izquierda (pantalla completa), las otras dos como PiP a la mitad en los cuadrantes de abajo.
/// </summary>
/// <remarks>
/// La principal repite un bloque de 60 s: 10 s a 1× con color, 2×, 0,5×, 1001/1000×, «Bala» y congelado hasta el final
/// del bloque, con fundido, cortinilla, empuje y fundido a negro entre los cinco primeros. El PiP izquierdo alterna 1× y
/// 2× de un HEVC a 25 fps con color; el derecho, clips a 1× de un H.264 a 23,976 con fundidos.
/// </remarks>
internal sealed class AcceptanceScene
{
    public const string SequenceId = "sec";
    public const int Width = 1920;
    public const int Height = 1080;
    public static readonly Rational Rate = Rational.Fps30;

    /// <summary>Del audio: el clip empieza en este segundo del medio principal y suena a 1× desde 0.</summary>
    public static readonly Time AudioSourceIn = Time.FromSeconds(1);

    /// <summary>11 min de 1080p a 29,97 con AAC y la marca de audio por segundo.</summary>
    public static MediaSpec MainMedia { get; } =
        new("exportar-1080p-2997-11min", 1920, 1080, Rational.Ntsc30, TimeSpan.FromMinutes(11))
        {
            Codec = VideoCodec.H264,
            GopLength = 60,
            BFrames = 2,
            Audio = AudioCodec.Aac,
            Container = ContainerFormat.Mp4,
            VideoBitrateKbps = 8000,
        };

    private static readonly Time Block = Time.FromSeconds(60);
    private static readonly Time Fade = Time.FromSeconds(0.5);

    private AcceptanceScene(Project project, Time duration, IReadOnlyList<SceneLayer> layers)
    {
        Project = project;
        Duration = duration;
        Layers = layers;
    }

    public Project Project { get; }

    public Time Duration { get; }

    public long FrameCount => Duration.ToFrames(Rate);

    public IReadOnlyList<SceneLayer> Layers { get; }

    /// <summary>Transiciones de la escena (todas las pistas).</summary>
    public int Transitions => Project.Sequences[0].VideoTracks.Sum(t => t.Transitions.Count);

    /// <param name="length">Múltiplo de 60 s.</param>
    public static AcceptanceScene Build(Time length)
    {
        if (length <= Time.Zero || length.Ticks % Block.Ticks != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "La escena se arma en bloques de 60 s.");
        }

        TestMediaGenerator generator = TestMediaGenerator.CreateDefault() ?? throw new InvalidOperationException(FfmpegTool.UnavailableReason);
        var sequence = new Sequence { Id = SequenceId, Name = "Aceptación", FrameRate = Rate, Width = Width, Height = Height };
        var project = new Project { Id = "p", Name = "Aceptación de la exportación", Sequences = [sequence] };
        MediaRef main = AddMedia(project, generator, MainMedia, "a");
        MediaRef left = AddMedia(project, generator, TestMediaPresets.Hevc25Mkv, "b");
        MediaRef right = AddMedia(project, generator, TestMediaPresets.H264Ntsc24Aac, "c");
        var v0 = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Principal" };
        var v1 = new Track { Id = "v1", Kind = TrackKind.Video, Name = "PiP izquierdo" };
        var v2 = new Track { Id = "v2", Kind = TrackKind.Video, Name = "PiP derecho" };
        var a0 = new Track { Id = "a0", Kind = TrackKind.Audio, Name = "Sonido" };
        sequence.VideoTracks.AddRange([v0, v1, v2]);
        sequence.AudioTracks.Add(a0);

        BuildMain(project, v0, main.Id, length);
        BuildLeft(v1, left, length);
        BuildRight(v2, right, length);
        a0.Clips.Add(new Clip { Id = "sonido", MediaId = main.Id, Start = Time.Zero, SourceIn = AudioSourceIn, SourceOut = AudioSourceIn + length });

        IReadOnlyList<string> issues = ModelValidator.Validate(project);
        if (issues.Count > 0)
        {
            throw new InvalidOperationException("La escena de aceptación no es válida: " + string.Join("; ", issues));
        }

        Time duration = sequence.Duration;
        return new AcceptanceScene(project, duration,
        [
            Layer("principal", new CodeRegion(0, 0, Width, Height), main, v0),
            Layer("PiP izquierdo", new CodeRegion(0, Height / 2, Width / 2, Height / 2), left, v1),
            Layer("PiP derecho", new CodeRegion(Width / 2, Height / 2, Width / 2, Height / 2), right, v2),
        ]);
    }

    /// <summary>
    /// El cuadro del medio que la capa debe mostrar en el cuadro <paramref name="frame"/> de la secuencia, según
    /// <see cref="ClipTimeMap"/>; <c>null</c> si una transición tapa el número o no hay clip.
    /// </summary>
    public static long? Expected(SceneLayer layer, long frame)
    {
        Time at = Time.FromFrames(frame, Rate);
        foreach ((Time from, Time to) in layer.Covered)
        {
            if (at >= from && at < to)
            {
                return null;
            }
        }

        foreach (Clip clip in layer.Clips)
        {
            if (at >= clip.Start && at < clip.End)
            {
                Time source = clip.TimeMap.Source(at);
                return (long)((Int128)source.Ticks * layer.MediaRate.Num / ((Int128)Time.TicksPerSecond * layer.MediaRate.Den));
            }
        }

        return null;
    }

    /// <summary>Lee el número de la capa en la luma de un cuadro de 1920×1080 (una muestra por píxel).</summary>
    public static int? Read(SceneLayer layer, ReadOnlySpan<byte> luma)
    {
        CodeRegion r = layer.Region;
        return FrameCode.Read(luma[((r.Y * Width) + r.X)..], r.Width, r.Height, Width);
    }

    private static void BuildMain(Project project, Track track, string media, Time length)
    {
        Time source = Time.FromSeconds(1);
        int block = 0;
        for (Time start = Time.Zero; start < length; start += Block, block++)
        {
            string p = $"b{block}-";
            Time end = start + Block;
            Time cursor = start;
            var made = new List<Clip>();

            Clip Add(string id, ClipSpeed? speed, Time material)
            {
                ClipTimeMap map = ClipTimeMap.Fit(cursor, source, source + material, speed);
                var clip = new Clip { Id = p + id, MediaId = media, Speed = speed };
                map.ApplyTo(clip);
                track.Clips.Add(clip);
                made.Add(clip);
                track.SortClips();
                source += material;
                cursor = clip.End;
                return clip;
            }

            Clip plain = Add("1x", null, Time.FromSeconds(10));
            plain.Effects.Add(Effect(plain.Id + "-color", BuiltInEffects.Color, (BuiltInEffects.ColorParameters.Saturation, 0.6), (BuiltInEffects.ColorParameters.Contrast, 0.1)));
            Add("2x", new ClipSpeed { Rate = new Rational(2, 1) }, Time.FromSeconds(20));
            Add("0,5x", new ClipSpeed { Rate = new Rational(1, 2) }, Time.FromSeconds(5));
            Add("1001", new ClipSpeed { Rate = new Rational(1001, 1000) }, Time.FromSeconds(10.01));
            Clip bullet = Add("bala", null, Time.FromSeconds(8));
            Edits.ApplySpeedPreset(SequenceId, bullet.Id, SpeedPreset.Bullet).Apply(project);
            cursor = bullet.End;
            if (cursor >= end)
            {
                throw new InvalidOperationException($"El bloque {block} no cabe en 60 s: «Bala» termina en {cursor}.");
            }

            var frozen = new ClipSpeed { Rate = new Rational(1, 1), Freeze = true };
            var still = new Clip { Id = p + "congelado", MediaId = media, Speed = frozen };
            ClipTimeMap.Create(cursor, end - cursor, source, frozen).ApplyTo(still);
            track.Clips.Add(still);
            track.SortClips();

            string[] kinds = [BuiltInTransitions.Crossfade, BuiltInTransitions.Wipe, BuiltInTransitions.Push, BuiltInTransitions.Dip];
            for (int k = 0; k < kinds.Length; k++)
            {
                track.Transitions.Add(new TransitionInstance { Id = $"{p}t{k}", Type = kinds[k], From = made[k].Id, To = made[k + 1].Id, Duration = Fade });
            }
        }
    }

    private static void BuildLeft(Track track, MediaRef media, Time length)
    {
        Time cursor = Time.Zero;
        for (int n = 0; cursor < length; n++)
        {
            bool fast = n % 2 == 1;
            ClipSpeed? speed = fast ? new ClipSpeed { Rate = new Rational(2, 1) } : null;
            ClipTimeMap map = ClipTimeMap.Fit(cursor, Time.FromSeconds(1), Time.FromSeconds(19), speed);
            if (map.End > length)
            {
                map = map.TrimOut(length);
            }

            var clip = new Clip { Id = $"izq{n}", MediaId = media.Id, Speed = speed };
            map.ApplyTo(clip);
            clip.Effects.Add(Pip(clip.Id, -Width / 4.0));
            clip.Effects.Add(Effect(clip.Id + "-color", BuiltInEffects.Color, (BuiltInEffects.ColorParameters.Saturation, 0.4)));
            track.Clips.Add(clip);
            cursor = clip.End;
        }
    }

    private static void BuildRight(Track track, MediaRef media, Time length)
    {
        Time cursor = Time.Zero;
        Clip? previous = null;
        for (int n = 0; cursor < length; n++)
        {
            Time duration = cursor + Time.FromSeconds(25) > length ? length - cursor : Time.FromSeconds(25);
            var clip = new Clip { Id = $"der{n}", MediaId = media.Id, Start = cursor, SourceIn = Time.FromSeconds(2), SourceOut = Time.FromSeconds(2) + duration };
            clip.Effects.Add(Pip(clip.Id, Width / 4.0));
            track.Clips.Add(clip);
            if (previous is not null && duration >= Time.FromSeconds(1))
            {
                track.Transitions.Add(new TransitionInstance { Id = $"der-t{n}", Type = BuiltInTransitions.Crossfade, From = previous.Id, To = clip.Id, Duration = Fade });
            }

            previous = clip;
            cursor = clip.End;
        }
    }

    private static SceneLayer Layer(string name, CodeRegion region, MediaRef media, Track track)
    {
        Rational rate = media.FrameRate ?? throw new InvalidOperationException($"El medio {media.Id} no tiene video.");
        List<(Time, Time)> covered = [];
        foreach (TransitionInstance t in track.Transitions)
        {
            Clip from = track.Clips.Single(c => c.Id == t.From);
            covered.Add((from.End - t.Before, from.End + t.After));
        }

        return new SceneLayer(name, region, rate, [.. track.Clips.OrderBy(c => c.Start)], covered);
    }

    private static MediaRef AddMedia(Project project, TestMediaGenerator generator, MediaSpec spec, string id)
    {
        var reference = new MediaRef { Id = id, Path = generator.GetOrCreate(spec).Path };
        MediaProbe.Probe(reference.Path).ApplyTo(reference);
        project.Media.Add(reference);
        return reference;
    }

    // Mitad de tamaño, centrada en el cuadrante de abajo: su número cae en el mismo lugar relativo que en 960×540.
    private static EffectInstance Pip(string clipId, double x) => Effect(clipId + "-pip", BuiltInEffects.Transform,
        (BuiltInEffects.TransformParameters.Scale, 0.5), (BuiltInEffects.TransformParameters.X, x), (BuiltInEffects.TransformParameters.Y, Height / 4.0));

    private static EffectInstance Effect(string id, string type, params (string Name, double Value)[] parameters)
    {
        var effect = new EffectInstance { Id = id, Type = type };
        foreach ((string name, double value) in parameters)
        {
            effect.Parameters[name] = new Parameter { Value = value };
        }

        return effect;
    }
}
