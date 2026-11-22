using Rodaje.App.Views.Export;
using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Tests.Export;

/// <summary>Lo que hacen los manejadores de <c>ExportView</c> y lo que lee su refresco, sin WinUI.</summary>
internal static class DialogDriver
{
    private static readonly LoudnessTarget?[] Loudness = [null, LoudnessTarget.Platforms, new LoudnessTarget(-16), LoudnessTarget.EbuR128];

    /// <summary>Un cambio al azar entre los que ofrece el diálogo ahora mismo; devuelve qué hizo.</summary>
    public static string RandomChange(ExportDialogModel model, Random random)
    {
        ExportSettings s = model.Settings;
        T Pick<T>(IReadOnlyList<T> items) => items[random.Next(items.Count)];
        switch (random.Next(16))
        {
            case 0:
                string preset = Pick([.. model.Presets.Select(p => p.Id), ExportPresetLibrary.CustomId]);
                model.SelectPreset(preset);
                return "preajuste " + preset;
            case 1 when ExportChoices.Containers(model.Encoders) is { Count: > 0 } containers:
                ExportContainer container = Pick(containers);
                model.Update(x => x with { Container = container });
                return "contenedor " + container;
            case 2 when s.Video is { } video && ExportChoices.VideoCodecs(s.Container, model.Encoders) is { Count: > 0 } codecs:
                ExportCodec codec = Pick(codecs);
                if (codec != video.Codec)
                {
                    VideoExportSettings fresh = ExportSettingsEditor.DefaultVideo(codec, model.Encoders);
                    model.Update(x => x with { Video = fresh with { Width = video.Width, Height = video.Height, Fit = video.Fit, FrameRate = video.FrameRate, Crop = video.Crop } });
                }

                return "códec " + codec;
            case 3 when s.Video is { } video && ExportChoices.EncodersFor(video.Codec, model.Encoders) is { Count: > 0 } specs:
                string id = Pick(specs).Id;
                Video(model, v => v with { EncoderId = id });
                return "codificador " + id;
            case 4 when model.VideoEncoder is { } encoder && model.Settings.Video is { } video && ExportChoices.RateChoices(encoder) is { Count: > 0 } rates:
                RateChoice choice = Pick(rates);
                (int w, int h) = model.OutputSize;
                int kbps = ExportSettingsEditor.SuggestedVideoKbps(video.Codec, w, h, model.FrameRate.ToDouble());
                RateControl rate = choice switch
                {
                    RateChoice.Vbr => RateControl.Vbr(kbps, kbps * 3 / 2),
                    RateChoice.Cbr => RateControl.Cbr(kbps),
                    RateChoice.TargetSize => RateControl.TargetSize(Math.Max(1_000_000, model.Estimate.Bytes)),
                    RateChoice.Lossless => RateControl.Lossless,
                    RateChoice.ProfileDefined => RateControl.ProfileDefined,
                    _ => RateControl.ConstantQuality(),
                };
                Video(model, v => v with { RateControl = rate });
                return "tasa " + choice;
            case 5 when s.Video is not null:
                SizeChoice size = Pick(model.SizeChoices);
                Video(model, v => v with { Width = size.Width, Height = size.Height });
                return "tamaño " + size.Label;
            case 6 when s.Video is not null:
                ExportFit fit = Pick([ExportFit.Bars, ExportFit.Crop, ExportFit.Stretch]);
                Video(model, v => v with { Fit = fit });
                return "encaje " + fit;
            case 7 when s.Video is not null:
                FrameRateChoice fps = Pick(ExportGeometry.FrameRates);
                Video(model, v => v with { FrameRate = fps.Rate });
                return "fps " + fps.Label;
            case 8 when model.VideoEncoder is { } encoder:
                int bits = Pick(ExportChoices.BitDepths(encoder));
                Video(model, v => v with { BitDepth = bits });
                return $"{bits} bits";
            case 9 when model.VideoEncoder is { } encoder && model.Settings.Video is { } video && ExportChoices.Chromas(encoder, video.BitDepth) is { Count: > 0 } chromas:
                ChromaSubsampling chroma = Pick(chromas);
                Video(model, v => v with { Chroma = chroma });
                return "croma " + chroma;
            case 10 when model.VideoEncoder is { } encoder && model.Settings.Video is { } video:
                string?[] profiles = [null, .. ExportChoices.Profiles(encoder, encoder.PixelFormatFor(video.BitDepth, video.Chroma, video.Alpha)).Select(p => p.Id)];
                string? profile = Pick(profiles);
                Video(model, v => v with { Profile = profile });
                return "perfil " + profile;
            case 11 when model.VideoEncoder is { } encoder:
                string?[] speeds = [null, .. encoder.SpeedPresets];
                string? speed = Pick(speeds);
                Video(model, v => v with { SpeedPreset = speed });
                return "velocidad " + speed;
            case 12:
                bool on = random.Next(2) == 0;
                if (random.Next(2) == 0)
                {
                    model.SetVideoEnabled(on);
                    return "video " + on;
                }

                model.SetAudioEnabled(on);
                return "audio " + on;
            case 13 when model.AudioEncoder is { } audio && s.Audio is { } current:
                switch (random.Next(4))
                {
                    case 0 when ExportChoices.AudioCodecs(s.Container, model.Encoders) is { Count: > 0 } codecs:
                        ExportCodec codec2 = Pick(codecs);
                        if (codec2 != current.Codec)
                        {
                            AudioExportSettings fresh = ExportSettingsEditor.DefaultAudio(codec2, model.Encoders);
                            model.Update(x => x with { Audio = fresh with { SampleRate = current.SampleRate, Channels = current.Channels, Loudness = current.Loudness } });
                        }

                        return "audio " + codec2;
                    case 1 when ExportChoices.SampleRates(audio) is { Count: > 0 } hz:
                        int sampleRate = Pick(hz);
                        model.Update(x => x with { Audio = x.Audio! with { SampleRate = sampleRate } });
                        return $"{sampleRate} Hz";
                    case 2 when ExportChoices.Channels(audio) is { Count: > 0 } channels:
                        int count = Pick(channels);
                        model.Update(x => x with { Audio = x.Audio! with { Channels = count } });
                        return $"{count} canales";
                    default:
                        LoudnessTarget? target = Pick(Loudness);
                        model.Update(x => x with { Audio = x.Audio! with { Loudness = target } });
                        return "sonoridad " + target?.Lufs;
                }

            case 14:
                model.SetPerformance(random.Next(2) == 0 ? ExportPerformance.Fastest : ExportPerformance.Background);
                return "rendimiento";
            case 15:
                int range = random.Next(model.Ranges.Count);
                model.SetRange(range);
                return "rango " + range;
            default:
                return "nada";
        }
    }

