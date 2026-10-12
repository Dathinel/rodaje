using K = Rodaje.Core.Model.BuiltInEffects.CompressorParameters;
using Q = Rodaje.Core.Model.BuiltInEffects.EqualizerParameters;

namespace Rodaje.Core.Model;

/// <summary>Un preajuste de audio: valores del ecualizador y del compresor (nulos, sin ese efecto).</summary>
public sealed record AudioPreset(
    string Id,
    string Name,
    string Description,
    IReadOnlyDictionary<string, double>? Equalizer,
    IReadOnlyDictionary<string, double>? Compressor);

/// <summary>Los preajustes de ecualizador y compresor que trae la app.</summary>
public static class AudioPresets
{
    public static AudioPreset ClearVoice { get; } = new(
        "voz-clara",
        "Voz clara",
        "Quita el retumbe y el barro de los graves, da presencia y empareja el volumen de la voz.",
        new Dictionary<string, double>
        {
            [Q.LowCut] = 80,
            [Q.MidGain] = -3,
            [Q.MidFrequency] = 300,
            [Q.MidQ] = 1,
            [Q.PresenceGain] = 3,
            [Q.PresenceFrequency] = 3000,
            [Q.PresenceQ] = 1,
            [Q.HighGain] = 2,
            [Q.HighFrequency] = 10000,
        },
        new Dictionary<string, double>
        {
            [K.Threshold] = -20,
            [K.Ratio] = 3,
            [K.Attack] = 10,
            [K.Release] = 120,
            [K.Makeup] = 4,
            [K.Knee] = 6,
        });

    public static AudioPreset Podcast { get; } = new(
        "podcast",
        "Podcast",
        "Voz cálida y cercana con compresión firme para que se entienda igual en auriculares y altavoces.",
        new Dictionary<string, double>
        {
            [Q.LowCut] = 70,
            [Q.LowGain] = 2,
            [Q.LowFrequency] = 120,
            [Q.MidGain] = -3,
            [Q.MidFrequency] = 400,
            [Q.MidQ] = 1.2,
            [Q.PresenceGain] = 2,
            [Q.PresenceFrequency] = 4000,
            [Q.PresenceQ] = 1,
            [Q.HighGain] = 1,
            [Q.HighFrequency] = 12000,
        },
        new Dictionary<string, double>
        {
            [K.Threshold] = -24,
            [K.Ratio] = 4,
            [K.Attack] = 5,
            [K.Release] = 150,
            [K.Makeup] = 6,
            [K.Knee] = 6,
        });

    public static AudioPreset SoftMusic { get; } = new(
        "musica-suave",
        "Música suave",
        "Un poco más de cuerpo y aire, con una compresión lenta que no se nota: para música de fondo.",
        new Dictionary<string, double>
        {
            [Q.LowGain] = 1.5,
            [Q.LowFrequency] = 80,
            [Q.PresenceGain] = -1,
            [Q.PresenceFrequency] = 3000,
            [Q.PresenceQ] = 0.8,
            [Q.HighGain] = 1,
            [Q.HighFrequency] = 12000,
        },
        new Dictionary<string, double>
        {
            [K.Threshold] = -16,
            [K.Ratio] = 2,
            [K.Attack] = 25,
            [K.Release] = 300,
            [K.Makeup] = 2,
            [K.Knee] = 10,
        });

    public static IReadOnlyList<AudioPreset> All { get; } = [ClearVoice, Podcast, SoftMusic];
}
