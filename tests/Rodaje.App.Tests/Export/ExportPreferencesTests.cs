using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Tests.Export;

public class ExportPreferencesTests
{
    [Fact]
    public void LosAjustesRecordadosYLosPreajustesPropios_SobrevivenAlArchivo()
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        var store = new SettingsStore(path);
        ExportPreferences prefs = store.Current.Export;
        ExportSettings vertical = ExportPresets.Vertical.Settings with
        {
            Performance = ExportPerformance.Fastest,
            Metadata = new ExportMetadata { Title = "Del proyecto" },
        };
        ExportPresetLibrary.Remember(prefs, ExportPresets.Vertical.Id, vertical, Path.Combine(dir.Path, "salida.mp4"));
        ExportPresetLibrary.SaveUserPreset(prefs, "Mi canal ñ", ExportPresets.ProResMaster.Settings);
        store.Save();

        var loaded = new SettingsStore(path);
        Assert.Null(loaded.Load());
        ExportPreferences back = loaded.Current.Export;

        Assert.Equal(ExportPresets.Vertical.Id, back.PresetId);
        Assert.Equal(ExportPerformance.Fastest, back.Performance);
        Assert.Equal(dir.Path, back.Folder);
        Assert.True(ExportPresetLibrary.Matches(ExportPresets.Vertical, back.Settings!));
        Assert.Null(back.Settings!.Metadata.Title);
        ExportPreset mine = Assert.Single(back.UserPresets);
        Assert.Equal("Mi canal ñ", mine.Name);
        Assert.Equal("usuario.mi-canal-n", mine.Id);
        Assert.Equal(ExportPresetOrigin.User, mine.Origin);
        Assert.True(ExportPresetLibrary.Matches(ExportPresets.ProResMaster, mine.Settings));
    }

    [Fact]
    public void ConElMismoNombre_SeReemplaza_YLosIdsNoChocan()
    {
        var prefs = new ExportPreferences();

        ExportPreset first = ExportPresetLibrary.SaveUserPreset(prefs, "Web", ExportPresets.YouTube1080p.Settings);
        ExportPreset again = ExportPresetLibrary.SaveUserPreset(prefs, "web", ExportPresets.SmallFile.Settings);
        ExportPreset other = ExportPresetLibrary.SaveUserPreset(prefs, "Web!", ExportPresets.Gif.Settings);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(2, prefs.UserPresets.Count);
        Assert.Equal("usuario.web-2", other.Id);
        Assert.True(ExportPresetLibrary.Matches(ExportPresets.SmallFile, prefs.UserPresets[0].Settings));
        Assert.Equal(ExportPresets.All.Count + 2, ExportPresetLibrary.All(prefs).Count);

        prefs.PresetId = other.Id;
        Assert.True(ExportPresetLibrary.Remove(prefs, other.Id));
        Assert.Null(prefs.PresetId);
        Assert.Throws<ArgumentException>(() => ExportPresetLibrary.SaveUserPreset(prefs, "  ", ExportPresets.Gif.Settings));
    }

    [Fact]
    public void UnPreajusteSeLlevaEnArchivoAOtroEquipo()
    {
        using var dir = new TempDirectory();
        string file = dir.File("canal" + ExportPresetLibrary.FileExtension);
        var source = new ExportPreferences();
        ExportPreset preset = ExportPresetLibrary.SaveUserPreset(source, "Canal", ExportPresets.Vertical.Settings);
        ExportPresetLibrary.WriteFile(preset, file);

        var target = new ExportPreferences();
        ExportPreset imported = ExportPresetLibrary.Import(target, file);

        Assert.Equal("Canal", imported.Name);
        Assert.True(ExportPresetLibrary.Matches(ExportPresets.Vertical, imported.Settings));

        // Uno propio con el mismo nombre no se pisa: el importado se llama «Canal (2)».
        ExportPresetLibrary.SaveUserPreset(target, "canal", ExportPresets.Gif.Settings);
        Assert.Equal("Canal (2)", ExportPresetLibrary.Import(target, file).Name);
        Assert.Equal(2, target.UserPresets.Count);
        Assert.True(ExportPresetLibrary.Matches(ExportPresets.Gif, ExportPresetLibrary.FindUserPreset(target, " CANAL ")!.Settings));

        File.WriteAllText(dir.File("malo" + ExportPresetLibrary.FileExtension), "{ no es json");
        Assert.Throws<InvalidDataException>(() => ExportPresetLibrary.Import(target, dir.File("malo" + ExportPresetLibrary.FileExtension)));
    }

    [Fact]
    public void LoDanadoONoValido_SeLimpiaAlLeer()
    {
        var prefs = new ExportPreferences
        {
            Performance = (ExportPerformance)42,
            UserPresets =
            [
                new ExportPreset("usuario.a", "A", "", ExportPresetCategory.Custom, ExportPresetOrigin.Factory, ExportPresets.Gif.Settings),
                new ExportPreset("usuario.a", "Repetido", "", ExportPresetCategory.Custom, ExportPresetOrigin.User, ExportPresets.Gif.Settings),
                new ExportPreset("", "Sin id", "", ExportPresetCategory.Custom, ExportPresetOrigin.User, ExportPresets.Gif.Settings),
            ],
        };

        prefs.Normalize();

        Assert.Equal(ExportPerformance.Background, prefs.Performance);
        ExportPreset kept = Assert.Single(prefs.UserPresets);
        Assert.Equal(ExportPresetOrigin.User, kept.Origin);
    }

    [Fact]
    public void ElDialogo_ArrancaConLoRecordadoYElRendimientoAnterior()
    {
        using var dir = new TempDirectory();
        Project project = ExportTestData.Project(1920, 1080);
        var prefs = new ExportPreferences { Performance = ExportPerformance.Fastest, Folder = dir.Path };
        ExportPresetLibrary.Remember(prefs, ExportPresets.Vertical.Id, ExportPresets.Vertical.Settings with { Performance = ExportPerformance.Fastest },
            dir.File("x.mp4"));

        var model = new ExportDialogModel(prefs, project.Sequences[0], ["b"], SimulatedEncoders.All, project.Name, null);

        Assert.Equal(ExportPresets.Vertical.Id, model.PresetId);
        Assert.Equal(ExportPerformance.Fastest, model.Performance);
        Assert.Equal(dir.File("Mi película_ versión 1.mp4"), model.OutputPath);
        // El formato es el del proyecto (16:9): el preajuste vertical sale con su forma y lo avisa.
        Assert.False(model.ShapeDiffers);
        Assert.Equal((1920, 1080), model.OutputSize);
        Assert.NotNull(model.PresetShapeNote);
        Assert.True(model.CanExport, string.Join(" ", model.Problems));

        model.Update(s => s with { Video = s.Video! with { Width = 1080, Height = 1920 } });
        Assert.True(model.ShapeDiffers);
        Assert.Equal(ExportPresetLibrary.CustomId, model.PresetId);
    }

    [Fact]
    public void ElDialogo_SinNadaRecordado_ProponeYouTubeYBajoRendimiento()
    {
        Project project = ExportTestData.Project();

        var model = new ExportDialogModel(new ExportPreferences(), project.Sequences[0], [], SimulatedEncoders.All, project.Name, null);

        Assert.Equal(ExportPresets.YouTube1080p.Id, model.PresetId);
        Assert.Equal(ExportPerformance.Background, model.Performance);
        Assert.False(model.ShapeDiffers);
    }

    [Fact]
    public void CambiarAlgo_PasaAPersonalizado_YLaExtensionSigueAlContenedor()
    {
        using var dir = new TempDirectory();
        Project project = ExportTestData.Project();
        var model = new ExportDialogModel(new ExportPreferences { Folder = dir.Path }, project.Sequences[0], [], SimulatedEncoders.All, "Corto", null);

        model.Update(s => s with { Container = ExportContainer.Mov });

        Assert.Equal(ExportPresetLibrary.CustomId, model.PresetId);
        Assert.Equal(dir.File("Corto.mov"), model.OutputPath);

        model.SelectPreset(ExportPresets.YouTube1080p.Id);
        Assert.Equal(dir.File("Corto.mp4"), model.OutputPath);
        Assert.Equal(ExportPresets.YouTube1080p.Id, model.PresetId);
    }

    [Fact]
    public void CrearLaExportacion_FijaCodificadoresYRendimiento_YLoRecuerda()
    {
        using var dir = new TempDirectory();
        Project project = ExportTestData.Project();
        var prefs = new ExportPreferences { Folder = dir.Path };
        var model = new ExportDialogModel(prefs, project.Sequences[0], [], SimulatedEncoders.All, "Corto", null);
        model.SetRange(2);
        model.SetPerformance(ExportPerformance.Fastest);

        ExportRequest request = model.CreateRequest(project.Clone(), "sec", dir.Path);

        Assert.Equal("h264_nvenc", request.Settings.Video!.EncoderId);
        Assert.Equal("aac_mf", request.Settings.Audio!.EncoderId);
        Assert.Equal(ExportPerformance.Fastest, request.Settings.Performance);
        Assert.Equal(dir.Path, request.ProjectDirectory);
        Assert.Equal(2, request.Range!.Value.Duration.ToSeconds(), 6);
        Assert.Equal(ExportPerformance.Fastest, prefs.Performance);
        Assert.Equal(ExportPresets.YouTube1080p.Id, prefs.PresetId);
        Assert.Null(prefs.Settings!.Video!.EncoderId);
    }

    [Fact]
    public void SinCarpetaOConLaSecuenciaVacia_NoSePuedeExportar()
    {
        Project project = ExportTestData.Project();
        var model = new ExportDialogModel(new ExportPreferences(), project.Sequences[0], [], SimulatedEncoders.All, "Corto", null);

        model.SetOutputPath(@"Z:\no existe ñ\video.mp4");
        Assert.Contains("La carpeta no existe.", model.Problems);
        Assert.Throws<InvalidOperationException>(() => model.CreateRequest(project, "sec", null));

        var empty = new Sequence { Id = "s", Width = 1920, Height = 1080 };
        var emptyModel = new ExportDialogModel(new ExportPreferences(), empty, [], SimulatedEncoders.All, "Vacío", null);
        Assert.Contains(emptyModel.Problems, p => p.Contains("vacía", StringComparison.Ordinal));
    }

    [Fact]
    public void LosRangos_SonTodoLoSeleccionadoYLasRegiones()
    {
        Project project = ExportTestData.Project();

        IReadOnlyList<RangeChoice> ranges = ExportRanges.Choices(project.Sequences[0], ["b", "no-existe"]);

        Assert.Equal(3, ranges.Count);
        Assert.Null(ranges[0].Range);
        Assert.Equal("Toda la secuencia (0:10)", ranges[0].Label);
        Assert.Equal((5.0, 10.0), (ranges[1].Range!.Value.Start.ToSeconds(), ranges[1].Range!.Value.End.ToSeconds()));
        Assert.StartsWith("Región «Intro»", ranges[2].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void LaRuta_SeComprueba()
    {
        using var dir = new TempDirectory();
        string existing = dir.File("ya.mp4");
        File.WriteAllText(existing, "x");

        Assert.Equal(OutputCheck.Exists, ExportRanges.Check(existing));
        Assert.Equal(OutputCheck.Ok, ExportRanges.Check(dir.File("nuevo.mp4")));
        Assert.Equal(OutputCheck.FolderMissing, ExportRanges.Check(Path.Combine(dir.Path, "falta", "a.mp4")));
        Assert.Equal(OutputCheck.InvalidName, ExportRanges.Check("relativo.mp4"));
        Assert.Equal(OutputCheck.Missing, ExportRanges.Check(" "));
        Assert.Equal("a.tif", Path.GetFileName(ExportRanges.WithExtension(dir.File("a.png"),
            new ExportSettings { Container = ExportContainer.ImageSequence, Video = new VideoExportSettings { Codec = ExportCodec.Tiff } })));
        ExportSettings mp4 = ExportPresets.YouTube1080p.Settings;
        Assert.Equal("Prueba 1.5.mp4", Path.GetFileName(ExportRanges.WithExtension(dir.File("Prueba 1.5"), mp4)));
        Assert.Equal("Boda 24.10.2026 - Web.mp4", Path.GetFileName(ExportRanges.WithExtension(dir.File("Boda 24.10.2026 - Web"), mp4)));
        Assert.Equal("a.mp4", Path.GetFileName(ExportRanges.WithExtension(dir.File("a.MOV"), mp4)));
        Assert.Equal("a.mp4", Path.GetFileName(ExportRanges.WithExtension(dir.File("a."), mp4)));

        // Una secuencia de imágenes no se escribe en una carpeta con archivos: no se ofrece reemplazarla.
        string frames = dir.File("cuadros");
        Directory.CreateDirectory(frames);
        Assert.Equal(OutputCheck.Exists, ExportRanges.Check(frames, ExportContainer.ImageSequence));
        File.WriteAllText(Path.Combine(frames, "000001.png"), "x");
        Assert.Equal(OutputCheck.FolderNotEmpty, ExportRanges.Check(frames, ExportContainer.ImageSequence));
        Assert.Equal(OutputCheck.Exists, ExportRanges.Check(frames));
        Assert.NotNull(ExportRanges.Describe(OutputCheck.FolderNotEmpty));
    }
}
