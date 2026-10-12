using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Still;
using Rodaje.Media.Export;

namespace Rodaje.Export.Tests.Still;

/// <summary>Lo que se decide del fotograma sin GPU ni procesos: corre también en Linux.</summary>
public sealed class StillPlanTests
{
    private static Project Project(int width = 1920, int height = 1080, Rational? rate = null) => new()
    {
        Id = "p",
        Name = "Fotograma",
        Sequences = [new Sequence { Id = "sec", Name = "Sec", Width = width, Height = height, FrameRate = rate ?? Rational.Fps30 }],
    };

    private static StillPlan Plan(StillSettings settings, Time? position = null, Project? project = null) =>
        StillPlan.Create(new StillRequest(project ?? Project(), "sec", position ?? Time.Zero, "/tmp/foto." + StillSettings.Extension(settings.Format), settings));

    [Fact]
    public void Por_omision_sale_un_png_rgb_del_tamano_de_la_secuencia()
    {
        StillPlan plan = Plan(new StillSettings());
        Assert.Equal((1920, 1080), (plan.Output.Width, plan.Output.Height));
        Assert.Equal(ExportPixelFormat.Rgb24, plan.Output.PixelFormat);
        Assert.Equal("png", plan.Encoder.Id);
        Assert.Empty(plan.Filters);
        Assert.False(plan.HasAlpha);
    }

    [Fact]
    public void Png_con_alfa_baja_rgba()
    {
        StillPlan plan = Plan(new StillSettings { Alpha = true });
        Assert.Equal(ExportPixelFormat.Rgba, plan.Output.PixelFormat);
        Assert.True(plan.HasAlpha);
        Assert.Contains(ExportPixelFormat.Rgba, plan.Encoder.PixelFormats);
    }

    [Fact]
    public void Jpeg_recibe_rgb_y_lo_convierte_a_bt601_completo_sin_strict()
    {
        StillPlan plan = Plan(new StillSettings { Format = StillFormat.Jpeg, JpegQuality = 92 });
        Assert.Equal("mjpeg", plan.Encoder.Id);
        Assert.Equal([ExportPixelFormat.Rgb24], plan.Encoder.PixelFormats);
        Assert.DoesNotContain("-strict", plan.Encoder.BaseArguments);
        Assert.Equal(["-q:v", "4", "-color_range", "pc", "-colorspace", "bt470bg"], plan.Arguments);
        Assert.Equal(["scale=out_color_matrix=bt601:out_range=full", "format=yuv444p"], plan.Filters);
        Assert.Equal(ExportPixelFormat.Rgb24, plan.Output.PixelFormat);
    }

    [Theory]
    [InlineData(100, 2)]
    [InlineData(92, 4)]
    [InlineData(75, 9)]
    [InlineData(50, 17)]
    [InlineData(1, 31)]
    public void La_calidad_jpeg_va_a_la_escala_de_mjpeg(int quality, int scale) => Assert.Equal(scale, StillPlan.JpegScale(quality));

    [Fact]
    public void La_escala_jpeg_mejora_siempre_con_la_calidad()
    {
        for (int q = 2; q <= 100; q++)
        {
            Assert.True(StillPlan.JpegScale(q) <= StillPlan.JpegScale(q - 1), $"Calidad {q}.");
        }
    }

    [Fact]
    public void El_cuadro_es_el_que_muestra_el_visor_en_el_cursor()
    {
        var rate = new Rational(30000, 1001);
        Assert.Equal(100, Plan(new StillSettings(), Time.FromFrames(100, rate), Project(rate: rate)).Frame);
        // A mitad de un cuadro, el que se está mostrando (hacia abajo).
        Time middle = Time.FromFrames(100, rate) + Time.FromTicks((Time.FromFrames(101, rate) - Time.FromFrames(100, rate)).Ticks / 2);
        Assert.Equal(100, Plan(new StillSettings(), middle, Project(rate: rate)).Frame);
    }

    [Fact]
    public void El_tamano_del_preajuste_se_respeta_con_su_forma()
    {
        StillPlan vertical = Plan(StillSettings.FromPreset(ExportPresets.Vertical));
        Assert.Equal((1080, 1920), (vertical.Output.Width, vertical.Output.Height));
        Assert.Equal(ExportFit.Bars, vertical.Settings.Fit);

        StillPlan uhd = Plan(StillSettings.FromPreset(ExportPresets.YouTube4K, StillFormat.Jpeg));
        Assert.Equal((3840, 2160), (uhd.Output.Width, uhd.Output.Height));
        Assert.Equal("mjpeg", uhd.Encoder.Id);
    }

    [Fact]
    public void Con_un_solo_lado_el_otro_sigue_la_forma_y_puede_ser_impar()
    {
        StillPlan plan = Plan(new StillSettings { Width = 641 });
        Assert.Equal((641, 361), (plan.Output.Width, plan.Output.Height));
    }

    [Fact]
    public void El_recorte_decide_el_tamano_si_no_se_pide_otro()
    {
        StillPlan plan = Plan(new StillSettings { Crop = new ExportCrop(100, 50, 801, 401) });
        Assert.Equal((801, 401), (plan.Output.Width, plan.Output.Height));
    }

    [Fact]
    public void El_formato_sale_de_la_extension()
    {
        Assert.Equal(StillFormat.Jpeg, StillSettings.FormatFor(@"C:\fotos\cuadro.JPG"));
        Assert.Equal(StillFormat.Jpeg, StillSettings.FormatFor("cuadro.jpeg"));
        Assert.Equal(StillFormat.Png, StillSettings.FormatFor("cuadro.png"));
        Assert.Equal(StillFormat.Png, StillSettings.FormatFor("cuadro"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Una_calidad_fuera_de_rango_no_vale(int quality)
    {
        ExportException error = Assert.Throws<ExportException>(() => Plan(new StillSettings { Format = StillFormat.Jpeg, JpegQuality = quality }));
        Assert.Equal(ExportErrorCode.InvalidSettings, error.Code);
        Assert.Contains("calidad JPEG", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Jpeg_con_alfa_no_vale_y_el_preajuste_no_lo_pide()
    {
        ExportException error = Assert.Throws<ExportException>(() => Plan(new StillSettings { Format = StillFormat.Jpeg, Alpha = true }));
        Assert.Contains("transparencia", error.Message, StringComparison.Ordinal);

        var alpha = new ExportPreset("usuario.alfa", "Alfa", "", ExportPresetCategory.Custom, ExportPresetOrigin.User,
            new ExportSettings { Video = new VideoExportSettings { Codec = ExportCodec.ProRes, Alpha = true } });
        Assert.True(StillSettings.FromPreset(alpha).Alpha);
        Assert.False(StillSettings.FromPreset(alpha, StillFormat.Jpeg).Alpha);
    }

    [Fact]
    public void Un_recorte_fuera_de_la_secuencia_o_una_secuencia_que_no_existe_no_valen()
    {
        Assert.Equal(ExportErrorCode.InvalidSettings,
            Assert.Throws<ExportException>(() => Plan(new StillSettings { Crop = new ExportCrop(1800, 0, 200, 100) })).Code);
        Assert.Equal(ExportErrorCode.InvalidSettings, Assert.Throws<ExportException>(() =>
            StillPlan.Create(new StillRequest(Project(), "otra", Time.Zero, "/tmp/foto.png", new StillSettings()))).Code);
        Assert.Equal(ExportErrorCode.InvalidSettings,
            Assert.Throws<ExportException>(() => Plan(new StillSettings { Width = 20_000 })).Code);
    }
}
