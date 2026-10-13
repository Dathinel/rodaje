using System.Globalization;
using CsCheck;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Properties;

/// <summary>Un medio a generar: tipo (0 video con audio, 1 solo video, 2 solo audio), duración en cuadros y un resto en ticks.</summary>
internal sealed record MediaSpec(int Kind, int Frames, int ExtraTicks);

/// <summary>Un clip inicial: qué medio, qué tramo, en qué pista, con qué fábrica (0 añadir al final, 1 colocar, 2 insertar, 3 sobrescribir) y dónde.</summary>
internal sealed record PlacementSpec(int Media, int InFrames, int LengthFrames, int Track, int Mode, int AtFrames, bool Linked);

/// <summary>
/// Receta de un proyecto aleatorio, en números para que CsCheck pueda achicar un caso que falla. <see cref="Extras"/>
/// decide qué cortes llevan transición y qué clips y pistas llevan efectos; <see cref="Speeds"/>, qué clips llevan
/// velocidad, curva, reversa o un congelado.
/// </summary>
/// <remarks>
/// Con <see cref="Curves"/>, cada clip que lleva velocidad lleva una curva o un preajuste. <see cref="Titles"/> pone
/// títulos (clips generadores) al final de algunas pistas de video, a veces seguidos y con transición entre ellos.
/// </remarks>
internal sealed record ProjectSpec(int Rate, int VideoTracks, int AudioTracks, MediaSpec[] Media, PlacementSpec[] Placements, int LockBits, int Extras = 0, int Speeds = 0,
    bool Curves = false, int Titles = 0)
{
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"Proyecto(tasa {Rate}, {VideoTracks}V {AudioTracks}A, bloqueos {LockBits}, extras {Extras}, velocidades {Speeds}{(Curves ? " (curvas)" : "")}, títulos {Titles}, medios [{string.Join("; ", Media.Select(m => m.ToString()))}], clips [{string.Join("; ", Placements.Select(p => p.ToString()))}])");
}

/// <summary>Operación en bruto; sus números se interpretan contra el estado del proyecto al ejecutarla.</summary>
internal sealed record OpSpec(int Kind, int A, int B, int C, int D, int E);

/// <summary>Las operaciones que se prueban: todas las fábricas de <see cref="Edits"/> más deshacer y rehacer.</summary>
internal enum OpKind
{
    Split,
    Trim,
    RippleTrim,
    Roll,
    Slip,
    Slide,
    Move,
    InsertMedia,
    Insert,
    Overwrite,
    AppendMedia,
    Lift,
    RippleDelete,
    CloseGap,
    AddTrack,
    RemoveTrack,
    MoveTrack,
    SetTrackState,
    SetClipEnabled,
    LinkClips,
    UnlinkClips,
    ReorderOnMainTrack,
    AddTransition,
    RemoveTransition,
    SetTransitionDuration,
    SetTransitionType,
    SetTransitionOption,
    AddEffect,
    SetEffectOption,
    SetKeyframe,
    SetSpeed,
    RateStretch,
    FreezeFrame,
    SetCurve,
    SetReverse,
    SetPreservePitch,
    ApplySpeedPreset,
    MoveClips,
    GroupClips,
    UngroupClips,
    SetClipColor,
    SetTrackHeight,
    CloseAllGaps,
    Resync,
    SourceEdit,
    Undo,
    Redo,
}

internal sealed class BuiltProject(Project project, Rational rate, IIdGenerator ids)
{
    public const string SequenceId = "seq";

    public Project Project { get; } = project;

    public Sequence Sequence => Project.GetSequence(SequenceId);

    public Rational Rate { get; } = rate;

    public IIdGenerator Ids { get; } = ids;
}

/// <summary>Una operación ya resuelta contra el estado actual: el comando (nulo para deshacer y rehacer) y sus datos.</summary>
internal sealed record ResolvedOp(OpKind Kind, SequenceEditCommand? Command, string Text, string? ClipId = null, Time Delta = default, Time At = default);

internal static class RandomTimeline
{
    public static readonly Rational[] Rates =
    [
        Rational.Ntsc24, Rational.Fps24, Rational.Fps25, Rational.Ntsc30,
        Rational.Fps30, Rational.Fps50, Rational.Ntsc60, Rational.Fps60,
    ];

    /// <summary>Peso relativo de cada operación en el sorteo.</summary>
    private static readonly (OpKind Kind, int Weight)[] Weights =
    [
        (OpKind.Split, 8), (OpKind.Trim, 7), (OpKind.RippleTrim, 6), (OpKind.Roll, 6), (OpKind.Slip, 6),
        (OpKind.Slide, 6), (OpKind.Move, 9), (OpKind.InsertMedia, 4), (OpKind.Insert, 2), (OpKind.Overwrite, 2),
        (OpKind.AppendMedia, 4), (OpKind.Lift, 3), (OpKind.RippleDelete, 4), (OpKind.CloseGap, 4),
        (OpKind.AddTrack, 3), (OpKind.RemoveTrack, 2), (OpKind.MoveTrack, 3), (OpKind.SetTrackState, 4),
        (OpKind.SetClipEnabled, 2), (OpKind.LinkClips, 2), (OpKind.UnlinkClips, 2), (OpKind.ReorderOnMainTrack, 3),
        (OpKind.AddTransition, 7), (OpKind.RemoveTransition, 2), (OpKind.SetTransitionDuration, 3), (OpKind.SetTransitionType, 2),
        (OpKind.SetTransitionOption, 2), (OpKind.AddEffect, 3), (OpKind.SetEffectOption, 2), (OpKind.SetKeyframe, 3),
        (OpKind.SetSpeed, 6), (OpKind.RateStretch, 5), (OpKind.FreezeFrame, 3), (OpKind.SetCurve, 3), (OpKind.SetReverse, 3),
        (OpKind.SetPreservePitch, 1), (OpKind.ApplySpeedPreset, 2),
        (OpKind.MoveClips, 5), (OpKind.GroupClips, 3), (OpKind.UngroupClips, 2), (OpKind.SetClipColor, 2), (OpKind.SetTrackHeight, 1),
        (OpKind.CloseAllGaps, 3), (OpKind.Resync, 2), (OpKind.SourceEdit, 5), (OpKind.Undo, 4), (OpKind.Redo, 3),
    ];

