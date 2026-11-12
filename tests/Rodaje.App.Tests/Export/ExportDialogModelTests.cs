using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
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

    [Fact]
    public void Versions_do_not_replace_existing_files()
    {
        using var dir = new TempDirectory();
        var project = ExportTestData.Project();
        var model = new ExportDialogModel(new ExportPreferences(), project.Sequences[0], [], SimulatedEncoders.All, project.Name, dir.Path);
        model.SetOutputPath(dir.File("viaje.mp4"));
        File.WriteAllText(dir.File("viaje - Web 1080p.mp4"), "");
        File.WriteAllText(dir.File("viaje - Web 1080p (2).mp4"), "");

        IReadOnlyList<ExportRequest> requests = model.CreateVersionRequests([ExportPresets.YouTube1080p.Id, ExportPresets.Vertical.Id], project, "sec", null);

        Assert.Equal("viaje - Web 1080p (3).mp4", Path.GetFileName(requests[0].OutputPath));
        Assert.All(requests, r => Assert.False(File.Exists(r.OutputPath)));
    }

    [Fact]
    public void Names_with_dots_keep_them_and_versions_do_not_loop()
    {
        using var dir = new TempDirectory();
        var project = ExportTestData.Project();
        var model = new ExportDialogModel(new ExportPreferences { Folder = dir.Path }, project.Sequences[0], [], SimulatedEncoders.All, "Boda 24.10.2026", null);
        Assert.Equal("Boda 24.10.2026.mp4", Path.GetFileName(model.OutputPath));

        model.SetOutputPath(dir.File("Prueba 1.5"));
        Assert.Equal("Prueba 1.5.mp4", Path.GetFileName(model.OutputPath));
        File.WriteAllText(dir.File("Prueba 1.5 - Web 1080p.mp4"), "");

        IReadOnlyList<ExportRequest> requests = model.CreateVersionRequests([ExportPresets.YouTube1080p.Id, ExportPresets.Vertical.Id], project, "sec", null);

        Assert.Equal(["Prueba 1.5 - Web 1080p (2).mp4", "Prueba 1.5 - Vertical 9_16.mp4"], requests.Select(r => Path.GetFileName(r.OutputPath)));
    }

    [Fact]
    public void Outputs_still_in_the_queue_count_as_existing_files()
    {
        using var dir = new TempDirectory();
        var project = ExportTestData.Project();
        var model = new ExportDialogModel(new ExportPreferences(), project.Sequences[0], [], SimulatedEncoders.All, project.Name, dir.Path)
        {
            PendingOutputs = [dir.File("VIAJE.mp4"), dir.File("viaje - web 1080p.mp4")],
        };
        model.SetOutputPath(dir.File("viaje.mp4"));
        Assert.Equal(OutputCheck.Exists, model.OutputCheck);
        Assert.True(model.CanExport);

        IReadOnlyList<ExportRequest> requests = model.CreateVersionRequests([ExportPresets.YouTube1080p.Id], project, "sec", null);

        Assert.Equal("viaje - Web 1080p (2).mp4", Path.GetFileName(requests[0].OutputPath));
        model.SetOutputPath(dir.File("otro.mp4"));
        Assert.Equal(OutputCheck.Ok, model.OutputCheck);
    }

    [Fact]
    public void Versions_keep_the_subtitle_mode_and_warn_where_the_container_cannot_take_it()
    {
        var project = ExportTestData.Project();
        var subtitles = new Track { Id = "s", Kind = TrackKind.Video, Role = TrackRoles.Subtitles };
        subtitles.Clips.Add(new Clip { Id = "t", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = Time.FromSeconds(2), Generator = new ClipGenerator { Kind = GeneratorKinds.Text, Text = "Hola" } });
        project.Sequences[0].VideoTracks.Add(subtitles);
        var model = new ExportDialogModel(new ExportPreferences(), project.Sequences[0], [], SimulatedEncoders.All, project.Name, null);
        model.Update(s => s with { Subtitles = SubtitleExport.Embedded });
        string[] ids = [ExportPresets.YouTube1080p.Id, ExportPresets.Gif.Id];

        IReadOnlyList<ExportRequest> requests = model.CreateVersionRequests(ids, project, "sec", null);

        Assert.Equal(SubtitleExport.Embedded, requests[0].Settings.Subtitles);
        Assert.Equal(SubtitleExport.Burned, requests[1].Settings.Subtitles);
        Assert.Contains("GIF animado", model.VersionsNote(ids));
        Assert.Null(model.VersionsNote([ExportPresets.YouTube1080p.Id]));
    }

    [Fact]
    public void Remembered_settings_keep_the_subtitle_mode_and_still_match_the_preset()
    {
        var project = ExportTestData.Project();
        var preferences = new ExportPreferences();
        var model = new ExportDialogModel(preferences, project.Sequences[0], [], SimulatedEncoders.All, project.Name, null);
        model.Update(s => s with { Subtitles = SubtitleExport.Embedded });
        model.RememberDraft();

        var reopened = new ExportDialogModel(preferences, project.Sequences[0], [], SimulatedEncoders.All, project.Name, null);

        Assert.Equal(SubtitleExport.Embedded, reopened.Settings.Subtitles);
        Assert.Equal(ExportPresets.YouTube1080p.Id, reopened.PresetId);
    }

    [Fact]
    public void Versions_refuse_an_empty_sequence_and_say_why()
    {
        var project = ExportTestData.Project();
        project.Sequences[0].VideoTracks[0].Clips.Clear();
        var model = new ExportDialogModel(new ExportPreferences(), project.Sequences[0], [], SimulatedEncoders.All, project.Name, null);

        Assert.Contains(model.VersionProblems, p => p.Contains("vacía", StringComparison.Ordinal));
        var error = Assert.Throws<InvalidOperationException>(() => model.CreateVersionRequests([ExportPresets.YouTube1080p.Id], project, "sec", null));
        Assert.Contains("vacía", error.Message, StringComparison.Ordinal);
    }
}