    /// <summary>Lo que lee el refresco de la vista (<c>RefreshSimple</c>, <c>RefreshVideo</c>, <c>RefreshAudio</c>, <c>RefreshSide</c>).</summary>
    public static void ReadLikeTheView(ExportDialogModel model)
    {
        _ = model.PresetShapeNote;
        _ = model.HdrNote;
        _ = model.OffersLightMeasurement;
        _ = ExportGeometry.AspectName(model.SequenceWidth, model.SequenceHeight);
        _ = ExportChoices.Containers(model.Encoders);
        _ = ExportChoices.VideoCodecs(model.Settings.Container, model.Encoders);
        if (model.Settings.Video is { } video && model.VideoEncoder is { } encoder)
        {
            _ = ExportChoices.EncodersFor(video.Codec, model.Encoders).Select(ExportChoices.EncoderLabel).ToList();
            _ = ExportChoices.RateChoices(encoder);
            _ = model.SizeChoices;
            _ = model.FrameRateLabels;
            _ = model.ShapeDiffers;
            _ = ExportChoices.Chromas(encoder, video.BitDepth);
            ExportPixelFormat? pixels = encoder.PixelFormatFor(video.BitDepth, video.Chroma, video.Alpha);
            _ = ExportChoices.Profiles(encoder, pixels).Select(p => encoder.Profiles.First(x => x.Id == p.Id).Name).ToList();
            if (video.Profile is { } profile)
            {
                _ = encoder.Profiles.First(p => p.Id == profile);
            }
        }

        if (model.Settings.Audio is not null && model.AudioEncoder is { } audio)
        {
            _ = ExportChoices.SampleRates(audio);
            _ = ExportChoices.Channels(audio);
        }

        ExportEstimate estimate = model.Estimate;
        _ = estimate.Text;
        _ = model.Problems;
        _ = model.VersionProblems;
        _ = model.Adjustments;
    }

    private static void Video(ExportDialogModel model, Func<VideoExportSettings, VideoExportSettings> change) =>
        model.Update(s => s.Video is { } v ? s with { Video = change(v) } : s);
}