    private static readonly OpKind[] KindTable = [.. Weights.SelectMany(w => Enumerable.Repeat(w.Kind, w.Weight))];

    public static int KindCount => KindTable.Length;

    /// <summary>Lo que se sortea sobre proyectos con curvas: cortes, recortes, estirar, curvas y lo que mueve clips.</summary>
    private static readonly OpKind[] CurveKinds =
    [
        OpKind.Split, OpKind.Split, OpKind.Split, OpKind.Trim, OpKind.Trim, OpKind.Trim, OpKind.RippleTrim, OpKind.RippleTrim,
        OpKind.Roll, OpKind.Slide, OpKind.Slip, OpKind.Move, OpKind.RateStretch, OpKind.RateStretch, OpKind.RateStretch,
        OpKind.SetCurve, OpKind.ApplySpeedPreset, OpKind.ApplySpeedPreset, OpKind.AddTransition, OpKind.SetReverse,
        OpKind.RippleDelete, OpKind.Undo, OpKind.Redo,
    ];

    /// <summary>Operaciones del sorteo con curvas (<see cref="CurveKinds"/>).</summary>
    public static readonly Gen<OpSpec> GenCurveOp = Gen.Select(
        Gen.Int[0, CurveKinds.Length - 1], Gen.Int[0, 1000], Gen.Int[0, 1000], Gen.Int[0, 1000], Gen.Int[0, 1000], Gen.Int[-40, 40],
        (k, a, b, c, d, e) => new OpSpec(Array.IndexOf(KindTable, CurveKinds[k]), a, b, c, d, e));

    public static readonly Gen<MediaSpec> GenMedia =
        Gen.Select(Gen.Int[0, 9], Gen.Int[1, 600], Gen.Int[0, 3], (k, f, x) => new MediaSpec(k < 6 ? 0 : k < 8 ? 1 : 2, f, x));

    public static readonly Gen<PlacementSpec> GenPlacement = Gen.Select(
        Gen.Int[0, 99], Gen.Int[0, 300], Gen.Int[1, 150], Gen.Int[0, 99], Gen.Int[0, 3], Gen.Int[0, 600], Gen.Bool,
        (m, i, l, t, mode, at, linked) => new PlacementSpec(m, i, l, t, mode, at, linked));

    public static readonly Gen<ProjectSpec> GenProject = Gen.Select(
        Gen.Select(
            Gen.Int[0, Rates.Length - 1], Gen.Int[1, 3], Gen.Int[1, 3], GenMedia.Array[2, 5], GenPlacement.Array[1, 8], Gen.Int[0, 4095], Gen.Int[0, 65535],
            (r, v, a, media, placements, locks, extras) => new ProjectSpec(r, v, a, media, placements, locks, extras)),
        Gen.Int[0, 65535],
        Gen.Int[0, 65535],
        (spec, speeds, titles) => spec with { Speeds = speeds, Titles = titles % 4 == 0 ? titles : 0 });

    /// <summary>Proyectos con títulos en casi todas las pistas de video.</summary>
    public static readonly Gen<ProjectSpec> GenTitleProject = Gen.Select(GenProject, Gen.Int[0, 65535],
        (spec, titles) => spec with { Titles = titles | 0x111 });

    /// <summary>Proyectos donde casi todos los clips llevan una curva o un preajuste.</summary>
    public static readonly Gen<ProjectSpec> GenCurveProject = Gen.Select(GenProject, Gen.Int[0, 65535],
        (spec, speeds) => spec with { Speeds = speeds | 0x5555, Curves = true });

    /// <summary>Velocidades que se sortean; la última está fuera de rango para probar el rechazo.</summary>
    public static readonly Rational[] SpeedRates =
    [
        new(1, 10), new(1, 4), new(37, 100), new(1, 2), new(1, 1), new(1001, 1000), new(3, 2), new(2, 1), new(7, 3), new(4, 1),
        new(100, 1), new(101, 1),
    ];

    /// <summary>Formas de curva que se sortean (la duración la pone la edición); la última se sale de rango.</summary>
    public static readonly SpeedPoint[][] CurveShapes =
    [
        [new(0, 100), new(10_000, 300)],
        [new(0, 400), new(3000, 30), new(7000, 30), new(10_000, 400)],
        [new(0, 10), new(10_000, 1000)],
        [new(0, 250), new(5000, 50), new(10_000, 250)],
        [new(0, 5), new(10_000, 100)],
    ];

    /// <summary>Tipos que se sortean al añadir efectos; incluye una transición y uno desconocido para probar los rechazos.</summary>
    private static readonly string[] EffectTypes =
    [
        BuiltInEffects.Color, BuiltInEffects.Blend, BuiltInEffects.Audio, BuiltInEffects.Transform,
        BuiltInEffects.Vignette, BuiltInTransitions.Crossfade, "otro.efecto",
    ];

    private static readonly string[] OptionNames =
    [
        BuiltInEffects.BlendOptions.Mode, BuiltInEffects.ColorOptions.Lut, BuiltInEffects.CommonOptions.Source,
        BuiltInTransitions.PushOptions.Direction, BuiltInTransitions.PresetOptions.Preset, "otra",
    ];

    /// <summary>
    /// Carpeta absoluta de las LUT sueltas (<c>file:</c>): en memoria esas referencias van siempre absolutas, como las deja
    /// <c>LutReference.ForFile</c> y la apertura del proyecto (tarea 236); solo el archivo las guarda relativas.
    /// </summary>
    private static readonly string LutFolder = Path.Combine(Path.GetTempPath(), "rodaje-luts").Replace('\\', '/');

    private static readonly string[] OptionValues =
    [
        "multiply", "softLight", "up", "left", "", "rodaje-transiciones@1.0.0/suave", "mal formada", "diagonal",
        $"file:{LutFolder}/cálido 2.cube", "file:", "file:a<b.cube",
    ];

