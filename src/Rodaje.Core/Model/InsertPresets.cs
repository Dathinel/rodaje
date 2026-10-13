using D = Rodaje.Core.Model.BuiltInEffects.DelayParameters;
using G = Rodaje.Core.Model.BuiltInEffects.GateParameters;
using L = Rodaje.Core.Model.BuiltInEffects.LimiterParameters;
using P = Rodaje.Core.Model.BuiltInEffects.PitchShiftParameters;
using Q = Rodaje.Core.Model.BuiltInEffects.EqualizerParameters;
using R = Rodaje.Core.Model.BuiltInEffects.ReverbParameters;
using S = Rodaje.Core.Model.BuiltInEffects.SaturationParameters;
using X = Rodaje.Core.Model.BuiltInEffects.DeEsserParameters;

namespace Rodaje.Core.Model;

/// <summary>Un preajuste de una inserción: los valores de sus parámetros (lo que falta vale lo de fábrica).</summary>
public sealed record InsertPreset(string Id, string Name, string Description, IReadOnlyDictionary<string, double> Values);

/// <summary>Los preajustes de fábrica de cada efecto de inserción del mezclador.</summary>
public static class InsertPresets
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<InsertPreset>> ByType = new Dictionary<string, IReadOnlyList<InsertPreset>>
    {
        [BuiltInEffects.Equalizer] =
        [
            new("eq-claridad", "Voz: claridad", "Quita el retumbe y el barro y da presencia.",
                new Dictionary<string, double> { [Q.LowCut] = 90, [Q.MidGain] = -3, [Q.MidFrequency] = 300, [Q.MidQ] = 1.2, [Q.PresenceGain] = 3, [Q.PresenceFrequency] = 4000, [Q.HighGain] = 1.5, [Q.HighFrequency] = 10000 }),
            new("eq-retumbe", "Quitar retumbe", "Corte de graves a 120 Hz: fuera ruido de mesa, viento y aire acondicionado.",
                new Dictionary<string, double> { [Q.LowCut] = 120 }),
            new("eq-brillo", "Música: brillo y cuerpo", "Graves y agudos con estantes suaves.",
                new Dictionary<string, double> { [Q.LowGain] = 2, [Q.LowFrequency] = 100, [Q.HighGain] = 2.5, [Q.HighFrequency] = 9000 }),
            new("eq-hueco", "Hueco para la voz", "Baja los medios de la música donde vive la voz.",
                new Dictionary<string, double> { [Q.PresenceGain] = -4, [Q.PresenceFrequency] = 2500, [Q.PresenceQ] = 0.8 }),
        ],
        [BuiltInEffects.GraphicEqualizer] =
        [
            new("geq-plano", "Plano", "Todas las bandas en 0 dB.", new Dictionary<string, double>()),
            new("geq-presencia", "Más presencia", "Sube 2 y 4 kHz para que la voz se entienda.",
                new Dictionary<string, double> { ["band250"] = -2, ["band2k"] = 2, ["band4k"] = 3, ["band8k"] = 1 }),
            new("geq-telefono", "Teléfono", "Solo los medios, como una llamada.",
                new Dictionary<string, double> { ["band31"] = -12, ["band63"] = -12, ["band125"] = -12, ["band250"] = -6, ["band1k"] = 4, ["band2k"] = 3, ["band4k"] = -6, ["band8k"] = -12, ["band16k"] = -12 }),
            new("geq-sonrisa", "Sonrisa", "Graves y agudos arriba, medios abajo.",
                new Dictionary<string, double> { ["band31"] = 4, ["band63"] = 3, ["band125"] = 1, ["band500"] = -2, ["band1k"] = -2, ["band4k"] = 1, ["band8k"] = 3, ["band16k"] = 4 }),
        ],
        [BuiltInEffects.Compressor] =
        [
            .. AudioPresets.All.Where(p => p.Compressor is not null).Select(p => new InsertPreset(p.Id, p.Name, p.Description, p.Compressor!)),
        ],
        [BuiltInEffects.Gate] =
        [
            new("puerta-voz", "Voz en sala ruidosa", "Calla el fondo entre frases sin cortar los finales.",
                new Dictionary<string, double> { [G.Threshold] = -45, [G.Range] = 20, [G.Ratio] = 4, [G.Attack] = 2, [G.Hold] = 80, [G.Release] = 200, [G.SidechainHighPass] = 100 }),
            new("puerta-firme", "Puerta firme", "Silencio total entre golpes (batería, efectos).",
                new Dictionary<string, double> { [G.Threshold] = -30, [G.Range] = 80, [G.Ratio] = 20, [G.Attack] = 0.5, [G.Hold] = 30, [G.Release] = 80 }),
            new("puerta-suave", "Expansor suave", "Baja un poco el ruido sin que se note la puerta.",
                new Dictionary<string, double> { [G.Threshold] = -50, [G.Range] = 12, [G.Ratio] = 2, [G.Attack] = 5, [G.Hold] = 50, [G.Release] = 300 }),
        ],
        [BuiltInEffects.DeEsser] =
        [
            new("eses-femenina", "Voz aguda", "Eses alrededor de 7 kHz.", new Dictionary<string, double> { [X.Frequency] = 7000, [X.Threshold] = -32, [X.Range] = 8 }),
            new("eses-masculina", "Voz grave", "Eses alrededor de 5,5 kHz.", new Dictionary<string, double> { [X.Frequency] = 5500, [X.Threshold] = -30, [X.Range] = 8 }),
            new("eses-suave", "Suave", "Solo las eses más fuertes.", new Dictionary<string, double> { [X.Frequency] = 6000, [X.Threshold] = -24, [X.Range] = 5 }),
        ],
        [BuiltInEffects.Saturation] =
        [
            new("sat-cinta", "Cinta cálida", "Armónicos impares suaves, redondea los picos.",
                new Dictionary<string, double> { [S.Drive] = 8, [S.Character] = 0, [S.Tone] = 16000 }),
            new("sat-valvula", "Válvula", "Armónicos pares: más cuerpo y brillo.",
                new Dictionary<string, double> { [S.Drive] = 10, [S.Character] = 80, [S.Tone] = 14000 }),
            new("sat-radio", "Radio vieja", "Muy saturada y oscura.",
                new Dictionary<string, double> { [S.Drive] = 24, [S.Character] = 40, [S.Tone] = 4000, [S.Output] = -3 }),
            new("sat-paralela", "Paralela", "Densidad sin perder los ataques (50 % de mezcla).",
                new Dictionary<string, double> { [S.Drive] = 18, [S.Character] = 20, [S.Mix] = 50 }),
        ],
        [BuiltInEffects.Delay] =
        [
            new("delay-puntillo", "Corchea con puntillo", "El eco clásico al tempo.",
                new Dictionary<string, double> { [D.Sync] = 1, [D.Division] = 7, [D.Feedback] = 35, [D.Mix] = 25 }),
            new("delay-slap", "Slapback", "Un solo rebote corto, de los 50.",
                new Dictionary<string, double> { [D.Time] = 110, [D.Feedback] = 0, [D.Mix] = 30, [D.HighCut] = 6000 }),
            new("delay-pingpong", "Ping-pong", "Negras que saltan de un lado al otro.",
                new Dictionary<string, double> { [D.Sync] = 1, [D.Division] = 2, [D.Feedback] = 45, [D.PingPong] = 1, [D.Mix] = 25 }),
            new("delay-envio", "Envío (solo eco)", "Para un bus de envío: sale solo el eco.",
                new Dictionary<string, double> { [D.Sync] = 1, [D.Division] = 3, [D.Feedback] = 40, [D.Mix] = 100 }),
        ],
        [BuiltInEffects.Reverb] =
        [
            new("rev-sala", "Sala pequeña", "Un cuarto con muebles: cerca y natural.",
                new Dictionary<string, double> { [R.PreDelay] = 8, [R.Size] = 25, [R.Decay] = 0.7, [R.Damping] = 6000, [R.Mix] = 20 }),
            new("rev-estudio", "Estudio", "Pegamento para una mezcla sin que se note.",
                new Dictionary<string, double> { [R.PreDelay] = 15, [R.Size] = 45, [R.Decay] = 1.2, [R.Damping] = 7000, [R.Mix] = 18 }),
            new("rev-hall", "Hall", "Un auditorio: cola larga y brillante.",
                new Dictionary<string, double> { [R.PreDelay] = 30, [R.Size] = 80, [R.Decay] = 2.6, [R.Damping] = 9000, [R.Mix] = 25 }),
            new("rev-catedral", "Catedral", "Enorme y oscura.",
                new Dictionary<string, double> { [R.PreDelay] = 60, [R.Size] = 100, [R.Decay] = 7, [R.Damping] = 4000, [R.Mix] = 30 }),
            new("rev-envio", "Envío de voz", "Para un bus de envío: sale solo la cola.",
                new Dictionary<string, double> { [R.PreDelay] = 25, [R.Size] = 55, [R.Decay] = 1.6, [R.Damping] = 6500, [R.LowCut] = 200, [R.Mix] = 100 }),
        ],
        [BuiltInEffects.PitchShift] =
        [
            new("tono-octava-abajo", "Una octava abajo", "−12 semitonos.", new Dictionary<string, double> { [P.Semitones] = -12 }),
            new("tono-octava-arriba", "Una octava arriba", "+12 semitonos.", new Dictionary<string, double> { [P.Semitones] = 12 }),
            new("tono-quinta", "Quinta en paralelo", "+7 semitonos mezclados al 50 %.", new Dictionary<string, double> { [P.Semitones] = 7, [P.Mix] = 50 }),
            new("tono-grave", "Voz más grave", "−3 semitonos.", new Dictionary<string, double> { [P.Semitones] = -3 }),
        ],
        [BuiltInEffects.Limiter] =
        [
            new("lim-1", "Techo −1 dBTP", "Para redes y streaming.", new Dictionary<string, double> { [L.Ceiling] = -1 }),
            new("lim-2", "Emisión −2 dBTP", "Con margen para el códec.", new Dictionary<string, double> { [L.Ceiling] = -2, [L.Release] = 100 }),
            new("lim-empuje", "Empuje +6 dB", "Más fuerte sin pasar de −1 dBTP.", new Dictionary<string, double> { [L.Ceiling] = -1, [L.InputGain] = 6, [L.Release] = 80 }),
        ],
    };

    /// <summary>Los preajustes de un tipo; vacío si no tiene.</summary>
    public static IReadOnlyList<InsertPreset> For(string type) => ByType.GetValueOrDefault(type) ?? [];

    /// <summary>El preajuste cuyos valores (y lo de fábrica en lo demás) coinciden con los del efecto; nulo si ninguno.</summary>
    public static InsertPreset? Match(EffectInstance effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (Effects.EffectRegistry.Find(effect.Type) is not { } definition)
        {
            return null;
        }

        foreach (InsertPreset preset in For(effect.Type))
        {
            bool same = true;
            foreach (Effects.ParameterDefinition parameter in definition.Parameters)
            {
                double expected = preset.Values.TryGetValue(parameter.Name, out double v) ? v : parameter.DefaultValue;
                double actual = effect.Parameters.TryGetValue(parameter.Name, out Parameter? p) && p.Keyframes.Count == 0 ? p.Value
                    : effect.Parameters.ContainsKey(parameter.Name) ? double.NaN : parameter.DefaultValue;
                same &= Math.Abs(expected - actual) < 1e-9;
            }

            if (same)
            {
                return preset;
            }
        }

        return null;
    }
}
