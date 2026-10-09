using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Model;

internal static class ModelSamples
{
    public static Time Sec(double seconds) => Time.FromSeconds(seconds);

    public static Clip MakeClip(string id, string mediaId, double start, double sourceIn, double sourceOut, string? linkId = null) => new()
    {
        Id = id,
        MediaId = mediaId,
        Start = Sec(start),
        SourceIn = Sec(sourceIn),
        SourceOut = Sec(sourceOut),
        LinkId = linkId,
    };

    /// <summary>Medios AV (60 s) y de audio (30 s), dos pistas de video y una de audio, enlaces y un efecto con keyframes.</summary>
    public static Project ValidProject()
    {
        var withEffect = MakeClip("c1", "m1", 0, 0, 10, "L1");
        withEffect.Effects.Add(new EffectInstance
        {
            Id = "fx1",
            Type = "rodaje.transform",
            Parameters =
            {
                ["opacity"] = new Parameter
                {
                    Value = 1,
                    Keyframes = [new Keyframe(Sec(0), 0), new Keyframe(Sec(1), 1, Interpolation.EaseOut)],
                },
                ["scale"] = new Parameter { Value = 1 },
            },
        });

        return new Project
        {
            Id = "p1",
            Name = "Proyecto de prueba",
            Media =
            [
                new MediaRef
                {
                    Id = "m1", Path = "toma 1.mp4", Name = "toma 1", Duration = Sec(60), HasVideo = true, HasAudio = true,
                    FrameRate = Rational.Fps30, Width = 1920, Height = 1080, SampleRate = 48000, AudioChannels = 2,
                },
                new MediaRef { Id = "m2", Path = "música.wav", Name = "música", Duration = Sec(30), HasAudio = true, SampleRate = 44100, AudioChannels = 2 },
            ],
            Sequences =
            [
                new Sequence
                {
                    Id = "s1",
                    Name = "Principal",
                    VideoTracks =
                    [
                        new Track { Id = "v1", Kind = TrackKind.Video, Name = "V1", Clips = [withEffect, MakeClip("c2", "m1", 12, 20, 25)] },
                        new Track { Id = "v2", Kind = TrackKind.Video, Name = "V2", Clips = [MakeClip("c3", "m1", 2, 30, 33)] },
                    ],
                    AudioTracks =
                    [
                        new Track
                        {
                            Id = "a1", Kind = TrackKind.Audio, Name = "A1",
                            Clips = [MakeClip("c4", "m1", 0, 0, 10, "L1"), MakeClip("c5", "m2", 10, 0, 30)],
                        },
                    ],
                },
            ],
        };
    }
}