    public static readonly Gen<OpSpec> GenOp = Gen.Select(
        Gen.Int[0, KindTable.Length - 1], Gen.Int[0, 1000], Gen.Int[0, 1000], Gen.Int[0, 1000], Gen.Int[0, 1000], Gen.Int[-40, 40],
        (k, a, b, c, d, e) => new OpSpec(k, a, b, c, d, e));

    /// <summary>Arma el proyecto de la receta; los clips iniciales que no caben se saltan.</summary>
    public static BuiltProject Build(ProjectSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Rational rate = Rates[spec.Rate];
        var project = new Project { Id = "proyecto", Name = "Aleatorio" };
        for (int i = 0; i < spec.Media.Length; i++)
        {
            MediaSpec m = spec.Media[i];
            bool video = m.Kind != 2;
            bool audio = m.Kind != 1;
            project.Media.Add(new MediaRef
            {
                Id = Inv($"m{i + 1}"),
                Name = Inv($"medio{i + 1}"),
                Path = Inv($"medio{i + 1}.mp4"),
                Duration = Time.FromFrames(m.Frames, rate) + Time.FromTicks(m.ExtraTicks * 12_345L),
                HasVideo = video,
                HasAudio = audio,
                FrameRate = video ? rate : null,
                Width = video ? 1920 : 0,
                Height = video ? 1080 : 0,
                SampleRate = audio ? 48000 : 0,
                AudioChannels = audio ? 2 : 0,
            });
        }

        var sequence = new Sequence { Id = BuiltProject.SequenceId, Name = "Principal", FrameRate = rate };
        for (int i = 0; i < spec.VideoTracks; i++)
        {
            sequence.VideoTracks.Add(new Track { Id = Inv($"v{i + 1}"), Kind = TrackKind.Video, Name = Inv($"Video {i + 1}") });
        }

        for (int i = 0; i < spec.AudioTracks; i++)
        {
            sequence.AudioTracks.Add(new Track { Id = Inv($"a{i + 1}"), Kind = TrackKind.Audio, Name = Inv($"Audio {i + 1}") });
        }

        project.Sequences.Add(sequence);
        var ids = new SequentialIdGenerator();
        var built = new BuiltProject(project, rate, ids);

        foreach (PlacementSpec p in spec.Placements)
        {
            MediaRef media = project.Media[p.Media % project.Media.Count];
            long frames = media.Duration.ToFrames(rate);
            Time sourceIn = Time.FromFrames(p.InFrames % Math.Max(frames, 1), rate);
            Time sourceOut = Time.Min(sourceIn + Time.FromFrames(p.LengthFrames, rate), media.Duration);
            if (sourceOut <= sourceIn)
            {
                continue;
            }

            Track primary = media.HasVideo
                ? sequence.VideoTracks[p.Track % sequence.VideoTracks.Count]
                : sequence.AudioTracks[p.Track % sequence.AudioTracks.Count];
            string? linked = media.HasVideo && media.HasAudio && p.Linked
                ? sequence.AudioTracks[p.Track / 3 % sequence.AudioTracks.Count].Id
                : null;
            Time at = Time.FromFrames(p.AtFrames, rate);
            SequenceEditCommand command = p.Mode switch
            {
                0 => Edits.AppendMedia(BuiltProject.SequenceId, media.Id, sourceIn, sourceOut, primary.Id, linked, ids),
                1 => Edits.InsertMedia(BuiltProject.SequenceId, media.Id, sourceIn, sourceOut, primary.Id, at, PlacementMode.Strict, linked, ids),
                2 => Edits.InsertMedia(BuiltProject.SequenceId, media.Id, sourceIn, sourceOut, primary.Id, at, PlacementMode.Insert, linked, ids),
                _ => Edits.InsertMedia(BuiltProject.SequenceId, media.Id, sourceIn, sourceOut, primary.Id, at, PlacementMode.Overwrite, linked, ids),
            };

            try
            {
                command.Apply(project);
            }
            catch (EditException)
            {
                // Lugar ocupado: ese clip inicial no se coloca.
            }
        }

        AddExtras(built, spec.Extras);
        AddSpeeds(built, spec.Speeds, spec.Curves);
        AddTitles(built, spec.Titles);
        int index = 0;
        foreach (Track track in sequence.AllTracks)
        {
            track.Locked = ((spec.LockBits >> (2 * index)) & 3) == 0;
            index++;
        }

        return built;
    }

    /// <summary>Transiciones en algunos cortes, color y mezcla en algunos clips de video y volumen animado en las pistas de audio.</summary>
    private static void AddExtras(BuiltProject built, int extras)
    {
        const string seq = BuiltProject.SequenceId;
        int bit = 0;
        bool Next() => ((extras >> (bit++ % 16)) & 1) == 1;

        foreach (Track track in built.Sequence.AllTracks)
        {
            List<Clip> clips = [.. track.Clips];
            for (int i = 0; i + 1 < clips.Count; i++)
            {
                if (clips[i].End != clips[i + 1].Start || !Next())
                {
                    continue;
                }

                EffectDefinition[] types = [.. EffectRegistry.Transitions.Where(t => track.Kind == TrackKind.Audio || !BuiltInTransitions.IsAudio(t.Type))];
                EffectDefinition type = types[(extras + i) % types.Length];
                Time duration = Time.FromFrames(1 + ((extras >> i) % 40), built.Rate);
                SequenceEditCommand transition = Edits.AddTransition(seq, clips[i].Id, type.Type, duration, built.Ids);
                transition.Apply(built.Project);
                if (Next())
                {
                    try
                    {
                        Edits.SetTransitionAlignment(seq, transition.CreatedIds[0], (TransitionAlignment)((extras + i) % 3), built.Ids).Apply(built.Project);
                    }
                    catch (EditException)
                    {
                        // La otra transición del clip ocupa todo: se queda centrada.
                    }
                }
            }

            foreach (Clip clip in clips.Where(_ => Next()))
            {
                string type = track.Kind == TrackKind.Video ? BuiltInEffects.Color : BuiltInEffects.Audio;
                SequenceEditCommand add = Edits.AddEffect(seq, clip.Id, type, ids: built.Ids);
                add.Apply(built.Project);
                if (track.Kind == TrackKind.Video)
                {
                    Edits.SetEffectOption(seq, clip.Id, add.CreatedIds[0], BuiltInEffects.ColorOptions.Lut, "rodaje-filtros@1.0.0/calido", built.Ids).Apply(built.Project);
                    SequenceEditCommand blend = Edits.AddEffect(seq, clip.Id, BuiltInEffects.Blend, ids: built.Ids);
                    blend.Apply(built.Project);
                    Edits.SetEffectOption(seq, clip.Id, blend.CreatedIds[0], BuiltInEffects.BlendOptions.Mode, EffectRegistry.BlendModes[extras % 13], built.Ids).Apply(built.Project);
                }
            }

            if (track.Kind == TrackKind.Audio && Next())
            {
                SequenceEditCommand volume = Edits.AddEffect(seq, track.Id, BuiltInEffects.Audio, ids: built.Ids);
                volume.Apply(built.Project);
                for (int k = 0; k < 4; k++)
                {
                    Time at = Time.FromFrames((extras % 90) + (k * (17 + (extras % 50))), built.Rate);
                    Edits.SetKeyframe(seq, track.Id, volume.CreatedIds[0], BuiltInEffects.AudioParameters.GainDb, at, -6 * ((k + extras) % 3), ids: built.Ids)
                        .Apply(built.Project);
                }
            }
        }
    }

