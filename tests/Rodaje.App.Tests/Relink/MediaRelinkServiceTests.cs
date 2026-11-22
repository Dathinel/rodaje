using Rodaje.App.Services;
using Rodaje.App.Services.Collab;
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

    [Fact]
    public void Al_abrir_la_copia_de_una_sala_no_sale_el_segundo_dialogo() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        var prober = new FakeProber();
        (EditorSession session, string projectPath) = await SavedProjectAsync(dir, prober);
        var service = new MediaRelinkService(prober);
        bool shown = false;
        service.Attach(session, _ =>
        {
            shown = true;
            return Task.CompletedTask;
        });

        // El proyecto de la sala lo abre «Busca tus archivos»: el aviso de medios que faltan ya se resolvió ahí.
        using (service.SkipCheckWhileOpening(projectPath))
        {
            Assert.True(await session.OpenProjectAsync(projectPath));
        }

        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(20);
        }

        Assert.False(shown);
    });

    [Fact]
    public void La_marca_solo_salta_la_apertura_de_ese_archivo() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        var prober = new FakeProber();
        (EditorSession session, string projectPath) = await SavedProjectAsync(dir, prober);
        Assert.True(await session.OpenProjectAsync(projectPath));
        var service = new MediaRelinkService(prober);
        bool shown = false;

        using (service.SkipCheckWhileOpening(dir.File("otra-copia.rodaje")))
        {
            Assert.Single(await service.CheckOpenedAsync(session, _ =>
            {
                shown = true;
                return Task.CompletedTask;
            }));
            Assert.True(shown);
        }

        // Y fuera de la marca, la misma apertura sí enseña lo que falta.
        shown = false;
        Assert.Single(await service.CheckOpenedAsync(session, _ =>
        {
            shown = true;
            return Task.CompletedTask;
        }));
        Assert.True(shown);
    });

    [Fact]
    public void El_medio_que_falta_se_enseña_por_su_nombre_no_por_el_id()
    {
        var media = new MediaRef { Id = "media-f9149496-1111-2222-3333", Name = "Cumpleaños.mp4", Path = @"medios\media-f9149496-1111-2222-3333" };
        Assert.Equal("Cumpleaños.mp4", MediaRelinkService.DisplayName(media));
        Assert.Null(MediaRelinkService.DisplayPath(media));

        var sinNombre = new MediaRef { Id = "media-f9149496-1111-2222-3333", Path = @"medios\media-f9149496-1111-2222-3333" };
        Assert.Equal(MediaRelinkService.UnnamedMedia, MediaRelinkService.DisplayName(sinNombre));
        Assert.Null(MediaRelinkService.DisplayPath(sinNombre));
        Assert.Contains(MediaRelinkService.UnnamedMedia, MediaRelinkService.Describe([sinNombre]), StringComparison.Ordinal);

        var normal = new MediaRef { Path = @"C:\v\toma.mp4" };
        Assert.Equal("toma.mp4", MediaRelinkService.DisplayName(normal));
        Assert.Equal(@"C:\v\toma.mp4", MediaRelinkService.DisplayPath(normal));
    }

    [Fact]
    public void El_medio_sin_nombre_hereda_el_del_manifiesto_de_la_sala()
    {
        var project = new Project();
        project.Media.Add(new MediaRef { Id = "m1" });
        project.Media.Add(new MediaRef { Id = "m2", Name = "toma.mp4" });

        MediaPlaceholders.FillNames(project,
            [new MediaManifestEntry("m1", "Cumpleaños.mp4", 1, new string('a', 64)),
             new MediaManifestEntry("m2", "otro.mp4", 1, new string('b', 64)),
             new MediaManifestEntry("m3", "sobra.mp4", 1, new string('c', 64))]);

        Assert.Equal("Cumpleaños.mp4", project.Media[0].Name);
        Assert.Equal("toma.mp4", project.Media[1].Name);
        Assert.Equal("Cumpleaños.mp4", MediaRelinkService.DisplayName(project.Media[0]));
    }

    [Fact]
    public void La_ruta_de_relleno_de_la_sala_se_reconoce_y_la_normal_no()
    {
        var media = new MediaRef { Id = "media-abc-123" };
        media.Path = MediaPlaceholders.PlaceholderFor(media, 0);
        Assert.True(MediaPlaceholders.IsPlaceholder(media));

        // Ya resuelta contra la carpeta del proyecto también lo es.
        media.Path = Path.Combine("sala", "medios", "media-abc-123");
        Assert.True(MediaPlaceholders.IsPlaceholder(media));

        media.Path = Path.Combine("C:\\videos", "media-abc-123");
        Assert.False(MediaPlaceholders.IsPlaceholder(media));
        media.Path = Path.Combine("C:\\medios", "otro.mp4");
        Assert.False(MediaPlaceholders.IsPlaceholder(media));

        // Sin caracteres válidos en el id, el relleno es «m» + posición.
        var sinId = new MediaRef { Id = "***", Path = Path.Combine("medios", "m4") };
        Assert.True(MediaPlaceholders.IsPlaceholder(sinId));
    }
}
