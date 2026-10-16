using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Export;

namespace Rodaje.App.Tests.Export;

public sealed class ExportDialogModelTests
{
    [Fact]
    public void Fit_is_needed_only_when_output_shape_differs_from_sequence()
    {
        var project = ExportTestData.Project();
        var model = new ExportDialogModel(new ExportPreferences(), project.Sequences[0], [], SimulatedEncoders.All, project.Name, null);
        Assert.False(model.ShapeDiffers);

        model.Update(settings => settings.Video is { } video
            ? settings with { Video = video with { Width = 1080, Height = 1080 } }
            : settings);

        Assert.True(model.ShapeDiffers);
    }

    [Fact]
    public void RememberDraft_preserves_export_choices_when_project_format_is_opened()
    {
        using var dir = new TempDirectory();
        ExportPreferences preferences = new() { Folder = dir.Path };
        var project = ExportTestData.Project();
        var model = new ExportDialogModel(preferences, project.Sequences[0], [], SimulatedEncoders.All, project.Name, dir.Path);
        model.SelectPreset(ExportPresets.Square.Id);
        model.SetPerformance(ExportPerformance.Fastest);
        model.SetOutputPath(dir.File("salida-cuadrada.mp4"));

        model.RememberDraft();

        Assert.Equal(ExportPresets.Square.Id, preferences.PresetId);
        Assert.Equal(dir.Path, preferences.Folder);
        Assert.Equal(ExportPerformance.Fastest, preferences.Performance);
        Assert.Equal("salida-cuadrada.mp4", Path.GetFileName(model.OutputPath));
        Assert.Equal(model.Settings.Video?.Fit, preferences.Settings?.Video?.Fit);
    }
}
