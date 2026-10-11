using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>Proyectos de ejemplo y comparación profunda para las pruebas de serialización.</summary>
internal static class SerializationSamples
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Proyecto que usa todo el modelo, incluidas imagen fija, tasas NTSC, marcas de pista y tildes.</summary>
    public static Project Rich()
    {
        var position = new Parameter
        {
            Value = 0,
            Keyframes =
            [
                new Keyframe(S(0), 0, Interpolation.Hold),
                new Keyframe(S(1), 10.5, Interpolation.Linear),
                new Keyframe(S(2), -3.25, Interpolation.EaseIn),
                new Keyframe(S(3), 1e-7, Interpolation.EaseOut),
                new Keyframe(S(4), 123456.789, Interpolation.EaseInOut),
                new Keyframe(Time.FromTicks(4 * Time.TicksPerSecond + 1), 0.1 + 0.2),
            ],
        };

        return new Project
        {
            Id = "project-1",
            Name = "Vacaciones en Cartagena — edición ñ",
            Media =
            [
                new MediaRef
                {
                    Id = "media-video",
                    Path = "material/toma 1.mp4",
                    Name = "toma 1",
                    Duration = Time.FromFrames(1800, Rational.Ntsc30),
                    HasVideo = true,
                    HasAudio = true,
                    FrameRate = Rational.Ntsc30,
                    Width = 3840,
                    Height = 2160,
                    SampleRate = 48000,
                    AudioChannels = 2,
                },
                new MediaRef
                {
                    Id = "media-image",
                    Path = @"C:\Fotos\atardecer.png",
                    Name = "atardecer",
                    Duration = S(3600),
                    HasVideo = true,
                    FrameRate = null,
                    Width = 1080,
                    Height = 1350,
                },
                new MediaRef
                {
                    Id = "media-music",
                    Path = "música/canción ñ.wav",
                    Name = "canción",
                    Duration = Time.FromSamples(44100 * 90, 44100),
                    HasAudio = true,
                    SampleRate = 44100,
                    AudioChannels = 2,
                },
            ],
            Sequences =
            [
                new Sequence
                {
                    Id = "sequence-1",
                    Name = "Secuencia 1",
                    FrameRate = Rational.Ntsc30,
                    Width = 1920,
                    Height = 1080,
                    SampleRate = 48000,
                    VideoTracks =
                    [
                        new Track
                        {
                            Id = "track-v1",
                            Kind = TrackKind.Video,
                            Name = "Video 1",
                            Clips =
                            [
                                new Clip
                                {
                                    Id = "clip-1",
                                    MediaId = "media-video",
                                    SourceIn = Time.FromFrames(30, Rational.Ntsc30),
                                    SourceOut = Time.FromFrames(330, Rational.Ntsc30),
                                    Start = Time.Zero,
                                    LinkId = "link-1",
                                    Effects =
                                    [
                                        new EffectInstance
                                        {
                                            Id = "effect-1",
                                            Type = "rodaje.transform",
                                            Parameters = new()
                                            {
                                                ["positionX"] = position,
                                                ["opacity"] = new Parameter { Value = 0.75 },
                                            },
                                        },
                                        new EffectInstance
                                        {
                                            Id = "effect-2",
                                            Type = "rodaje.blur",
                                            Enabled = false,
                                            Options = new() { ["source"] = "rodaje-esenciales@1.0.0/suave", ["nota"] = "ñandú" },
                                        },
                                    ],
                                },
                                new Clip
                                {
                                    Id = "clip-2",
                                    MediaId = "media-image",
                                    SourceIn = Time.Zero,
                                    SourceOut = S(5),
                                    Start = S(20),
                                    Enabled = false,
                                },
                            ],
                        },
                        new Track { Id = "track-v2", Kind = TrackKind.Video, Name = "Títulos", Hidden = true, Locked = true },
                    ],
                    AudioTracks =
                    [
                        new Track
                        {
                            Id = "track-a1",
                            Kind = TrackKind.Audio,
                            Name = "Audio 1",
                            Muted = true,
                            Clips =
                            [
                                new Clip
                                {
                                    Id = "clip-3",
                                    MediaId = "media-video",
                                    SourceIn = Time.FromFrames(30, Rational.Ntsc30),
                                    SourceOut = Time.FromFrames(330, Rational.Ntsc30),
                                    Start = Time.Zero,
                                    LinkId = "link-1",
                                },
                            ],
                        },
                        new Track
                        {
                            Id = "track-a2",
                            Kind = TrackKind.Audio,
                            Name = "Música",
                            Effects =
                            [
                                new EffectInstance
                                {
                                    Id = "effect-3",
                                    Type = "rodaje.audio",
                                    Parameters = new() { ["gainDb"] = new Parameter { Value = -6, Keyframes = [new Keyframe(S(2), -6), new Keyframe(S(4), -60, Interpolation.EaseIn)] } },
                                },
                            ],
                            Clips =
                            [
                                new Clip
                                {
                                    Id = "clip-4",
                                    MediaId = "media-music",
                                    SourceIn = Time.FromSamples(1, 44100),
                                    SourceOut = S(60),
                                    Start = S(1.5),
                                },
                            ],
                        },
                    ],
                },
                new Sequence
                {
                    Id = "sequence-2",
                    Name = "Vertical",
                    FrameRate = Rational.Ntsc24,
                    Width = 1080,
                    Height = 1920,
                    SampleRate = 44100,
                },
            ],
        };
    }

    /// <summary>Compara dos proyectos campo por campo, sin pasar por el serializador.</summary>
    public static void AssertDeepEqual(Project expected, Project actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);

        Assert.Equal(expected.Media.Count, actual.Media.Count);
        for (int i = 0; i < expected.Media.Count; i++)
        {
            MediaRef e = expected.Media[i];
            MediaRef a = actual.Media[i];
            Assert.Equal(
                (e.Id, e.Path, e.Name, e.Duration, e.HasVideo, e.HasAudio, e.FrameRate, e.Width, e.Height, e.SampleRate, e.AudioChannels),
                (a.Id, a.Path, a.Name, a.Duration, a.HasVideo, a.HasAudio, a.FrameRate, a.Width, a.Height, a.SampleRate, a.AudioChannels));
        }

        Assert.Equal(expected.Sequences.Count, actual.Sequences.Count);
        for (int i = 0; i < expected.Sequences.Count; i++)
        {
            Sequence e = expected.Sequences[i];
            Sequence a = actual.Sequences[i];
            Assert.Equal(
                (e.Id, e.Name, e.FrameRate, e.Width, e.Height, e.SampleRate),
                (a.Id, a.Name, a.FrameRate, a.Width, a.Height, a.SampleRate));
            AssertTracksEqual(e.VideoTracks, a.VideoTracks);
            AssertTracksEqual(e.AudioTracks, a.AudioTracks);
        }
    }

    private static void AssertTracksEqual(List<Track> expected, List<Track> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Track e = expected[i];
            Track a = actual[i];
            Assert.Equal((e.Id, e.Kind, e.Name, e.Muted, e.Locked, e.Hidden), (a.Id, a.Kind, a.Name, a.Muted, a.Locked, a.Hidden));
            AssertEffectsEqual(e.Effects, a.Effects);
            Assert.Equal(e.Transitions.Count, a.Transitions.Count);
            for (int j = 0; j < e.Transitions.Count; j++)
            {
                TransitionInstance et = e.Transitions[j];
                TransitionInstance at = a.Transitions[j];
                Assert.Equal((et.Id, et.Type, et.From, et.To, et.Duration), (at.Id, at.Type, at.From, at.To, at.Duration));
                Assert.Equal(et.Parameters, at.Parameters);
                Assert.Equal(et.Options, at.Options);
            }

            Assert.Equal(e.Clips.Count, a.Clips.Count);
            for (int j = 0; j < e.Clips.Count; j++)
            {
                Clip ec = e.Clips[j];
                Clip ac = a.Clips[j];
                Assert.Equal(
                    (ec.Id, ec.MediaId, ec.SourceIn, ec.SourceOut, ec.Start, ec.LinkId, ec.Enabled),
                    (ac.Id, ac.MediaId, ac.SourceIn, ac.SourceOut, ac.Start, ac.LinkId, ac.Enabled));
                AssertEffectsEqual(ec.Effects, ac.Effects);
            }
        }
    }

    private static void AssertEffectsEqual(List<EffectInstance> expected, List<EffectInstance> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int k = 0; k < expected.Count; k++)
        {
            EffectInstance ee = expected[k];
            EffectInstance ae = actual[k];
            Assert.Equal((ee.Id, ee.Type, ee.Enabled), (ae.Id, ae.Type, ae.Enabled));
            Assert.Equal(ee.Options, ae.Options);
            Assert.Equal(ee.Parameters.Keys.Order(StringComparer.Ordinal), ae.Parameters.Keys.Order(StringComparer.Ordinal));
            foreach ((string name, Parameter ep) in ee.Parameters)
            {
                Parameter ap = ae.Parameters[name];
                Assert.Equal(ep.Value, ap.Value);
                Assert.Equal(ep.Keyframes, ap.Keyframes);
            }
        }
    }
}
