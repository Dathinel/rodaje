using K = Rodaje.Core.Model.BuiltInEffects.CompressorParameters;
using Q = Rodaje.Core.Model.BuiltInEffects.EqualizerParameters;

namespace Rodaje.Core.Model;

/// <summary>
/// Un preajuste de audio: valores del compresor y, si los trae, del ecualizador. Sin ecualizador (<see cref="Equalizer"/>
/// nulo) el preajuste deja el que haya.
/// </summary>
public sealed record AudioPreset(
    string Id,
    string Name,
    string Description,
    IReadOnlyDictionary<string, double>? Equalizer,
    IReadOnlyDictionary<string, double>? Compressor);

/// <summary>Los preajustes de dinámica que trae la app; los de voz traen también su ecualizador.</summary>
public static class AudioPresets
{
    public static AudioPreset SocialVoice { get; } = new(
        "voz-redes",
        "Voz para redes",
        "Voz al frente y pareja para el móvil: quita el retumbe, da presencia, comprime firme y no pasa de −1 dBTP.",
        new Dictionary<string, double>
        {
            [Q.LowCut] = 90,
            [Q.MidGain] = -2.5,
            [Q.MidFrequency] = 300,
            [Q.MidQ] = 1,
            [Q.PresenceGain] = 3.5,
            [Q.PresenceFrequency] = 4000,
            [Q.PresenceQ] = 1,
            [Q.HighGain] = 2,
            [Q.HighFrequency] = 11000,
        },
        new Dictionary<string, double>
        {
            [K.Threshold] = -22,
            [K.Ratio] = 4,
            [K.Attack] = 4,
            [K.Release] = 90,
            [K.Knee] = 6,
            [K.Detector] = 1,
            [K.RmsWindow] = 5,
            [K.AutoRelease] = 1,
            [K.AutoMakeup] = 1,
            [K.Lookahead] = 2,
            [K.SidechainHighPass] = 100,
            [K.Limiter] = 1,
            [K.Ceiling] = -1,
        });

    public static AudioPreset Podcast { get; } = new(
        "podcast",
        "Podcast",
        "Voz cálida y cercana con compresión suave y constante: se entiende igual en auriculares y altavoces.",
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
            [K.Threshold] = -20,
            [K.Ratio] = 3,
            [K.Attack] = 8,
            [K.Release] = 140,
            [K.Knee] = 8,
            [K.Detector] = 1,
            [K.RmsWindow] = 15,
            [K.AutoRelease] = 1,
            [K.AutoMakeup] = 1,
            [K.SidechainHighPass] = 90,
            [K.Limiter] = 1,
            [K.Ceiling] = -1,
        });

    public static AudioPreset WarmNarration { get; } = new(
        "locucion-calida",
        "Locución cálida",
        "Cuerpo en los graves y agudos suaves, con una compresión lenta y en paralelo que no se nota: para documental o anuncio.",
        new Dictionary<string, double>
        {
            [Q.LowCut] = 60,
            [Q.LowGain] = 2.5,
            [Q.LowFrequency] = 150,
            [Q.MidGain] = -2,
            [Q.MidFrequency] = 450,
            [Q.MidQ] = 1,
            [Q.PresenceGain] = 1,
            [Q.PresenceFrequency] = 3000,
            [Q.PresenceQ] = 0.8,
            [Q.HighGain] = -1,
            [Q.HighFrequency] = 9000,
        },
        new Dictionary<string, double>
        {
            [K.Threshold] = -24,
            [K.Ratio] = 2.5,
            [K.Attack] = 15,
            [K.Release] = 200,
            [K.Knee] = 10,
            [K.Detector] = 1,
            [K.RmsWindow] = 20,
            [K.AutoMakeup] = 1,
            [K.Mix] = 85,
            [K.SidechainHighPass] = 80,
        });

    public static AudioPreset MusicGlue { get; } = new(
        "musica-pegamento",
        "Música: pegamento",
        "Une los instrumentos de una mezcla con 1 o 2 dB de reducción lenta; el pasa altos del detector evita que el bombo la haga bombear.",
        null,
        new Dictionary<string, double>
        {
            [K.Threshold] = -14,
            [K.Ratio] = 2,
            [K.Attack] = 30,
            [K.Release] = 300,
            [K.Knee] = 6,
            [K.Makeup] = 1.5,
            [K.AutoRelease] = 1,
            [K.SidechainHighPass] = 90,
        });

    public static AudioPreset PunchyDrums { get; } = new(
        "bateria-pegada",
        "Batería con pegada",
        "Compresión en paralelo: el ataque deja pasar el golpe y la mezcla al 45 % suma cuerpo sin aplastar los transitorios.",
        null,
        new Dictionary<string, double>
        {
            [K.Threshold] = -28,
            [K.Ratio] = 6,
            [K.Attack] = 10,
            [K.Release] = 80,
            [K.Knee] = 3,
            [K.Makeup] = 6,
            [K.Mix] = 45,
            [K.SidechainHighPass] = 60,
        });

    public static AudioPreset MixLimiter { get; } = new(
        "limitador-mezcla",
        "Limitador de mezcla",
        "∞:1 con 5 ms de anticipación y limitador de pico verdadero: sube la mezcla 2 dB sin pasar de −1 dBTP.",
        null,
        new Dictionary<string, double>
        {
            [K.Threshold] = -3,
            [K.Ratio] = 100,
            [K.Attack] = 0.5,
            [K.Release] = 50,
            [K.Knee] = 1,
            [K.Makeup] = 2,
            [K.AutoRelease] = 1,
            [K.Lookahead] = 5,
            [K.Limiter] = 1,
            [K.Ceiling] = -1,
        });

    public static AudioPreset MultibandVoice { get; } = new(
        "multibanda-voz",
        "Multibanda voz",
        "Tres bandas: frena el retumbe de los graves y las eses de los agudos sin apagar el centro de la voz.",
        new Dictionary<string, double>
        {
            [Q.LowCut] = 80,
        },
        new Dictionary<string, double>
        {
            [K.Threshold] = -24,
            [K.Ratio] = 3,
            [K.Attack] = 5,
            [K.Release] = 120,
            [K.Knee] = 6,
            [K.Detector] = 1,
            [K.RmsWindow] = 10,
            [K.AutoRelease] = 1,
            [K.AutoMakeup] = 1,
            [K.Bands] = 3,
            [K.LowCrossover] = 180,
            [K.HighCrossover] = 5000,
            [K.LowThreshold] = -3,
            [K.HighThreshold] = -6,
            [K.MidGain] = 1,
            [K.Limiter] = 1,
            [K.Ceiling] = -1,
        });

    public static IReadOnlyList<AudioPreset> All { get; } =
        [SocialVoice, Podcast, WarmNarration, MusicGlue, PunchyDrums, MixLimiter, MultibandVoice];
}