    /// <summary>
    /// Por cada pista de video, según 4 bits: de 1 a 3 títulos seguidos tras su último clip (pegados o tras un hueco), con
    /// el origen virtual a veces negativo, el estilo completo o uno mínimo, y a veces una transición entre los dos primeros.
    /// </summary>
    private static void AddTitles(BuiltProject built, int titles)
    {
        for (int i = 0; i < built.Sequence.VideoTracks.Count && i < 4; i++)
        {
            int bits = (titles >> (4 * i)) & 15;
            if (bits == 0)
            {
                continue;
            }

            Track track = built.Sequence.VideoTracks[i];
            Time at = track.End + Time.FromFrames(bits % 2 == 0 ? 0 : 3 + bits, built.Rate);
            int count = 1 + (bits % 3);
            for (int k = 0; k < count; k++)
            {
                int seed = (titles >> 2) + (7 * k) + i;
                Time length = Time.FromFrames(1 + (seed % 60), built.Rate);
                Time sourceIn = Time.FromFrames((seed % 41) - 20, built.Rate);
                track.Clips.Add(new Clip
                {
                    Id = built.Ids.NewId("titulo"),
                    Start = at,
                    SourceIn = sourceIn,
                    SourceOut = sourceIn + length,
                    Generator = new ClipGenerator
                    {
                        Text = Inv($"Título {k}\nlínea {seed % 5}"),
                        Style = seed % 3 == 0 ? new TextStyle() : Tests.Model.ModelSamples.FullStyle(),
                        Box = seed % 4 == 0 ? new TextFrame { WidthFraction = 0.8 } : null,
                    },
                });
                at += length;
            }

            if (count > 1 && (bits & 8) != 0)
            {
                Edits.AddTransition(BuiltProject.SequenceId, track.Clips[^count].Id, BuiltInTransitions.Crossfade, Time.FromFrames(1, built.Rate), built.Ids)
                    .Apply(built.Project);
            }

            if ((bits & 4) != 0)
            {
                track.Role = TrackRoles.Subtitles;
            }
        }
    }

    /// <summary>Velocidades, curvas, reversas y congelados en algunos clips; lo que no se puede (por ejemplo, congelar audio) se salta.</summary>
    private static void AddSpeeds(BuiltProject built, int speeds, bool curves)
    {
        const string seq = BuiltProject.SequenceId;
        List<Clip> clips = [.. built.Sequence.AllTracks.SelectMany(t => t.Clips)];
        for (int i = 0; i < clips.Count && i < 8; i++)
        {
            int choice = (speeds >> (2 * i)) & 3;
            if (choice == 0 || built.Sequence.FindClip(clips[i].Id) is not { } found)
            {
                continue;
            }

            Clip clip = found.Clip;
            int pick = (speeds >> 3) + i;
            SequenceEditCommand command = curves ? (pick % 2 == 0
                    ? Edits.ApplySpeedPreset(seq, clip.Id, SpeedPresets.All[pick % SpeedPresets.All.Count], (RippleScope)(pick % 3), built.Ids)
                    : Edits.SetCurve(seq, clip.Id, new SpeedCurve { Points = [.. CurveShapes[pick % (CurveShapes.Length - 1)]] }, (RippleScope)(pick % 3), built.Ids))
                : choice switch
            {
                1 => Edits.SetSpeed(seq, clip.Id, SpeedRates[pick % (SpeedRates.Length - 1)], (RippleScope)(pick % 3), built.Ids),
                2 when pick % 3 == 0 => Edits.SetReverse(seq, clip.Id, true, built.Ids),
                2 => Edits.SetCurve(seq, clip.Id, new SpeedCurve { Points = [.. CurveShapes[pick % (CurveShapes.Length - 1)]] }, (RippleScope)(pick % 3), built.Ids),
                _ => Edits.FreezeFrame(seq, clip.Id, clip.Start + Time.FromTicks(clip.Duration.Ticks / 3), Time.FromFrames(1 + (pick % 90), built.Rate), built.Ids),
            };

            try
            {
                command.Apply(built.Project);
            }
            catch (EditException)
            {
                // Clip de audio, demasiado corto o sin lugar: se queda como estaba.
            }
        }
    }

