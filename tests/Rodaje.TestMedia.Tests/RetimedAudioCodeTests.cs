namespace Rodaje.TestMedia.Tests;

public class RetimedAudioCodeTests
{
    private const int Rate = 48000;

    /// <summary>El código de audio a <paramref name="speed"/>× sin mantener el tono: interpolación lineal sobre una versión 8 veces más fina.</summary>
    private static float[] Resampled(double seconds, double speed)
    {
        var output = new float[(int)(seconds * Rate)];
        const int fine = Rate * 8;
        for (int i = 0; i < output.Length; i++)
        {
            double position = i * speed * 8;
            long left = (long)Math.Floor(position);
            double t = position - left;
            output[i] = (float)(((1 - t) * AudioCode.SampleAt(left, fine)) + (t * AudioCode.SampleAt(left + 1, fine)));
        }

        return output;
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(0.25)]
    public void Sin_mantener_el_tono_cada_marca_cae_donde_la_pone_la_velocidad(double speed)
    {
        float[] audio = Resampled(6 / speed, speed);
        float[] template = RetimedAudioCode.ChirpTemplate(Rate, speed, preservePitch: false);
        double period = Rate / speed;

        for (int s = 1; s < 5; s++)
        {
            int expected = (int)Math.Round(s * period);
            AudioMark? found = RetimedAudioCode.FindChirp(audio, template, expected - (Rate / 20), expected + (Rate / 20));

            Assert.NotNull(found);
            Assert.InRange(found.Value.Sample, expected - 1, expected + 1);
            Assert.True(found.Value.Correlation > 0.95, $"Correlación {found.Value.Correlation} en el segundo {s}.");
        }
    }

    [Fact]
    public void Con_el_tono_mantenido_la_plantilla_es_el_chirp_original()
    {
        float[] audio = new float[3 * Rate];
        AudioCode.Generate(audio, 0, Rate, 1);
        float[] template = RetimedAudioCode.ChirpTemplate(Rate, 0.5, preservePitch: true);

        Assert.Equal(Rate / 50, template.Length);
        AudioMark? found = RetimedAudioCode.FindChirp(audio, template, Rate - 2400, Rate + 2400);
        Assert.Equal(Rate, found?.Sample);
        Assert.True(found!.Value.Correlation > 0.99);
    }

    [Fact]
    public void Sin_chirp_en_la_ventana_no_hay_marca()
    {
        float[] audio = new float[2 * Rate];
        AudioCode.Generate(audio, 0, Rate, 1);
        float[] template = RetimedAudioCode.ChirpTemplate(Rate, 1, preservePitch: true);

        Assert.Null(RetimedAudioCode.FindChirp(audio, template, Rate / 10, Rate * 9 / 10));
    }

    [Fact]
    public void Downmix_promedia_los_canales()
    {
        float[] stereo = [1, 0, 0.5f, 0.5f, -1, 1];
        var mono = new float[3];

        RetimedAudioCode.Downmix(stereo, 2, mono);

        Assert.Equal([0.5f, 0.5f, 0f], mono);
    }

    [Fact]
    public void El_medio_de_velocidad_cubre_diez_minutos_a_uno_y_medio()
    {
        MediaSpec spec = SpeedMediaPresets.Speed720p16Min;

        Assert.True(spec.Duration >= TimeSpan.FromMinutes(15));
        Assert.True(spec.FrameCount <= FrameCode.MaxIndex);
        Assert.Equal(AudioCodec.Aac, spec.Audio);
    }
}
