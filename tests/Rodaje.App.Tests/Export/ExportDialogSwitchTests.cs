using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;

namespace Rodaje.App.Tests.Export;

/// <summary>Error reportado: «cuando se selecciona un archivo o modo en exportar no deja seleccionar otro». Todo se puede cambiar siempre.</summary>
public class ExportDialogSwitchTests
{
    [Fact]
    public void LosPreajustes_SeCambianUnoTrasOtro_YSeVuelveACualquiera()
    {
        using var dir = new TempDirectory();
        Project project = ExportTestData.Project();
        var model = new ExportDialogModel(new ExportPreferences { Folder = dir.Path }, project.Sequences[0], [], SimulatedEncoders.All, "Corto", null);
        int changes = 0;
        model.Changed += (_, _) => changes++;

        string[] order = [.. model.Presets.Select(p => p.Id), ExportPresetLibrary.CustomId, .. model.Presets.Select(p => p.Id).Reverse()];
        foreach (string id in order)
        {
            int before = changes;
            model.SelectPreset(id);
            Assert.Equal(id, model.PresetId);
            Assert.True(changes > before, $"Elegir «{id}» no avisó a la vista.");
        }
    }

    [Fact]
    public void ElRangoLaRutaYElRendimiento_SeCambianVariasVeces()
    {
        using var dir = new TempDirectory();
        Project project = ExportTestData.Project();
        var model = new ExportDialogModel(new ExportPreferences { Folder = dir.Path }, project.Sequences[0], ["b"], SimulatedEncoders.All, "Corto", null);
        Assert.True(model.Ranges.Count >= 3);

        foreach (int index in new[] { 1, 2, 0, 2, 1, 0 })
        {
            model.SetRange(index);
            Assert.Equal(index, model.RangeIndex);
        }

        model.SetOutputPath(dir.File("uno.mp4"));
        model.SetOutputPath(dir.File("dos.mp4"));
        Assert.Equal(dir.File("dos.mp4"), model.OutputPath);

        model.SetPerformance(ExportPerformance.Fastest);
        model.SetPerformance(ExportPerformance.Background);
        Assert.Equal(ExportPerformance.Background, model.Performance);
    }

    [Fact]
    public void ConUnProyectoVertical_YouTubeSaleVerticalSinAviso_YElCuadradoAvisa()
    {
        using var dir = new TempDirectory();
        Project project = ExportTestData.Project(1080, 1920);
        var model = new ExportDialogModel(new ExportPreferences { Folder = dir.Path }, project.Sequences[0], [], SimulatedEncoders.All, "Corto", null);

        Assert.Equal((1080, 1920), model.OutputSize);
        Assert.Null(model.PresetShapeNote);
        model.SelectPreset(ExportPresets.YouTube4K.Id);
        Assert.Equal((2160, 3840), model.OutputSize);
        model.SelectPreset(ExportPresets.Vertical.Id);
        Assert.Null(model.PresetShapeNote);
        model.SelectPreset(ExportPresets.Square.Id);
        Assert.Equal((1080, 1920), model.OutputSize);
        Assert.NotNull(model.PresetShapeNote);
    }

    [Fact]
    public void LaVista_SoloRehaceLaListaSiCambio()
    {
        Assert.False(ComboChoices.NeedsRebuild(["MP4", "MOV"], ["MP4", "MOV"]));
        Assert.True(ComboChoices.NeedsRebuild(["MP4", "MOV"], ["MP4"]));
        Assert.True(ComboChoices.NeedsRebuild(["Toda la secuencia (0:10)"], ["Toda la secuencia (0:09)"]));
    }

    [Fact]
    public void VariasVersiones_UnNombreUnaVez_YElOrigenSoloSiDifieren()
    {
        ExportPreset factory = ExportPresets.ProResMaster;
        ExportPreset copy = factory with { Id = "pro/maestro", Origin = ExportPresetOrigin.Package };
        ExportPreset other = copy with { Id = "pro/otro", Settings = factory.Settings with { Audio = null } };

        Assert.Equal(new (ExportPreset, string?)[] { (factory, null), (ExportPresets.Gif, null) },
            ExportPresetLibrary.DistinctByName([factory, copy, ExportPresets.Gif]));
        Assert.Equal(new (ExportPreset, string?)[] { (factory, "de serie"), (other, "de un paquete") },
            ExportPresetLibrary.DistinctByName([factory, other]));
    }

    [Fact]
    public void LaNotaDelPreajuste_AvisaDelCambioDeCadencia()
    {
        using var dir = new TempDirectory();
        Project vertical = ExportTestData.Project(1080, 1920);
        vertical.Sequences[0].FrameRate = Rational.Ntsc24;
        var model = new ExportDialogModel(new ExportPreferences { Folder = dir.Path }, vertical.Sequences[0], [], SimulatedEncoders.All, "Corto", null);
        model.SelectPreset(ExportPresets.Vertical.Id);
        Assert.Equal("«Vertical 9:16» se exporta a 30 fps (el proyecto va a 23,976).", model.PresetShapeNote);
        model.SelectPreset(ExportPresets.YouTube1080p.Id);
        Assert.Null(model.PresetShapeNote);

        Project wide = ExportTestData.Project();
        wide.Sequences[0].FrameRate = Rational.Ntsc24;
        model = new ExportDialogModel(new ExportPreferences { Folder = dir.Path }, wide.Sequences[0], [], SimulatedEncoders.All, "Corto", null);
        model.SelectPreset(ExportPresets.Vertical.Id);
        Assert.Equal("«Vertical 9:16» es para 9:16 y el proyecto es 16:9: se exporta con la forma del proyecto, a 30 fps (el proyecto va a 23,976).",
            model.PresetShapeNote);
    }
}
