using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Session;

/// <summary>
/// Deshacer y rehacer tras «Guardar como» en otra carpeta (tarea 236). #232 recalculaba en memoria las referencias
/// <c>file:</c> del proyecto, pero no las que guarda la pila de deshacer: rehacer «Quitar fondo» volvía a poner
/// <c>file:máscaras/…</c> relativa a la carpeta vieja y el Ctrl+S siguiente la resolvía contra la nueva.
/// </summary>
public class SaveAsUndoFileReferencesTests
{
    [Fact]
    public void Deshacer_y_rehacer_tras_guardar_como_en_otra_carpeta_no_estropea_la_mascara() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string folderA = dir.File("A");
        string folderB = dir.File("B");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        string video = Path.Combine(folderA, "toma.mp4");
        string matte = Path.Combine(folderA, "máscaras", "fondo 🎭.mp4");
        var prober = new FakeProber();
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        prober.Video(video, 10);
        ImportResult imported = await session.ImportMediaAsync([video]);
        Assert.True(session.AppendToTimeline(imported.MediaIds[0]));
        Assert.True(await session.SaveAsAsync(Path.Combine(folderA, "corto")));

        // Como VisionDialogs con el proyecto ya guardado en A.
        string clipId = session.Sequence.VideoTracks[0].Clips[0].Id;
        Assert.True(session.Execute(Edits.SetImageMask(session.SequenceId, clipId, LutReference.ForFile(matte), Time.Zero)));
        Assert.True(await session.SaveAsAsync(Path.Combine(folderB, "corto")));

        Assert.True(session.Undo());
        Assert.DoesNotContain(session.Sequence.VideoTracks[0].Clips[0].Effects, e => e.Type == BuiltInEffects.Mask);
        Assert.True(session.Redo());

        // Lo que vuelve de la pila de deshacer se resuelve igual en la carpeta nueva.
        Assert.Equal(matte, LutReference.ResolveFile(Matte(session.Project), folderB));
        Assert.True(await session.SaveAsync());

        var other = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        Assert.True(await other.OpenProjectAsync(Path.Combine(folderB, "corto.rodaje")));

        // Antes se escribía «file:máscaras/fondo 🎭.mp4» en B: apuntaba a «B/máscaras», donde no hay nada.
        Assert.Equal(matte, LutReference.ResolveFile(Matte(other.Project), folderB));

        // En el archivo sigue relativa a su carpeta, para poder mover la carpeta entera.
        Project written = ProjectSerializer.Load(Path.Combine(folderB, "corto.rodaje"));
        Assert.Equal("file:../A/máscaras/fondo 🎭.mp4", Matte(written));
    });

    [Fact]
    public void Deshacer_tras_guardar_como_devuelve_la_mascara_anterior_resoluble() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string folderA = dir.File("A");
        string folderB = dir.File(Path.Combine("B", "dentro"));
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        string video = Path.Combine(folderA, "toma.mp4");
        string first = Path.Combine(folderA, "máscaras", "uno.mp4");
        string second = Path.Combine(folderA, "máscaras", "dos.mp4");
        var prober = new FakeProber();
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        prober.Video(video, 10);
        ImportResult imported = await session.ImportMediaAsync([video]);
        Assert.True(session.AppendToTimeline(imported.MediaIds[0]));
        Assert.True(await session.SaveAsAsync(Path.Combine(folderA, "corto")));
        string clipId = session.Sequence.VideoTracks[0].Clips[0].Id;
        Assert.True(session.Execute(Edits.SetImageMask(session.SequenceId, clipId, LutReference.ForFile(first), Time.Zero)));
        Assert.True(session.Execute(Edits.SetImageMask(session.SequenceId, clipId, LutReference.ForFile(second), Time.Zero)));
        Assert.True(await session.SaveAsAsync(Path.Combine(folderB, "corto")));

        // Deshacer devuelve el valor de antes del «Guardar como», que no pasó por ningún recálculo.
        Assert.True(session.Undo());
        Assert.Equal(first, LutReference.ResolveFile(Matte(session.Project), folderB));
        Assert.True(await session.SaveAsync());

        Project reopened = ProjectSerializer.Load(Path.Combine(folderB, "corto.rodaje"));
        ProjectPaths.MakeAbsolute(reopened, folderB);
        Assert.Equal(first, LutReference.ResolveFile(Matte(reopened), folderB));
        Assert.Equal(ProjectSerializer.Serialize(session.Project), ProjectSerializer.Serialize(reopened));
    });

    private static string Matte(Project project) =>
        project.Sequences[0].VideoTracks[0].Clips[0].Effects.Single(e => e.Type == BuiltInEffects.Mask).Options[BuiltInEffects.MaskOptions.Matte];
}
