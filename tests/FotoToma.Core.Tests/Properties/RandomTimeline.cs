using System.Globalization;
using CsCheck;
using FotoToma.Core.Commands;
using FotoToma.Core.Editing;
using FotoToma.Core.Model;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Tests.Properties;

/// <summary>Un medio a generar: tipo (0 video con audio, 1 solo video, 2 solo audio), duración en cuadros y un resto en ticks.</summary>
internal sealed record MediaSpec(int Kind, int Frames, int ExtraTicks);

/// <summary>Un clip inicial: qué medio, qué tramo, en qué pista, con qué fábrica (0 añadir al final, 1 colocar, 2 insertar, 3 sobrescribir) y dónde.</summary>
internal sealed record PlacementSpec(int Media, int InFrames, int LengthFrames, int Track, int Mode, int AtFrames, bool Linked);

/// <summary>Receta de un proyecto aleatorio, en números para que CsCheck pueda achicar un caso que falla.</summary>
internal sealed record ProjectSpec(int Rate, int VideoTracks, int AudioTracks, MediaSpec[] Media, PlacementSpec[] Placements, int LockBits)
{
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"Proyecto(tasa {Rate}, {VideoTracks}V {AudioTracks}A, bloqueos {LockBits}, medios [{string.Join("; ", Media.Select(m => m.ToString()))}], clips [{string.Join("; ", Placements.Select(p => p.ToString()))}])");
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
        (OpKind.SetClipEnabled, 2), (OpKind.LinkClips, 2), (OpKind.UnlinkClips, 2), (OpKind.Undo, 4), (OpKind.Redo, 3),
    ];

    private static readonly OpKind[] KindTable = [.. Weights.SelectMany(w => Enumerable.Repeat(w.Kind, w.Weight))];

    public static int KindCount => KindTable.Length;

    public static readonly Gen<MediaSpec> GenMedia =
        Gen.Select(Gen.Int[0, 9], Gen.Int[1, 600], Gen.Int[0, 3], (k, f, x) => new MediaSpec(k < 6 ? 0 : k < 8 ? 1 : 2, f, x));

    public static readonly Gen<PlacementSpec> GenPlacement = Gen.Select(
        Gen.Int[0, 99], Gen.Int[0, 300], Gen.Int[1, 150], Gen.Int[0, 99], Gen.Int[0, 3], Gen.Int[0, 600], Gen.Bool,
        (m, i, l, t, mode, at, linked) => new PlacementSpec(m, i, l, t, mode, at, linked));

    public static readonly Gen<ProjectSpec> GenProject = Gen.Select(
        Gen.Int[0, Rates.Length - 1], Gen.Int[1, 3], Gen.Int[1, 3], GenMedia.Array[2, 5], GenPlacement.Array[1, 8], Gen.Int[0, 4095],
        (r, v, a, media, placements, locks) => new ProjectSpec(r, v, a, media, placements, locks));

    public static readonly Gen<OpSpec> GenOp = Gen.Select(
        Gen.Int[0, 99], Gen.Int[0, 1000], Gen.Int[0, 1000], Gen.Int[0, 1000], Gen.Int[0, 1000], Gen.Int[-40, 40],
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

        int index = 0;
        foreach (Track track in sequence.AllTracks)
        {
            track.Locked = ((spec.LockBits >> (2 * index)) & 3) == 0;
            index++;
        }

        return built;
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
