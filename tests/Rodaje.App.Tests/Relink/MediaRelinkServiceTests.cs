using Rodaje.App.Services;
using Rodaje.App.Services.Relink;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Tests.Relink;

/// <summary>Volver a enlazar al abrir un proyecto con medios movidos.</summary>
public class MediaRelinkServiceTests
{
    /// <summary>Un proyecto guardado con «toma.mp4» en la línea de tiempo; el archivo luego «se mueve» a otra carpeta.</summary>
    private static async Task<(EditorSession Session, string ProjectPath)> SavedProjectAsync(TempDirectory dir, FakeProber prober)
    {
        string original = dir.File("toma.mp4");
        prober.Video(original, 20);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs { Choice = UnsavedChangesChoice.Discard }, new SequentialIdGenerator());
        ImportResult imported = await session.ImportMediaAsync([original]);
        Assert.True(session.AppendToTimeline(imported.MediaIds[0]));
        string projectPath = dir.File("proyecto.rodaje");
        ProjectSerializer.Save(session.Project, projectPath);
        return (session, projectPath);
    }

    [Fact]
    public void Al_abrir_muestra_los_que_faltan_y_buscar_en_carpeta_los_reenlaza_en_un_paso() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        var prober = new FakeProber();
        (EditorSession session, string projectPath) = await SavedProjectAsync(dir, prober);
        string moved = Path.Combine(dir.Path, "movido", "material");
        Directory.CreateDirectory(moved);
        File.WriteAllBytes(Path.Combine(moved, "toma.mp4"), [1, 2, 3]);
        prober.Video(Path.Combine(moved, "toma.mp4"), 20);

        var service = new MediaRelinkService(prober);
        IReadOnlyList<MediaRef>? shown = null;
        service.Attach(session, missing =>
        {
            shown = missing;
            return Task.CompletedTask;
        });
        Assert.True(await session.OpenProjectAsync(projectPath));
        for (int i = 0; i < 100 && shown is null; i++)
        {
            await Task.Delay(20);
        }

        Assert.NotNull(shown);
        MediaRef media = Assert.Single(shown);
        string clipsBefore = ProjectSerializer.Serialize(new Project { Id = "p", Name = "p", Sequences = session.Project.Clone().Sequences });

        RelinkSearchResult result = await service.SearchAsync(session.Project, shown, Path.Combine(dir.Path, "movido"));
        Assert.True(MediaRelinkService.Apply(session, result.Targets));

        Assert.Equal(Path.Combine(moved, "toma.mp4"), session.Project.GetMedia(media.Id).Path);
        Assert.Equal(clipsBefore, ProjectSerializer.Serialize(new Project { Id = "p", Name = "p", Sequences = session.Project.Clone().Sequences }));
        Assert.Empty(await service.FindMissingAsync(session.Project));
        Assert.Equal("Volver a enlazar medio", session.UndoDescription);

        Assert.True(session.Undo());
        Assert.Equal(dir.File("toma.mp4"), session.Project.GetMedia(media.Id).Path);
    });

    [Fact]
    public void Un_proyecto_sin_medios_que_falten_no_muestra_nada() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        var prober = new FakeProber();
        (EditorSession session, string projectPath) = await SavedProjectAsync(dir, prober);
        File.WriteAllBytes(dir.File("toma.mp4"), [1]);
        var service = new MediaRelinkService(prober);
        bool shown = false;

        Assert.True(await session.OpenProjectAsync(projectPath));
        Assert.Empty(await service.CheckOpenedAsync(session, _ =>
        {
            shown = true;
            return Task.CompletedTask;
        }));
        Assert.False(shown);
    });

    [Fact]
    public void Reenlazar_a_mano_comprueba_el_archivo() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        var prober = new FakeProber();
        (EditorSession session, _) = await SavedProjectAsync(dir, prober);
        MediaRef media = session.Project.Media[0];
        prober.Video(dir.File("corto.mp4"), 5).Video(dir.File("largo.mp4"), 60, 3840, 2160);
        var service = new MediaRelinkService(prober);

        (MediaRelinkTarget? none, string? problem) = await service.CheckFileAsync(session.Project, media, dir.File("corto.mp4"));
        Assert.Null(none);
        Assert.Contains("usan hasta", problem, StringComparison.Ordinal);

        (_, string? unreadable) = await service.CheckFileAsync(session.Project, media, dir.File("nada.txt"));
        Assert.Contains("no se puede leer", unreadable, StringComparison.Ordinal);

        (MediaRelinkTarget? target, string? ok) = await service.CheckFileAsync(session.Project, media, dir.File("largo.mp4"));
        Assert.Null(ok);
        Assert.NotNull(target?.Probe);
        Assert.True(MediaRelinkService.Apply(session, [target!]));
        Assert.Equal(3840, session.Project.GetMedia(media.Id).Width);
        Assert.False(MediaRelinkService.Apply(session, []));
    });

    [Fact]
    public void El_aviso_nombra_el_archivo_o_cuenta_los_que_faltan()
    {
        Assert.Contains("«toma.mp4»", MediaRelinkService.Describe([new MediaRef { Path = @"C:\v\toma.mp4" }]), StringComparison.Ordinal);
        Assert.Contains("3 medios", MediaRelinkService.Describe([new MediaRef(), new MediaRef(), new MediaRef()]), StringComparison.Ordinal);
    }
}