    /// <summary>Traduce la operación a un comando; a veces pide algo inexistente o imposible para probar los rechazos.</summary>
    public static ResolvedOp Resolve(BuiltProject built, OpSpec op)
    {
        ArgumentNullException.ThrowIfNull(built);
        ArgumentNullException.ThrowIfNull(op);
        const string seq = BuiltProject.SequenceId;
        IIdGenerator ids = built.Ids;
        OpKind kind = KindTable[op.Kind % KindTable.Length];
        var r = new Resolver(built);

        switch (kind)
        {
            case OpKind.Split:
            {
                string clip = r.ClipId(op.A);
                Time at = r.Instant(op.A, op.B, op.E, op.C);
                return new(kind, Edits.Split(seq, clip, at, ids), $"Split({clip}, {at.Ticks})", clip, At: at);
            }

            case OpKind.Trim:
            {
                string clip = r.ClipId(op.A);
                TrimEdge edge = op.B % 2 == 0 ? TrimEdge.In : TrimEdge.Out;
                Time delta = r.Delta(op.E, op.C);
                return new(kind, Edits.Trim(seq, clip, edge, delta, ids), $"Trim({clip}, {edge}, {delta.Ticks})", clip, delta);
            }

            case OpKind.RippleTrim:
            {
                string clip = r.ClipId(op.A);
                TrimEdge edge = op.B % 2 == 0 ? TrimEdge.In : TrimEdge.Out;
                RippleScope scope = op.D % 2 == 0 ? RippleScope.Track : RippleScope.AllTracks;
                Time delta = r.Delta(op.E, op.C);
                return new(kind, Edits.RippleTrim(seq, clip, edge, delta, scope, ids), $"RippleTrim({clip}, {edge}, {delta.Ticks}, {scope})", clip, delta);
            }

            case OpKind.Roll:
            case OpKind.Slip:
            case OpKind.Slide:
            {
                string clip = r.ClipId(op.A);
                Time delta = r.Delta(op.E, op.C);
                SequenceEditCommand command = kind switch
                {
                    OpKind.Roll => Edits.Roll(seq, clip, delta, ids),
                    OpKind.Slip => Edits.Slip(seq, clip, delta, ids),
                    _ => Edits.Slide(seq, clip, delta, ids),
                };
                return new(kind, command, $"{kind}({clip}, {delta.Ticks})", clip, delta);
            }

            case OpKind.Move:
            {
                string clip = r.ClipId(op.A);
                Time at = r.Instant(op.B, op.C, op.E, op.D);
                string? target = op.C % 3 == 0 ? r.TrackId(op.D) : null;
                PlacementMode mode = (PlacementMode)(op.D % 3);
                return new(kind, Edits.Move(seq, clip, at, target, mode, ids), $"Move({clip}, {at.Ticks}, {target ?? "-"}, {mode})", clip);
            }

            case OpKind.InsertMedia:
            case OpKind.Insert:
            case OpKind.Overwrite:
            case OpKind.AppendMedia:
            {
                (string media, Time sourceIn, Time sourceOut) = r.MediaRange(op.B, op.C, op.D);
                string track = r.TrackId(op.A);
                string? linked = op.E % 2 == 0 ? r.OtherKindTrackId(track, op.D) : null;
                Time at = r.Instant(op.C, op.A, op.E, op.B);
                PlacementMode mode = (PlacementMode)(op.D % 3);
                SequenceEditCommand command = kind switch
                {
                    OpKind.InsertMedia => Edits.InsertMedia(seq, media, sourceIn, sourceOut, track, at, mode, linked, ids),
                    OpKind.Insert => Edits.Insert(seq, media, sourceIn, sourceOut, track, at, linked, ids),
                    OpKind.Overwrite => Edits.Overwrite(seq, media, sourceIn, sourceOut, track, at, linked, ids),
                    _ => Edits.AppendMedia(seq, media, sourceIn, sourceOut, track, linked, ids),
                };
                string where = kind switch { OpKind.InsertMedia => $"{at.Ticks}, {mode}", OpKind.AppendMedia => "al final", _ => $"{at.Ticks}" };
                return new(kind, command, $"{kind}({media}, {sourceIn.Ticks}-{sourceOut.Ticks}, {track}, {linked ?? "-"}, {where})");
            }

            case OpKind.Lift:
            case OpKind.RippleDelete:
            {
                string[] clips = op.C % 3 == 0 ? [r.ClipId(op.A), r.ClipId(op.B)] : [r.ClipId(op.A)];
                RippleScope scope = op.D % 2 == 0 ? RippleScope.Track : RippleScope.AllTracks;
                SequenceEditCommand command = kind == OpKind.Lift
                    ? Edits.Lift(seq, clips, ids)
                    : Edits.RippleDelete(seq, clips, scope, ids);
                return new(kind, command, $"{kind}([{string.Join(",", clips)}], {scope})");
            }

            case OpKind.CloseGap:
            {
                string track = r.TrackId(op.A);
                Time at = r.Instant(op.B, op.C, op.E, op.D);
                RippleScope scope = op.D % 2 == 0 ? RippleScope.Track : RippleScope.AllTracks;
                return new(kind, Edits.CloseGap(seq, track, at, scope, ids), $"CloseGap({track}, {at.Ticks}, {scope})");
            }

            case OpKind.AddTrack:
            {
                TrackKind trackKind = op.A % 2 == 0 ? TrackKind.Video : TrackKind.Audio;
                int count = trackKind == TrackKind.Video ? built.Sequence.VideoTracks.Count : built.Sequence.AudioTracks.Count;
                int index = op.B % (count + 2);
                string name = op.C % 10 == 0 ? " " : "Pista nueva";
                return new(kind, Edits.AddTrack(seq, trackKind, index, name, ids), $"AddTrack({trackKind}, {index}, «{name}»)");
            }

            case OpKind.RemoveTrack:
            {
                string track = r.TrackId(op.A);
                return new(kind, Edits.RemoveTrack(seq, track, ids), $"RemoveTrack({track})");
            }

            case OpKind.MoveTrack:
            {
                string track = r.TrackId(op.A);
                int index = op.B % (built.Sequence.AllTracks.Count() + 1);
                return new(kind, Edits.MoveTrack(seq, track, index, ids), $"MoveTrack({track}, {index})");
            }

            case OpKind.SetTrackState:
            {
                string track = r.TrackId(op.A);
                bool? muted = Tri(op.B);
                bool? locked = Tri(op.B / 3);
                bool? hidden = Tri(op.B / 9);
                string? name = (op.B / 27 % 5) switch
                {
                    0 => "Renombrada",
                    1 => " ",
                    _ => null,
                };
                return new(kind, Edits.SetTrackState(seq, track, muted, locked, hidden, name, ids), $"SetTrackState({track}, m={muted}, l={locked}, h={hidden}, n={name ?? "-"})");
            }

            case OpKind.SetClipEnabled:
            {
                string clip = r.ClipId(op.A);
                bool enabled = op.B % 2 == 0;
                return new(kind, Edits.SetClipEnabled(seq, clip, enabled, ids), $"SetClipEnabled({clip}, {enabled})");
            }

            case OpKind.LinkClips:
            {
                string[] clips = op.D % 3 == 0 ? [r.ClipId(op.A), r.ClipId(op.B), r.ClipId(op.C)] : [r.ClipId(op.A), r.ClipId(op.B)];
                return new(kind, Edits.LinkClips(seq, clips, ids), $"LinkClips([{string.Join(",", clips)}])");
            }

            case OpKind.UnlinkClips:
            {
                string clip = r.ClipId(op.A);
                return new(kind, Edits.UnlinkClips(seq, clip, ids), $"UnlinkClips({clip})");
            }

            case OpKind.ReorderOnMainTrack:
            {
                string clip = r.ClipId(op.A);
                int count = built.Sequence.FindClip(clip)?.Track.Clips.Count ?? 1;
                int index = op.C % 9 == 8 ? count : op.B % count;
                return new(kind, Edits.ReorderOnMainTrack(seq, clip, index, ids), $"ReorderOnMainTrack({clip}, {index})", clip);
            }

            case OpKind.AddTransition:
            {
                string clip = r.CutClipId(op.A);
                string type = Resolver.TransitionType(op.B);
                Time? duration = (op.C % 4) switch
                {
                    0 => null,
                    1 => op.D % 37 == 36 ? Time.Zero : Time.FromFrames(1 + (op.D % 60), built.Rate),
                    2 => Time.FromFrames(1 + (op.D % 600), built.Rate),
                    _ => r.Delta(op.E, op.D),
                };
                return new(kind, Edits.AddTransition(seq, clip, type, duration, ids), $"AddTransition({clip}, {type}, {duration?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "-"})");
            }

            case OpKind.RemoveTransition:
            {
                string transition = r.TransitionId(op.A);
                return new(kind, Edits.RemoveTransition(seq, transition, ids), $"RemoveTransition({transition})");
            }

            case OpKind.SetTransitionDuration:
            {
                string transition = r.TransitionId(op.A);
                Time duration = op.C % 2 == 0 ? Time.FromFrames(op.E, built.Rate) : Time.FromFrames(op.B % 400, built.Rate);
                return new(kind, Edits.SetTransitionDuration(seq, transition, duration, ids), $"SetTransitionDuration({transition}, {duration.Ticks})");
            }

            case OpKind.SetTransitionType:
            {
                string transition = r.TransitionId(op.A);
                string type = Resolver.TransitionType(op.B);
                return new(kind, Edits.SetTransitionType(seq, transition, type, ids), $"SetTransitionType({transition}, {type})");
            }

            case OpKind.SetTransitionOption:
            {
                string transition = r.TransitionId(op.A);
                (string name, string value) = Resolver.Option(built.Sequence.AllTracks.SelectMany(t => t.Transitions).FirstOrDefault(t => t.Id == transition)?.Type, op.B, op.C);
                return new(kind, Edits.SetTransitionOption(seq, transition, name, value, ids), $"SetTransitionOption({transition}, {name}, «{value}»)");
            }

            case OpKind.AddEffect:
            {
                string owner = op.C % 3 == 0 ? r.TrackId(op.A) : r.ClipId(op.A);
                string type = EffectTypes[op.B % EffectTypes.Length];
                return new(kind, Edits.AddEffect(seq, owner, type, ids: ids), $"AddEffect({owner}, {type})");
            }

            case OpKind.SetEffectOption:
            {
                (string owner, string effect) = r.Effect(op.A);
                string? type = built.Sequence.AllTracks.SelectMany(t => t.Effects.Concat(t.Clips.SelectMany(c => c.Effects))).FirstOrDefault(e => e.Id == effect)?.Type;
                (string name, string value) = Resolver.Option(type, op.B, op.C);
                return new(kind, Edits.SetEffectOption(seq, owner, effect, name, value, ids), $"SetEffectOption({owner}, {effect}, {name}, «{value}»)");
            }

            case OpKind.SetKeyframe:
            {
                (string owner, string effect) = r.Effect(op.A);
                string name = op.B % 2 == 0 ? BuiltInEffects.AudioParameters.GainDb : BuiltInEffects.TransformParameters.Opacity;
                Time at = r.KeyframeTime(owner, op.C, op.E);
                double value = op.D % 61 - 30;
                return new(kind, Edits.SetKeyframe(seq, owner, effect, name, at, value, ids: ids), $"SetKeyframe({owner}, {effect}, {name}, {at.Ticks}, {value})");
            }

            case OpKind.SetSpeed:
            {
                string clip = r.ClipId(op.A);
                Rational rate = SpeedRates[op.B % SpeedRates.Length];
                var scope = (RippleScope)(op.C % 3);
                return new(kind, Edits.SetSpeed(seq, clip, rate, scope, ids), $"SetSpeed({clip}, {rate}, {scope})", clip);
            }

            case OpKind.RateStretch:
            {
                string clip = r.ClipId(op.A);
                TrimEdge edge = op.B % 2 == 0 ? TrimEdge.In : TrimEdge.Out;
                var scope = (RippleScope)(op.D % 3);
                Time delta = r.Delta(op.E, op.C);
                return new(kind, Edits.RateStretch(seq, clip, edge, delta, scope, ids), $"RateStretch({clip}, {edge}, {delta.Ticks}, {scope})", clip, delta);
            }

            case OpKind.FreezeFrame:
            {
                string clip = r.ClipId(op.A);
                Time at = r.Instant(op.A, op.B, op.E, op.C);
                Time? duration = (op.D % 5) switch
                {
                    0 => null,
                    1 => Time.Zero,
                    _ => Time.FromFrames(1 + (op.D % 120), built.Rate),
                };
                return new(kind, Edits.FreezeFrame(seq, clip, at, duration, ids, (RippleScope)(op.B % 3)), $"FreezeFrame({clip}, {at.Ticks}, {duration?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "-"}, {(RippleScope)(op.B % 3)})", clip, At: at);
            }

            case OpKind.SetCurve:
            {
                string clip = r.ClipId(op.A);
                SpeedCurve? curve = op.B % 6 == 5 ? null : new SpeedCurve { Points = [.. CurveShapes[op.B % CurveShapes.Length]] };
                var scope = (RippleScope)(op.C % 3);
                return new(kind, Edits.SetCurve(seq, clip, curve, scope, ids), $"SetCurve({clip}, {(curve is null ? "-" : (op.B % CurveShapes.Length).ToString(CultureInfo.InvariantCulture))}, {scope})", clip);
            }

            case OpKind.ApplySpeedPreset:
            {
                string clip = r.ClipId(op.A);
                SpeedPreset preset = SpeedPresets.All[op.B % SpeedPresets.All.Count];
                var scope = (RippleScope)(op.C % 3);
                return new(kind, Edits.ApplySpeedPreset(seq, clip, preset, scope, ids), $"ApplySpeedPreset({clip}, {preset}, {scope})", clip);
            }

            case OpKind.SetReverse:
            {
                string clip = r.ClipId(op.A);
                bool reverse = op.B % 3 != 0;
                return new(kind, Edits.SetReverse(seq, clip, reverse, ids), $"SetReverse({clip}, {reverse})", clip);
            }

            case OpKind.SetPreservePitch:
            {
                string clip = r.ClipId(op.A);
                bool preserve = op.B % 2 == 0;
                return new(kind, Edits.SetPreservePitch(seq, clip, preserve, ids), $"SetPreservePitch({clip}, {preserve})", clip);
            }

            case OpKind.MoveClips:
            {
                string[] clips = op.C % 3 == 0 ? [r.ClipId(op.A), r.ClipId(op.B)] : [r.ClipId(op.A)];
                Time delta = r.Delta(op.E, op.D);
                return new(kind, Edits.MoveClips(seq, clips, delta, ids), $"MoveClips([{string.Join(",", clips)}], {delta.Ticks})", clips[0], delta);
            }

            case OpKind.GroupClips:
            case OpKind.UngroupClips:
            {
                string[] clips = op.C % 2 == 0 ? [r.ClipId(op.A), r.ClipId(op.B), r.ClipId(op.D)] : [r.ClipId(op.A), r.ClipId(op.B)];
                SequenceEditCommand command = kind == OpKind.GroupClips ? Edits.GroupClips(seq, clips, ids) : Edits.UngroupClips(seq, clips, ids);
                return new(kind, command, $"{kind}([{string.Join(",", clips)}])");
            }

            case OpKind.SetClipColor:
            {
                string clip = r.ClipId(op.A);
                string? color = (op.B % 4) switch { 0 => null, 1 => "#e5484d", 2 => "#3E9BFF", _ => "rojo" };
                return new(kind, Edits.SetClipColor(seq, [clip], color, ids), $"SetClipColor({clip}, {color ?? "-"})", clip);
            }

            case OpKind.SetTrackHeight:
            {
                string track = r.TrackId(op.A);
                int? height = op.B % 5 == 0 ? null : op.B;
                return new(kind, Edits.SetTrackHeight(seq, track, height, ids), $"SetTrackHeight({track}, {height?.ToString(CultureInfo.InvariantCulture) ?? "-"})");
            }

            case OpKind.CloseAllGaps:
            {
                string[] tracks = op.C % 3 == 0 ? [.. built.Sequence.AllTracks.Select(t => t.Id)] : [r.TrackId(op.A)];
                RippleScope scope = (RippleScope)(op.D % 3);
                return new(kind, Edits.CloseAllGaps(seq, tracks, scope, ids), $"CloseAllGaps([{string.Join(",", tracks)}], {scope})");
            }

            case OpKind.Resync:
            {
                string clip = r.ClipId(op.A);
                ResyncMode mode = op.B % 2 == 0 ? ResyncMode.Move : ResyncMode.Slip;
                return new(kind, Edits.Resync(seq, clip, mode, ids), $"Resync({clip}, {mode})", clip);
            }

            case OpKind.SourceEdit:
            {
                (string media, Time sourceIn, Time sourceOut) = r.MediaRange(op.B, op.C, op.D);
                var action = (ThreePointAction)(((op.E % 5) + 5) % 5);
                List<Track> video = built.Sequence.VideoTracks;
                List<Track> audio = built.Sequence.AudioTracks;
                string? v = video.Count == 0 || op.A % 7 == 6 ? null : video[op.A % video.Count].Id;
                string? a = audio.Count == 0 || op.A % 5 == 4 ? null : audio[op.A % audio.Count].Id;
                Time playhead = r.Instant(op.A, op.C, op.E, op.B);
                Time? seqIn = op.D % 3 == 0 ? playhead : null;
                Time? seqOut = op.D % 6 == 0 ? playhead + Time.FromFrames(1 + (op.B % 90), built.Rate) : null;
                var request = new ThreePointRequest(
                    media,
                    op.C % 4 == 0 ? null : sourceIn,
                    op.C % 4 == 1 ? null : sourceOut,
                    seqIn,
                    seqOut,
                    playhead,
                    v,
                    a,
                    PlacementMode.Overwrite,
                    op.B % 2 == 0 ? FourPointFit.Trim : FourPointFit.FitToFill,
                    (RippleScope)(op.D % 3),
                    sourceIn + Time.FromFrames(Math.Abs(op.E % 30), built.Rate));
                return new(kind, Edits.ThreePointEdit(seq, request, action, ids), $"SourceEdit({action}, {media}, {sourceIn.Ticks}-{sourceOut.Ticks}, {v ?? "-"}/{a ?? "-"}, {playhead.Ticks}, {seqIn?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "-"}-{seqOut?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "-"})");
            }

            default:
                return new(kind, null, kind.ToString());
        }
    }

