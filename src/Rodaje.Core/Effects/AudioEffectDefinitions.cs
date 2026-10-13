using System.Collections.Immutable;
using Rodaje.Core.Model;
using D = Rodaje.Core.Model.BuiltInEffects.DelayParameters;
using G = Rodaje.Core.Model.BuiltInEffects.GateParameters;
using L = Rodaje.Core.Model.BuiltInEffects.LimiterParameters;
using P = Rodaje.Core.Model.BuiltInEffects.PitchShiftParameters;
using R = Rodaje.Core.Model.BuiltInEffects.ReverbParameters;
using S = Rodaje.Core.Model.BuiltInEffects.SaturationParameters;
using X = Rodaje.Core.Model.BuiltInEffects.DeEsserParameters;

namespace Rodaje.Core.Effects;

/// <summary>Las definiciones de los efectos de inserción del mezclador (códigos 40 a 47). Sus parámetros no se animan.</summary>
internal static class AudioEffectDefinitions
{
    public static ImmutableArray<EffectDefinition> Inserts { get; } =
    [
        new(BuiltInEffects.Gate, 40, EffectTarget.Audio, EffectStage.Audio,
        [
            new(G.Threshold, -45, -90, 0, Animatable: false),
            new(G.Range, 40, 0, 90, Animatable: false),
            new(G.Ratio, 20, 1, 20, Animatable: false),
            new(G.Attack, 1, 0.05, 100, Animatable: false),
            new(G.Hold, 40, 0, 1000, Animatable: false),
            new(G.Release, 120, 5, 3000, Animatable: false),
            new(G.Hysteresis, 4, 0, 20, Animatable: false),
            new(G.SidechainHighPass, 0, 0, 1000, Animatable: false),
        ]),
        new(BuiltInEffects.DeEsser, 41, EffectTarget.Audio, EffectStage.Audio,
        [
            new(X.Frequency, 6000, 2000, 14000, Animatable: false),
            new(X.Threshold, -30, -60, 0, Animatable: false),
            new(X.Range, 8, 0, 24, Animatable: false),
            new(X.SplitBand, 1, 0, 1, Animatable: false),
        ]),
        new(BuiltInEffects.Reverb, 42, EffectTarget.Audio, EffectStage.Audio,
        [
            new(R.PreDelay, 20, 0, 200, Animatable: false),
            new(R.Size, 50, 0, 100, Animatable: false),
            new(R.Decay, 1.6, 0.1, 20, Animatable: false),
            new(R.Damping, 7000, 500, 20000, Animatable: false),
            new(R.Width, 100, 0, 100, Animatable: false),
            new(R.LowCut, 120, 20, 1000, Animatable: false),
            new(R.Mix, 25, 0, 100, Animatable: false),
        ]),
        new(BuiltInEffects.Delay, 43, EffectTarget.Audio, EffectStage.Audio,
        [
            new(D.Time, 350, 1, 4000, Animatable: false),
            new(D.Sync, 0, 0, 1, Animatable: false),
            new(D.Tempo, 120, 20, 300, Animatable: false),
            new(D.Division, 7, 0, 9, Animatable: false),
            new(D.Feedback, 35, 0, 98, Animatable: false),
            new(D.PingPong, 0, 0, 1, Animatable: false),
            new(D.LowCut, 120, 20, 2000, Animatable: false),
            new(D.HighCut, 7000, 1000, 20000, Animatable: false),
            new(D.Mix, 25, 0, 100, Animatable: false),
        ]),
        new(BuiltInEffects.Saturation, 44, EffectTarget.Audio, EffectStage.Audio,
        [
            new(S.Drive, 6, 0, 36, Animatable: false),
            new(S.Character, 0, 0, 100, Animatable: false),
            new(S.Tone, 20000, 1000, 20000, Animatable: false),
            new(S.Output, 0, -24, 12, Animatable: false),
            new(S.Mix, 100, 0, 100, Animatable: false),
        ]),
        new(BuiltInEffects.GraphicEqualizer, 45, EffectTarget.Audio, EffectStage.Audio,
        [
            .. BuiltInEffects.GraphicEqualizerParameters.Bands.Select(b => new ParameterDefinition(b, 0, -12, 12, Animatable: false)),
            new(BuiltInEffects.GraphicEqualizerParameters.Output, 0, -12, 12, Animatable: false),
        ]),
        new(BuiltInEffects.PitchShift, 46, EffectTarget.Audio, EffectStage.Audio,
        [
            new(P.Semitones, 0, -12, 12, Animatable: false),
            new(P.Cents, 0, -100, 100, Animatable: false),
            new(P.Mix, 100, 0, 100, Animatable: false),
        ]),
        new(BuiltInEffects.Limiter, 47, EffectTarget.Audio, EffectStage.Audio,
        [
            new(L.Ceiling, -1, -24, 0, Animatable: false),
            new(L.InputGain, 0, 0, 24, Animatable: false),
            new(L.Release, 60, 5, 1000, Animatable: false),
        ]),
    ];
}
