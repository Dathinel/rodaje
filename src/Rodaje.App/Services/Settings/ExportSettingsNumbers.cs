using Rodaje.Export;

namespace Rodaje.App.Services.Settings;

/// <summary>
/// Los números con decimales de <see cref="ExportSettings"/> que llegan de un archivo (<c>ajustes.json</c> o un
/// <c>.rodaje-preajuste</c>). JSON no tiene infinito, pero <c>1e400</c> sí es JSON y se lee como ±∞: el escritor lo
/// rechaza después con <see cref="ArgumentException"/> (tarea 267).
/// </summary>
internal static class ExportSettingsNumbers
{
    /// <summary>La ruta del primer número que no es finito («video.rateControl.quality»), o <c>null</c> si no hay.</summary>
    public static string? NonFinite(ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Video is { } video)
        {
            if (Bad(video.RateControl?.Quality))
            {
                return "video.rateControl.quality";
            }

            if (Bad(video.Gop?.KeyframeSeconds))
            {
                return "video.gop.keyframeSeconds";
            }
        }

        if (settings.Audio is { } audio)
        {
            if (Bad(audio.RateControl?.Quality))
            {
                return "audio.rateControl.quality";
            }

            if (audio.Loudness is { } loudness)
            {
                if (Bad(loudness.Lufs))
                {
                    return "audio.loudness.lufs";
                }

                if (Bad(loudness.MaxTruePeakDbtp))
                {
                    return "audio.loudness.maxTruePeakDbtp";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Los mismos ajustes con cada número no finito cambiado por el de fábrica: la calidad sin fijar, los fotogramas
    /// clave cada <see cref="GopSettings.KeyframeSeconds"/> de siempre, el pico de siempre y, sin objetivo, sin sonoridad.
    /// </summary>
    public static ExportSettings Finite(ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (NonFinite(settings) is null)
        {
            return settings;
        }

        ExportSettings result = settings;
        if (settings.Video is { } video)
        {
            result = result with
            {
                Video = video with
                {
                    RateControl = video.RateControl is { } rate && Bad(rate.Quality) ? rate with { Quality = null } : video.RateControl!,
                    Gop = video.Gop is { } gop && Bad(gop.KeyframeSeconds) ? gop with { KeyframeSeconds = new GopSettings().KeyframeSeconds } : video.Gop!,
                },
            };
        }

        if (settings.Audio is { } audio)
        {
            result = result with
            {
                Audio = audio with
                {
                    RateControl = audio.RateControl is { } rate && Bad(rate.Quality) ? rate with { Quality = null } : audio.RateControl!,
                    Loudness = audio.Loudness switch
                    {
                        { } l when Bad(l.Lufs) => null,
                        { } l when Bad(l.MaxTruePeakDbtp) => l with { MaxTruePeakDbtp = new LoudnessTarget(l.Lufs).MaxTruePeakDbtp },
                        var l => l,
                    },
                },
            };
        }

        return result;
    }

    private static bool Bad(double? value) => value is { } v && !double.IsFinite(v);
}