    private static bool? Tri(int value) => (value % 3) switch
    {
        0 => null,
        1 => true,
        _ => false,
    };

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>Elige clips, pistas, instantes y desplazamientos a partir de números en bruto.</summary>
    private sealed class Resolver(BuiltProject built)
    {
        private readonly List<Clip> _clips = [.. built.Sequence.AllTracks.SelectMany(t => t.Clips)];
        private readonly List<Track> _tracks = [.. built.Sequence.AllTracks];

        public string ClipId(int a) =>
            _clips.Count == 0 || a % 23 == 22 ? "clip-falta" : _clips[a % _clips.Count].Id;

        public string TrackId(int a) =>
            _tracks.Count == 0 || a % 29 == 28 ? "pista-falta" : _tracks[a % _tracks.Count].Id;

        private readonly List<TransitionInstance> _transitions = [.. built.Sequence.AllTracks.SelectMany(t => t.Transitions)];

        private readonly List<(string Owner, string Effect)> _effects =
        [
            .. built.Sequence.AllTracks.SelectMany(t => t.Effects.Select(e => (t.Id, e.Id))
                .Concat(t.Clips.SelectMany(c => c.Effects.Select(e => (c.Id, e.Id))))),
        ];

        /// <summary>Casi siempre un clip seguido de otro sin hueco.</summary>
        public string CutClipId(int a)
        {
            List<string> cuts = [.. built.Sequence.AllTracks.SelectMany(t => t.Clips.Where((c, i) => i + 1 < t.Clips.Count && t.Clips[i + 1].Start == c.End)).Select(c => c.Id)];
            return cuts.Count == 0 || a % 5 == 4 ? ClipId(a) : cuts[a % cuts.Count];
        }

