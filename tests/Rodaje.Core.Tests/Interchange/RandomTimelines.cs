using Rodaje.Core.Interchange;
using Rodaje.Core.Model;
using Rational = Rodaje.Core.Timing.Rational;
using Time = Rodaje.Core.Timing.Time;
using ClipTimeMap = Rodaje.Core.Timing.ClipTimeMap;

namespace Rodaje.Core.Tests.Interchange;

/// <summary>Qué puede salir en una secuencia generada.</summary>
public sealed record RandomTimelineOptions
{
    public int VideoTracks { get; init; } = 2;
    public int AudioTracks { get; init; } = 2;
    public int ClipsPerTrack { get; init; } = 12;
    public int Media { get; init; } = 6;
    public bool Gaps { get; init; } = true;
    public bool Crossfades { get; init; } = true;
    public bool Speeds { get; init; } = true;
    public bool Compounds { get; init; } = true;
    public bool Markers { get; init; } = true;
    public bool Titles { get; init; }
    public bool Effects { get; init; }

    /// <summary>La secuencia de las pruebas de rendimiento: 10 000 clips en 8 pistas y 400 medios.</summary>
    public static RandomTimelineOptions Large { get; } = new() { VideoTracks = 4, AudioTracks = 4, ClipsPerTrack = 1250, Media = 400, Compounds = false };
}

/// <summary>
/// Secuencias válidas al azar con semilla fija, para las pruebas de ida y vuelta por propiedad del intercambio (D-062).
/// Todo cae en cuadros enteros de la secuencia, así sirve también para la EDL.
/// </summary>
public static class RandomTimelines
{
    public static readonly Rational[] Rates = [new(25, 1), new(24, 1), Rational.Ntsc30, Rational.Ntsc24, new(50, 1)];

