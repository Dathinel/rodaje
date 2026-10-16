using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;
using Time = Rodaje.Core.Timing.Time;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete la sección «Volumen» de <c>docs/guia/audio.md</c> y depende del núcleo. Cada prueba cita la frase. El
/// arrastre del texto de la pista, el dibujo de la envolvente y el tope de los fundidos en la mitad del clip son de la
/// interfaz.
/// </summary>
public sealed class GuideAudioVolumeContractTests
{
    private static (TrimFixture Fixture, string EffectId) AudioClip()
    {
        var f = new TrimFixture().Add("a1", "c", "m3", 0, 50, 0);
        string effect = EffectEdits.AddEffect(f.Ctx, "c", BuiltInEffects.Audio);
        return (f, effect);
    }

    private static Parameter Gain(EffectInstance effect) => effect.Parameters[A.GainDb];

    // «“Volumen” de −60 (silencio) a +24 dB»: el rango del parámetro y el silencio en su mínimo.
    [Fact]
    public void El_volumen_va_de_menos_60_a_mas_24_dB_y_el_minimo_es_el_silencio()
    {
        ParameterDefinition gain = EffectRegistry.Find(BuiltInEffects.Audio)!.FindParameter(A.GainDb)!;

        Assert.Equal(-60, gain.Minimum);
        Assert.Equal(24, gain.Maximum);
        Assert.Equal(EffectRegistry.SilenceDb, gain.Minimum);
    }

    // «“Volumen” de −60 (silencio) a +24 dB»: lo que se pide fuera queda en el borde y los extremos se aceptan.
    [Theory]
    [InlineData(-60, -60)]
    [InlineData(24, 24)]
    [InlineData(-90, -60)]
    [InlineData(40, 24)]
    public void El_volumen_del_clip_queda_dentro_de_su_rango(double asked, double expected)
    {
        (TrimFixture f, string id) = AudioClip();

        EffectEdits.SetParameter(f.Ctx, "c", id, A.GainDb, asked, Time.Zero);

        f.AssertValid();
        Assert.Equal(expected, Gain(f.Clip("c").Effects.Single(e => e.Id == id)).Value);
    }

    // «“Volumen” […], con keyframes»
    [Fact]
    public void El_volumen_del_clip_admite_keyframes()
    {
        (TrimFixture f, string id) = AudioClip();

        EffectEdits.SetKeyframe(f.Ctx, "c", id, A.GainDb, F(0), -12);
        EffectEdits.SetKeyframe(f.Ctx, "c", id, A.GainDb, F(25), 6);

        f.AssertValid();
        Assert.True(EffectRegistry.Find(BuiltInEffects.Audio)!.FindParameter(A.GainDb)!.Animatable);
        Assert.Equal([-12.0, 6.0], Gain(f.Clip("c").Effects.Single(e => e.Id == id)).Keyframes.Select(k => k.Value));
    }

    // «“Fundidos”: “Entrada” y “Salida” en segundos»: de fábrica no hay fundido y no bajan de cero.
    [Theory]
    [InlineData(A.FadeIn)]
    [InlineData(A.FadeOut)]
    public void Los_fundidos_empiezan_en_cero_y_no_son_negativos(string name)
    {
        (TrimFixture f, string id) = AudioClip();

        EffectEdits.SetParameter(f.Ctx, "c", id, name, 0.5, Time.Zero);
        double half = f.Clip("c").Effects.Single(e => e.Id == id).Parameters[name].Value;
        EffectEdits.SetParameter(f.Ctx, "c", id, name, -1, Time.Zero);

        f.AssertValid();
        Assert.Equal(0, EffectRegistry.DefaultValue(BuiltInEffects.Audio, name));
        Assert.Equal(0.5, half);
        Assert.Equal(0, f.Clip("c").Effects.Single(e => e.Id == id).Parameters[name].Value);
    }

    // «El botón de la cabecera restablece volumen y fundidos.»
    [Fact]
    public void Restablecer_devuelve_volumen_y_fundidos_a_fabrica_y_quita_los_keyframes()
    {
        (TrimFixture f, string id) = AudioClip();
        EffectEdits.SetKeyframe(f.Ctx, "c", id, A.GainDb, F(0), -12);
        EffectEdits.SetKeyframe(f.Ctx, "c", id, A.GainDb, F(25), 6);
        EffectEdits.SetParameter(f.Ctx, "c", id, A.FadeIn, 0.4, Time.Zero);
        EffectEdits.SetParameter(f.Ctx, "c", id, A.FadeOut, 0.7, Time.Zero);
        string[] names = [A.GainDb, A.FadeIn, A.FadeOut];

        EffectEdits.ResetParameters(f.Ctx, "c", id, names.ToDictionary(n => n, n => EffectRegistry.DefaultValue(BuiltInEffects.Audio, n)));

        f.AssertValid();
        EffectInstance effect = f.Clip("c").Effects.Single(e => e.Id == id);
        foreach (string name in names)
        {
            Assert.Equal(0, effect.Parameters[name].Value);
            Assert.Empty(effect.Parameters[name].Keyframes);
        }
    }

    // «Por pista: […] “Volumen 0,0 dB” […]; doble clic vuelve a 0 dB.»: la pista empieza en 0 dB y su volumen se mueve
    // dentro del mismo rango que el del clip.
    [Fact]
    public void El_volumen_de_la_pista_empieza_en_0_dB_y_se_acota_al_rango()
    {
        var f = new TrimFixture().Add("a1", "c", "m3", 0, 50, 0);

        EffectEdits.ShiftGain(f.Ctx, "a1", 30);
        EffectInstance effect = f.Track("a1").Effects.Single(e => e.Type == BuiltInEffects.Audio);
        double top = Gain(effect).Value;
        EffectEdits.ShiftGain(f.Ctx, "a1", -100);
        double bottom = Gain(effect).Value;
        EffectEdits.SetParameter(f.Ctx, "a1", effect.Id, A.GainDb, 0, Time.Zero);

        f.AssertValid();
        Assert.Equal(0, EffectRegistry.DefaultValue(BuiltInEffects.Audio, A.GainDb));
        Assert.Equal(24, top);
        Assert.Equal(-60, bottom);
        Assert.Equal(0, Gain(effect).Value);
        Assert.Empty(f.Clip("c").Effects);
    }

    // «Silenciar una pista: el altavoz de su cabecera.»: silenciar es un estado de la pista, sin tocar su volumen ni sus clips.
    [Fact]
    public void Silenciar_la_pista_no_toca_su_volumen_ni_sus_clips()
    {
        var f = new TrimFixture().Add("a1", "c", "m3", 0, 50, 0);
        EffectEdits.ShiftGain(f.Ctx, "a1", -6);

        TimelineEdits.SetTrackState(f.Ctx, "a1", muted: true);

        f.AssertValid();
        Assert.True(f.Track("a1").Muted);
        Assert.False(f.Track("a2").Muted);
        Assert.Equal(-6, Gain(f.Track("a1").Effects.Single(e => e.Type == BuiltInEffects.Audio)).Value);
        Assert.Empty(f.Clip("c").Effects);

        TimelineEdits.SetTrackState(f.Ctx, "a1", muted: false);
        Assert.False(f.Track("a1").Muted);
    }
}