        public string TransitionId(int a) =>
            _transitions.Count == 0 || a % 19 == 18 ? "transicion-falta" : _transitions[a % _transitions.Count].Id;

        /// <summary>Casi siempre una transición registrada; a veces un efecto o un tipo desconocido.</summary>
        public static string TransitionType(int b) => (b % 17) switch
        {
            15 => BuiltInEffects.Blur,
            16 => "otra.transicion",
            _ => EffectRegistry.Transitions[b % EffectRegistry.Transitions.Length].Type,
        };

        /// <summary>Casi siempre una opción del tipo con un valor aceptado; a veces otra opción o un valor que no vale.</summary>
        public static (string Name, string Value) Option(string? type, int b, int c)
        {
            OptionDefinition? option = EffectRegistry.Find(type ?? "") is { Options.Length: > 0 } definition && b % 4 != 3
                ? definition.Options[b % definition.Options.Length]
                : null;
            if (option is null)
            {
                return (OptionNames[b % OptionNames.Length], OptionValues[c % OptionValues.Length]);
            }

            string value = c % 5 == 4
                ? OptionValues[c % OptionValues.Length]
                : option.Kind == OptionKind.Choice
                    ? option.Choices[c % option.Choices.Length]
                    : option.AllowsFile && c % 3 == 0
                        ? Inv($"file:{LutFolder}/look {c % 4}.cube")
                        : Inv($"rodaje-filtros@1.{c % 3}.0/elemento-{c % 4}");
            return (option.Name, value);
        }

