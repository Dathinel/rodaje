using Rodaje.App.Views.Export;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Tests.Export;

public class ExportEstimateTests
{
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    [Fact]
    public void ConTasaFija_ElTamanoSaleDeLaTasa()
    {
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.H264, RateControl = RateControl.Cbr(8_000) },
            Audio = new AudioExportSettings { Codec = ExportCodec.Aac, RateControl = new() { Mode = RateControlMode.Cbr, BitrateKbps = 192 } },
        };

        ExportEstimate estimate = ExportEstimator.Estimate(settings, SimulatedEncoders.H264Nvenc, 1920, 1080, 30, TenSeconds);

        Assert.False(estimate.Approximate);
        Assert.Equal((long)Math.Round((8_000 + 192) * 1000 / 8.0 * 10 * 1.01), estimate.Bytes);
    }

    [Fact]
    public void ConTamanoObjetivo_EsElObjetivo()
    {
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.Av1, RateControl = RateControl.TargetSize(25_000_000) },
        };

        Assert.Equal(25_000_000, ExportEstimator.Estimate(settings, SimulatedEncoders.SvtAv1, 1920, 1080, 30, TenSeconds).Bytes);
    }

    [Fact]
    public void ConCalidadConstante_EsAproximadoYMenorCrfPesaMas()
    {
        ExportSettings Crf(double q) => new()
        {
            Video = new VideoExportSettings { Codec = ExportCodec.Av1, RateControl = RateControl.ConstantQuality(q) },
        };

        ExportEstimate high = ExportEstimator.Estimate(Crf(23), SimulatedEncoders.SvtAv1, 1920, 1080, 30, TenSeconds);
        ExportEstimate low = ExportEstimator.Estimate(Crf(35), SimulatedEncoders.SvtAv1, 1920, 1080, 30, TenSeconds);

        Assert.True(high.Approximate);
        Assert.InRange(high.Bytes / (double)low.Bytes, 3.9, 4.1);
    }

    [Fact]
    public void ProResHq_UnosDoscientosVeinteMbps()
    {
        ExportEstimate estimate = ExportEstimator.Estimate(ExportPresets.ProResMaster.Settings with { Audio = null }, SimulatedEncoders.ProRes,
            1920, 1080, 30, TenSeconds);

        Assert.InRange(estimate.Bytes, 270_000_000, 285_000_000);
    }

    [Fact]
    public void PcmDe24Bits_EsExacto()
    {
        var audio = new AudioExportSettings { Codec = ExportCodec.Pcm, RateControl = RateControl.Lossless, PcmFormat = ExportSampleFormat.Pcm24 };

        Assert.Equal(48_000 * 2 * 3, ExportEstimator.AudioBytesPerSecond(audio));
    }

    [Fact]
    public void AltoRendimiento_TardaMenosQueBajo()
    {
        ExportSettings settings = ExportPresets.YouTube1080p.Settings;
        TimeSpan fast = ExportEstimator.Estimate(settings with { Performance = ExportPerformance.Fastest }, SimulatedEncoders.H264Nvenc,
            1920, 1080, 30, TimeSpan.FromMinutes(10)).Time;
        TimeSpan slow = ExportEstimator.Estimate(settings, SimulatedEncoders.H264Nvenc, 1920, 1080, 30, TimeSpan.FromMinutes(10)).Time;

        Assert.True(fast < slow);
        Assert.InRange(fast.TotalMinutes, 1, 3);
    }

    [Theory]
    [InlineData(850_000, "850 KB")]
    [InlineData(5_400_000, "5,4 MB")]
    [InlineData(245_000_000, "245 MB")]
    [InlineData(1_400_000_000, "1,4 GB")]
    public void LosTamanosSeEscribenEnEspanol(long bytes, string text) => Assert.Equal(text, ExportText.Bytes(bytes));

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(65, "1 min 05 s")]
    [InlineData(3725, "1 h 02 min")]
    public void LasDuracionesSeEscribenEnEspanol(int seconds, string text) => Assert.Equal(text, ExportText.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ElAvanceDicePorcentajeFpsYTiempoRestante()
    {
        var progress = new ExportProgress(ExportJobState.Encoding, 420, 1000, 238.4, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(65), 0);

        Assert.Equal("42 % · 238 fps · quedan 1 min 05 s", ExportText.Status(progress));
        Assert.Equal("Pasada 2 de 2 · 71 % · 238 fps · quedan 1 min 05 s", ExportText.Status(progress with { Pass = 2, PassCount = 2 }));
        Assert.Equal("Mezclando el audio", ExportText.Status(progress with { State = ExportJobState.MixingAudio }));
    }

    [Theory]
    [InlineData(1920, 1080, 1080, 1920)]
    [InlineData(3840, 2160, 2160, 3840)]
    [InlineData(1280, 720, 720, 1280)]
    public void LaSecuenciaVerticalConservaElLadoCorto(int w, int h, int vw, int vh) =>
        Assert.Equal((vw, vh), ExportGeometry.Vertical(w, h));

    [Fact]
    public void ConUnaSolaMedida_LaOtraSigueLaFormaYEsPar()
    {
        var video = new VideoExportSettings { Codec = ExportCodec.H264, Width = 1281 };

        Assert.Equal((1280, 720), ExportGeometry.OutputSize(video, 1920, 1080));
        Assert.Equal((1920, 1080), ExportGeometry.OutputSize(video with { Width = null }, 1920, 1080));
        Assert.Equal((481, 271), ExportGeometry.OutputSize(new VideoExportSettings { Codec = ExportCodec.Gif, Width = 481 }, 1920, 1080));
    }

    [Fact]
    public void LaFormaDistintaSeDetectaYSeNombra()
    {
        Assert.True(ExportGeometry.ShapeDiffers(1080, 1920, 1920, 1080));
        Assert.False(ExportGeometry.ShapeDiffers(1280, 720, 1920, 1080));
        Assert.Equal("9:16", ExportGeometry.AspectName(1080, 1920));
        Assert.Equal("16:9", ExportGeometry.AspectName(3840, 2160));
        Assert.Equal("1,78:1", ExportGeometry.AspectName(854, 480));
    }

    [Fact]
    public void CambiarLaSecuenciaA916_SeDeshace()
    {
        Project project = ExportTestData.Project();
        var stack = new UndoStack(project);
        var command = new SequenceShapeCommand("sec", 1080, 1920);

        stack.Execute(command);
        Assert.Equal((1080, 1920), (project.Sequences[0].Width, project.Sequences[0].Height));
        Assert.Equal("Cambiar la secuencia a 9:16", command.Description);

        stack.Undo();
        Assert.Equal((1920, 1080), (project.Sequences[0].Width, project.Sequences[0].Height));
    }
}
