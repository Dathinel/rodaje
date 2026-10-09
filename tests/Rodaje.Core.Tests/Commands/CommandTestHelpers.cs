using System.Globalization;
using System.Text;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Commands;

/// <summary>Proyectos pequeños armados a mano para las pruebas de comandos.</summary>
internal static class CommandFixtures
{
    public const string SequenceId = "seq-1";

    public static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Un medio, dos pistas de video (la segunda bloqueada) y una de audio; «clip-2» lleva un parámetro animado.</summary>
    public static Project Create()
    {
        var media = new MediaRef
        {
            Id = "media-1",
            Name = "toma.mp4",
            Path = "toma.mp4",
            Duration = S(60),
            HasVideo = true,
            HasAudio = true,
            FrameRate = Rational.Fps30,
            Width = 1920,
            Height = 1080,
            SampleRate = 48000,
            AudioChannels = 2,
        };

        var v1 = new Track
        {
            Id = "track-v1",
            Kind = TrackKind.Video,
            Name = "Video 1",
            Clips =
            [
                new Clip { Id = "clip-1", MediaId = "media-1", SourceIn = S(0), SourceOut = S(5), Start = S(0), LinkId = "link-a" },
                new Clip
                {
                    Id = "clip-2",
                    MediaId = "media-1",
                    SourceIn = S(10),
                    SourceOut = S(15),
                    Start = S(10),
                    Effects =
                    [
                        new EffectInstance
                        {
                            Id = "fx-1",
                            Type = "rodaje.transform",
                            Parameters = new Dictionary<string, Parameter>
                            {
                                ["opacity"] = new Parameter
                                {
                                    Value = 1,
                                    Keyframes = [new Keyframe(S(10), 0), new Keyframe(S(12), 1, Interpolation.EaseIn)],
                                },
                                ["x"] = new Parameter { Value = 0.5 },
                            },
                        },
                    ],
                },
            ],
        };

        var v2 = new Track
        {
            Id = "track-v2",
            Kind = TrackKind.Video,
            Name = "Video 2",
            Locked = true,
            Clips = [new Clip { Id = "clip-3", MediaId = "media-1", SourceIn = S(20), SourceOut = S(22), Start = S(3) }],
        };

        var a1 = new Track
        {
            Id = "track-a1",
            Kind = TrackKind.Audio,
            Name = "Audio 1",
            Clips = [new Clip { Id = "clip-4", MediaId = "media-1", SourceIn = S(0), SourceOut = S(5), Start = S(0), LinkId = "link-a" }],
        };

        var sequence = new Sequence
        {
            Id = SequenceId,
            Name = "Principal",
            VideoTracks = [v1, v2],
            AudioTracks = [a1],
        };

        return new Project
        {
            Id = "project-1",
            Name = "Prueba",
            Media = [media],
            Sequences = [sequence],
        };
    }

    public static Sequence Seq(Project project) => project.GetSequence(SequenceId);

    public static Clip NewClip(string id, double start, double length = 1) =>
        new() { Id = id, MediaId = "media-1", SourceIn = S(30), SourceOut = S(30 + length), Start = S(start) };
}

/// <summary>Volcado de texto del proyecto campo por campo; si dos difieren, el diff dice dónde.</summary>
internal static class ProjectDump
{
    public static string Of(Project project)
    {
        var sb = new StringBuilder();
        Line(sb, 0, $"proyecto {project.Id} «{project.Name}» v{project.SchemaVersion}");
        foreach (MediaRef m in project.Media)
        {
            Line(sb, 1, $"medio {m.Id} «{m.Name}» {m.Path} {m.Duration.Ticks} v={m.HasVideo} a={m.HasAudio} fps={m.FrameRate} {m.Width}x{m.Height} {m.SampleRate}Hz {m.AudioChannels}ch");
        }

        foreach (Sequence s in project.Sequences)
        {
            Line(sb, 1, $"secuencia {s.Id} «{s.Name}» {s.FrameRate} {s.Width}x{s.Height} {s.SampleRate}Hz");
            foreach (Track t in s.VideoTracks)
            {
                DumpTrack(sb, "V", t);
            }

            foreach (Track t in s.AudioTracks)
            {
                DumpTrack(sb, "A", t);
            }
        }

        return sb.ToString();
    }

    private static void DumpTrack(StringBuilder sb, string list, Track t)
    {
        Line(sb, 2, $"{list} pista {t.Id} {t.Kind} «{t.Name}» m={t.Muted} l={t.Locked} h={t.Hidden}");
        foreach (Clip c in t.Clips)
        {
            Line(sb, 3, $"clip {c.Id} {c.MediaId} in={c.SourceIn.Ticks} out={c.SourceOut.Ticks} start={c.Start.Ticks} link={c.LinkId ?? "-"} e={c.Enabled}");
            foreach (EffectInstance e in c.Effects)
            {
                Line(sb, 4, $"efecto {e.Id} {e.Type} e={e.Enabled}");
                foreach ((string key, Parameter p) in e.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    string keys = string.Join(" ", p.Keyframes.Select(k => $"[{k.Time.Ticks}:{k.Value.ToString("R", CultureInfo.InvariantCulture)}:{k.Interpolation}]"));
                    Line(sb, 5, $"{key}={p.Value.ToString("R", CultureInfo.InvariantCulture)} {keys}");
                }
            }
        }
    }

    private static void Line(StringBuilder sb, int indent, string text) =>
        sb.Append(' ', indent * 2).Append(text).Append('\n');
}

/// <summary>Registro compartido del orden en que los comandos falsos se aplican y revierten.</summary>
internal sealed class CommandLog
{
    public List<string> Entries { get; } = [];
}

/// <summary>Comando falso que suma su valor al nombre del proyecto al aplicarse y lo quita al revertirse.</summary>
internal sealed class FakeCommand(string name, CommandLog? log = null) : ICommand
{
    public string Description => name;

    public bool ThrowOnApply { get; set; }

    public bool ThrowOnRevert { get; set; }

    /// <summary>Si no es nula, absorbe comandos falsos con la misma clave.</summary>
    public string? MergeKey { get; set; }

    public List<FakeCommand> Merged { get; } = [];

    public int ApplyCount { get; private set; }

    public int RevertCount { get; private set; }

    public void Apply(Project project)
    {
        if (ThrowOnApply)
        {
            throw new InvalidOperationException($"falla {name}");
        }

        ApplyCount++;
        log?.Entries.Add($"apply {name}");
        project.Name += $"+{name}";
        foreach (FakeCommand m in Merged)
        {
            m.Apply(project);
        }
    }

    public void Revert(Project project)
    {
        if (ThrowOnRevert)
        {
            throw new InvalidOperationException($"falla revertir {name}");
        }

        for (int i = Merged.Count - 1; i >= 0; i--)
        {
            Merged[i].Revert(project);
        }

        RevertCount++;
        log?.Entries.Add($"revert {name}");
        string suffix = $"+{name}";
        Assert.EndsWith(suffix, project.Name, StringComparison.Ordinal);
        project.Name = project.Name[..^suffix.Length];
    }

    public bool TryMerge(ICommand following)
    {
        if (MergeKey is not null && following is FakeCommand f && f.MergeKey == MergeKey)
        {
            Merged.Add(f);
            return true;
        }

        return false;
    }
}
