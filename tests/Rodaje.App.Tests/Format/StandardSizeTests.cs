using Rodaje.App.Services;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Tests.Export;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Tests.Format;

/// <summary>Tamaños raros (la pantalla de un celular) frente a los estándar: tabla de <c>docs/resoluciones-estandar.md</c>.</summary>
public class StandardSizeTests
{
    [Theory]
    [InlineData(1080, 2400, 1080, 1920)]
    [InlineData(1080, 2340, 1080, 1920)]
    [InlineData(1440, 3200, 1440, 2560)]
    [InlineData(2340, 1080, 1920, 1080)]
    [InlineData(1366, 768, 1920, 1080)]
    [InlineData(2560, 1080, 1920, 1080)]
    [InlineData(3440, 1440, 2560, 1440)]
    [InlineData(1088, 1920, 1080, 1920)]
    [InlineData(640, 480, 1280, 720)]
    [InlineData(1000, 1000, 1080, 1080)]
    [InlineData(864, 1080, 1080, 1350)]
    [InlineData(5120, 2880, 7680, 4320)]
    [InlineData(8192, 6000, 7680, 4320)]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(4096, 2160, 4096, 2160)]
    [InlineData(1080, 1350, 1080, 1350)]
    public void ElEstandarMasCercano(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), ProjectFormats.NearestStandard(width, height));
        Assert.True(ProjectFormats.IsStandard(expectedWidth, expectedHeight));
    }

    [Fact]
    public void LaListaDeEstandares()
    {
        Assert.False(ProjectFormats.IsStandard(1080, 2400));
        Assert.All(ProjectFormats.StandardSizes, s => Assert.True(ProjectFormats.IsValidSize(s.Width, s.Height)));
        Assert.Equal("estándar vertical", ProjectFormats.StandardName(1080, 1920));
        Assert.Equal("estándar horizontal", ProjectFormats.StandardName(1920, 1080));
    }

    [Fact]
    public void ElPrimerClipRaro_OfreceElEstandar_EnUnPasoQueSeDeshace() => UiThread.Run(async () =>
    {
        var prober = new FakeProber().Video("celular.mp4", 10, 1080, 2400, Rational.Fps30);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);

        await session.ImportMediaAsync([Path.GetFullPath("celular.mp4")]);

        Assert.Equal((1080, 2400), (session.Sequence.Width, session.Sequence.Height));
        EditorNotification notice = Assert.Single(notices);
        Assert.Equal("La secuencia tomó 1080×2400 del clip.", notice.Message);
        Assert.Equal("Usar 1080×1920 (estándar vertical)", notice.Action!.Text);
        Assert.Equal("Dejar así", notice.SecondaryAction!.Text);
        Assert.False(notice.IsStale!());

        notice.Action.Run();
        Assert.Equal((1080, 1920), (session.Sequence.Width, session.Sequence.Height));
        Assert.True(notice.IsStale());
        session.Undo();
        Assert.Equal((1080, 2400), (session.Sequence.Width, session.Sequence.Height));
    });

    [Fact]
    public void ElPrimerClipEstandar_NoAvisa() => UiThread.Run(async () =>
    {
        var prober = new FakeProber().Video("a.mp4", 10, 1080, 1920, Rational.Fps30);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);

        await session.ImportMediaAsync([Path.GetFullPath("a.mp4")]);

        Assert.Equal((1080, 1920), (session.Sequence.Width, session.Sequence.Height));
        Assert.Empty(notices);
    });

    private static ExportDialogModel Model(int width, int height)
    {
        var project = ExportTestData.Project(width, height);
        return new ExportDialogModel(new ExportPreferences(), project.Sequences[0], [], SimulatedEncoders.All, project.Name, null);
    }

    [Fact]
    public void Exportar_DiceElTamanoReal_YOfreceElEstandar()
    {
        ExportDialogModel model = Model(1080, 2400);

        Assert.Equal("1080×2400, como la secuencia", model.PresetSizeLabel(ExportPresets.YouTube1080p));
        Assert.Equal((1080, 1920), model.StandardSuggestion);

        model.UseStandardSize();

        Assert.Equal((1080, 1920), model.OutputSize);
        Assert.Equal(ExportFit.Crop, model.Settings.Video!.Fit);
        Assert.Null(model.StandardSuggestion);
        Assert.Equal("1080×1920", model.SizeLabel(model.OutputSize));
        // Las etiquetas de las tarjetas no dependen de lo elegido (tarea 91).
        Assert.Equal("1080×2400, como la secuencia", model.PresetSizeLabel(ExportPresets.YouTube1080p));
    }

    [Fact]
    public void Exportar_UnaSecuenciaEstandar_NoOfreceNada()
    {
        ExportDialogModel model = Model(1920, 1080);

        Assert.Null(model.StandardSuggestion);
        Assert.Equal("1920×1080, como la secuencia", model.PresetSizeLabel(ExportPresets.YouTube1080p));
    }
}
