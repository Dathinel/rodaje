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

    /// <summary>Clip de texto (formato 5) sin medio, con el estilo completo de <see cref="FullStyle"/>.</summary>
    public static Clip MakeTitle(string id, double start, double sourceIn, double sourceOut, string text = "Título") => new()
    {
        Id = id,
        Start = Sec(start),
        SourceIn = Sec(sourceIn),
        SourceOut = Sec(sourceOut),
        Generator = new ClipGenerator { Text = text, Style = FullStyle() },
    };

    /// <summary>Un estilo con todos los campos opcionales y animaciones de entrada y salida.</summary>
    public static TextStyle FullStyle() => new()
    {
        Source = "rodaje-titulos@1.0.0/clasico",
        Name = "Clásico",
        Font = new StyleFont { Family = "Inter", Weight = 700, Source = "rodaje-fuentes@1.0.0/inter", Fallback = ["Segoe UI", "Arial"] },
        Size = 96,
        Stroke = new StyleStroke { Color = "#000000", Width = 6 },
        Shadow = new StyleShadow { Color = "#00000099", Dx = 4, Dy = 6, Blur = 8 },
        Background = new StyleBackground { Color = "#00000080", Padding = [24, 12], Radius = 12 },
        In = new StyleAnimation
        {
            DurationSeconds = 0.4,
            Curves = { ["opacity"] = [new StyleKey(0, 0), new StyleKey(0.4, 1, Interpolation.EaseOut)], ["y"] = [new StyleKey(0, 40), new StyleKey(0.4, 0)] },
        },
        Out = new StyleAnimation { DurationSeconds = 0.3, Curves = { ["opacity"] = [new StyleKey(0, 1), new StyleKey(0.3, 0)] } },
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