    public static TimelineDraft Create(int seed, RandomTimelineOptions? options = null)
    {
        options ??= new RandomTimelineOptions();
        var random = new Random(seed);
        Rational rate = Rates[random.Next(Rates.Length)];
        Time Frames(long n) => Time.FromFrames(n, rate);
        var project = new Project { Id = "p", Name = $"Semilla {seed}" };
        for (int i = 0; i < options.Media; i++)
        {
            project.Media.Add(new MediaRef
            {
                Id = $"m{i}",
                Name = $"toma{i:000}",
                Path = $@"C:\medios\dia {i % 3}\toma{i:000}.mov",
                Duration = Frames(3000 + random.Next(3000)),
                HasVideo = true,
                HasAudio = true,
                FrameRate = rate,
                Width = 1920,
                Height = 1080,
                SampleRate = 48000,
                AudioChannels = 2,
            });
        }

        var sequence = new Sequence { Id = "principal", Name = $"Montaje {seed}", FrameRate = rate };
        project.Sequences.Add(sequence);
        int clipNumber = 0;
        Sequence? compound = null;
        if (options.Compounds)
        {
            compound = new Sequence { Id = "compuesto", Name = "Compuesto", FrameRate = rate };
            var inner = new Track { Id = "cv1", Kind = TrackKind.Video, Name = "V1" };
            compound.VideoTracks.Add(inner);
            compound.AudioTracks.Add(new Track { Id = "ca1", Kind = TrackKind.Audio, Name = "A1" });
            long at = 0;
            for (int i = 0; i < 3; i++)
            {
                long length = 25 + random.Next(100);
                inner.Clips.Add(new Clip { Id = $"cc{i}", MediaId = project.Media[i % project.Media.Count].Id, Start = Frames(at), SourceIn = Frames(10 * i), SourceOut = Frames(10 * i + length) });
                at += length;
            }

            project.Sequences.Add(compound);
        }

        foreach ((TrackKind kind, int count) in new[] { (TrackKind.Video, options.VideoTracks), (TrackKind.Audio, options.AudioTracks) })
        {
            for (int t = 0; t < count; t++)
            {
                var track = new Track { Id = $"{kind}{t}", Kind = kind, Name = kind == TrackKind.Video ? $"V{t + 1}" : $"A{t + 1}" };
                long cursor = 0;
                Clip? previous = null;
                for (int c = 0; c < options.ClipsPerTrack; c++)
                {
                    if (options.Gaps && random.Next(4) == 0)
                    {
                        cursor += 1 + random.Next(50);
                        previous = null;
                    }

                    Clip clip = NewClip(random, project, kind, compound, options, Frames, ref clipNumber);
                    clip.Start = Frames(cursor);
                    track.Clips.Add(clip);
                    long length = clip.Duration.ToFrames(rate);
                    if (options.Crossfades && previous is not null && previous.Speed is null && clip.Speed is null && random.Next(3) == 0)
                    {
                        long room = Math.Min(previous.Duration.ToFrames(rate), length) / 2;
                        if (room >= 2)
                        {
                            track.Transitions.Add(new TransitionInstance
                            {
                                Id = $"t{clipNumber}",
                                Type = BuiltInTransitions.Crossfade,
                                From = previous.Id,
                                To = clip.Id,
                                Duration = Frames(2 * (1 + random.Next((int)room / 2))),
                            });
                        }
                    }

                    cursor += length;
                    previous = clip;
                }

                (kind == TrackKind.Video ? sequence.VideoTracks : sequence.AudioTracks).Add(track);
            }
        }

        if (options.Markers)
        {
            long end = Math.Max(1, sequence.Duration.ToFrames(rate));
            for (int i = 0; i < 3; i++)
            {
                sequence.Markers.Add(new Marker { Id = $"mk{i}", Time = Frames(random.NextInt64(end)), Name = $"Marca {i}" });
            }

            sequence.Markers.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        IReadOnlyList<string> errors = ModelValidator.Validate(project);
        return errors.Count == 0
            ? new TimelineDraft(project, sequence)
            : throw new InvalidOperationException($"La semilla {seed} dio una secuencia inválida: {string.Join(" ", errors.Take(3))}");
    }

    private static Clip NewClip(Random random, Project project, TrackKind kind, Sequence? compound, RandomTimelineOptions options, Func<long, Time> frames, ref int number)
    {
        string id = $"c{number++}";
        if (compound is not null && kind == TrackKind.Video && random.Next(10) == 0)
        {
            long length = compound.Duration.ToFrames(compound.FrameRate);
            long trimmed = Math.Max(1, length - random.Next((int)Math.Min(length - 1, 20) + 1));
            return new Clip { Id = id, SequenceId = compound.Id, SourceIn = Time.Zero, SourceOut = frames(trimmed) };
        }

        if (options.Titles && kind == TrackKind.Video && random.Next(12) == 0)
        {
            long length = 25 + random.Next(75);
            return new Clip { Id = id, Generator = new ClipGenerator { Text = $"Título {number}", Style = new TextStyle() }, SourceIn = Time.Zero, SourceOut = frames(length) };
        }

        MediaRef media = project.Media[random.Next(project.Media.Count)];
        long mediaFrames = media.Duration.ToFrames(media.FrameRate!.Value);
        long duration = 10 + random.Next(150);
        long sourceIn = random.Next((int)(mediaFrames / 2));
        var clip = new Clip { Id = id, MediaId = media.Id, SourceIn = frames(sourceIn), SourceOut = frames(sourceIn + duration) };
        if (options.Speeds && random.Next(6) == 0)
        {
            int kindOfSpeed = random.Next(kind == TrackKind.Video ? 4 : 3);
            clip.Speed = kindOfSpeed switch
            {
                0 => new ClipSpeed { Rate = new Rational(2, 1) },
                1 => new ClipSpeed { Rate = new Rational(1, 2) },
                2 => new ClipSpeed { Reverse = true },
                _ => new ClipSpeed { Freeze = true },
            };
            clip.Duration = frames(duration);
            ClipTimeMap map = ClipTimeMap.Create(Time.Zero, clip.Duration, clip.Speed.Reverse ? frames(sourceIn + duration) : clip.SourceIn, clip.Speed);
            clip.SourceIn = map.SourceIn;
            clip.SourceOut = map.SourceOut;
        }

        if (options.Effects && random.Next(5) == 0)
        {
            clip.Color = "#3366CC";
        }

        return clip;
    }
}