        public (string Owner, string Effect) Effect(int a) =>
            _effects.Count == 0 || a % 23 == 22 ? ("clip-falta", "efecto-falta") : _effects[a % _effects.Count];

        /// <summary>Dentro del clip (tiempo del origen) o de la secuencia, a veces fuera.</summary>
        public Time KeyframeTime(string owner, int c, int e)
        {
            Clip? clip = _clips.Find(x => x.Id == owner);
            Time from = clip?.SourceIn ?? Time.Zero;
            Time length = clip?.Duration ?? built.Sequence.Duration + Time.FromFrames(10, built.Rate);
            Time at = from + Time.FromTicks(length.Ticks / 7 * (c % 8));
            return c % 11 == 10 ? at + Time.FromFrames(e, built.Rate) : at;
        }

        /// <summary>Una pista del otro tipo que <paramref name="trackId"/> (para el clip enlazado); a veces una del mismo tipo.</summary>
        public string? OtherKindTrackId(string trackId, int d)
        {
            Track? track = _tracks.Find(t => t.Id == trackId);
            if (track is null)
            {
                return null;
            }

            List<Track> candidates = d % 11 == 10
                ? _tracks.FindAll(t => t.Kind == track.Kind && t != track)
                : _tracks.FindAll(t => t.Kind != track.Kind);
            return candidates.Count == 0 ? null : candidates[d % candidates.Count].Id;
        }

        /// <summary>Casi siempre cuadros enteros entre -40 y 40; a veces unos ticks fuera de la cuadrícula.</summary>
        public Time Delta(int e, int c) => c % 10 == 0
            ? Time.FromTicks(e * 997L)
            : Time.FromFrames(e, built.Rate);

        /// <summary>Casi siempre cerca de un borde o del medio de un clip; a veces un instante cualquiera.</summary>
        public Time Instant(int a, int b, int e, int c)
        {
            if (_clips.Count == 0 || b % 7 == 6)
            {
                long total = built.Sequence.Duration.ToFrames(built.Rate) + 30;
                return Time.FromFrames(b % total, built.Rate);
            }

            Clip clip = _clips[a % _clips.Count];
            Time boundary = (b % 3) switch
            {
                0 => clip.Start,
                1 => clip.End,
                _ => clip.Start + Time.FromTicks(clip.Duration.Ticks / 2),
            };
            Time at = boundary + Delta(e % 4, c);
            return at < Time.Zero ? Time.Zero : at;
        }

        /// <summary>Un medio y un tramo de él; a veces un medio que no existe o un tramo que pasa el final.</summary>
        public (string Media, Time SourceIn, Time SourceOut) MediaRange(int b, int c, int d)
        {
            if (b % 31 == 30)
            {
                return ("medio-falta", Time.Zero, Time.FromFrames(10, built.Rate));
            }

            MediaRef media = built.Project.Media[b % built.Project.Media.Count];
            long frames = Math.Max(media.Duration.ToFrames(built.Rate), 1);
            Time sourceIn = Time.FromFrames(c % frames, built.Rate);
            Time sourceOut = sourceIn + Time.FromFrames(1 + (d % 120), built.Rate);
            if (d % 13 != 12)
            {
                sourceOut = Time.Min(sourceOut, media.Duration);
            }

            return (media.Id, sourceIn, sourceOut);
        }
    }
}
