using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Session;

/// <summary>
/// Referencias <c>file:</c> en memoria tras «Guardar como» en otra carpeta (tarea 230). La vista previa las resuelve
/// contra la carpeta del proyecto (<see cref="AppEffectContent"/>): antes seguían relativas a la carpeta vieja y la
/// máscara o la LUT no aparecían hasta reabrir.
/// </summary>
public class SaveAsFileReferencesTests
{
    [Fact]
    public void Guardar_como_en_otra_carpeta_deja_la_mascara_resoluble_en_memoria() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string folderA = dir.File("A");
        string folderB = dir.File("B");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        string video = Path.Combine(folderA, "toma.mp4");
        string matte = Path.Combine(folderA, "máscaras", "fondo.mp4");
        var prober = new FakeProber();
        var playback = new FakePlayback();
        var session = new EditorSession(playback, prober, new FakeDialogs(), new SequentialIdGenerator());
        prober.Video(video, 10);
        ImportResult imported = await session.ImportMediaAsync([video]);
        Assert.True(session.AppendToTimeline(imported.MediaIds[0]));
        Assert.True(await session.SaveAsAsync(Path.Combine(folderA, "corto")));

        // Como VisionDialogs, con el proyecto ya guardado en A.
        string clipId = session.Sequence.VideoTracks[0].Clips[0].Id;
        Assert.True(session.Execute(Edits.SetImageMask(session.SequenceId, clipId, LutReference.ForFile(matte), Time.Zero)));
        Assert.True(await session.SaveAsAsync(Path.Combine(folderB, "corto")));

        // Antes quedaba «file:máscaras/fondo.mp4» y se resolvía contra B, donde no hay nada.
        string folder = Path.GetDirectoryName(session.FilePath)!;
        Assert.Equal(matte, LutReference.ResolveFile(Matte(session.Project), folder));

        // Lo que tiene el motor también, y en memoria queda lo mismo que en el archivo.
        Assert.Equal(matte, LutReference.ResolveFile(Matte(playback.Reloads[^1]), folder));
        string inMemory = ProjectSerializer.Serialize(session.Project);
        Assert.True(await session.OpenProjectAsync(session.FilePath));
        Assert.Equal(inMemory, ProjectSerializer.Serialize(session.Project));
    });

    [Fact]
    public void Guardar_como_en_otra_carpeta_y_luego_guardar_no_estropea_el_archivo() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string folderA = dir.File("A");
        string folderB = dir.File("B");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        string video = Path.Combine(folderA, "toma.mp4");
        string matte = Path.Combine(folderA, "mascaras", "fondo.mp4");
        var prober = new FakeProber();
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        prober.Video(video, 10);
        ImportResult imported = await session.ImportMediaAsync([video]);
        Assert.True(session.AppendToTimeline(imported.MediaIds[0]));
        Assert.True(await session.SaveAsAsync(Path.Combine(folderA, "corto")));
        string clipId = session.Sequence.VideoTracks[0].Clips[0].Id;
        Assert.True(session.Execute(Edits.SetImageMask(session.SequenceId, clipId, LutReference.ForFile(matte), Time.Zero)));
        Assert.True(await session.SaveAsAsync(Path.Combine(folderB, "corto")));

        // Ctrl+S: antes resolvía «mascaras/fondo.mp4» contra B (la carpeta nueva, también como «anterior») y escribía
        // una ruta a un archivo que no existe; el «Guardar como» había quedado bien y el guardado siguiente lo estropeaba.
        Assert.True(await session.SaveAsync());

        var other = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        Assert.True(await other.OpenProjectAsync(Path.Combine(folderB, "corto.rodaje")));
        Assert.Equal(matte, LutReference.ResolveFile(Matte(other.Project), folderB));
    });

    [Fact]
    public void Guardar_en_la_misma_carpeta_no_toca_las_referencias() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Path);
        string video = dir.File("toma.mp4");
        var prober = new FakeProber();
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        prober.Video(video, 10);
        ImportResult imported = await session.ImportMediaAsync([video]);
        Assert.True(session.AppendToTimeline(imported.MediaIds[0]));
        Assert.True(await session.SaveAsAsync(dir.File("corto")));
        string clipId = session.Sequence.VideoTracks[0].Clips[0].Id;
        Assert.True(session.Execute(Edits.SetImageMask(session.SequenceId, clipId, "file:./otra/../fondo.mp4", Time.Zero)));

        Assert.True(await session.SaveAsAsync(dir.File("otro")));

        // En la misma carpeta no hay nada que recalcular: ni siquiera se normaliza la ruta.
        Assert.Equal("file:./otra/../fondo.mp4", Matte(session.Project));
    });

    private static string Matte(Project project) =>
        project.Sequences[0].VideoTracks[0].Clips[0].Effects.Single(e => e.Type == BuiltInEffects.Mask).Options[BuiltInEffects.MaskOptions.Matte];
}
