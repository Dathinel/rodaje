using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;

namespace Rodaje.App.Tests.Session;

/// <summary>
/// Archivos sueltos (<c>file:</c>) que guarda el proyecto: la LUT de «Cargar .cube…», la imagen del barrido y el video
/// de una máscara de imagen (tarea 227). Antes, <see cref="ProjectPaths"/> solo volvía relativas las rutas de los medios.
/// </summary>
public class ProjectFileReferencesTests
{
    [Fact]
    public void Una_LUT_elegida_sin_guardar_se_guarda_relativa_y_sigue_a_la_carpeta()
    {
        using var dir = new TempDirectory();
        string lut = dir.File(Path.Combine("luts", "mi look ñ.cube"));
        Project project = WithLut(LutReference.ForFile(lut));

        Project copy = ProjectPaths.ForSaving(project, dir.Path);

        // Antes quedaba «file:C:/…/luts/mi look ñ.cube»: al mover la carpeta del proyecto, la LUT se perdía.
        Assert.Equal("file:luts/mi look ñ.cube", Lut(copy));
    }

    [Fact]
    public void Abrir_y_guardar_en_otra_carpeta_no_pierde_la_LUT()
    {
        using var dir = new TempDirectory();
        string folderA = dir.File("A");
        string folderB = dir.File("B");
        Project project = WithLut("file:luts/mi look.cube");

        ProjectPaths.MakeAbsolute(project, folderA);
        Project copy = ProjectPaths.ForSaving(project, folderB, currentDirectory: folderA);

        // Antes se guardaba «file:luts/mi look.cube» junto al proyecto nuevo, donde no hay ninguna LUT.
        Assert.Equal(Path.Combine(folderA, "luts", "mi look.cube"), LutReference.ResolveFile(Lut(copy), folderB));
    }

    [Fact]
    public void Guardar_como_en_otra_carpeta_recalcula_la_mascara_de_imagen() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string folderA = dir.File("A");
        string folderB = dir.File("B");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        string video = Path.Combine(folderA, "toma 🎬.mp4");
        string matte = Path.Combine(folderA, "máscaras", "fondo.mp4");
        var prober = new FakeProber();
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        prober.Video(video, 10);
        ImportResult imported = await session.ImportMediaAsync([video]);
        Assert.True(session.AppendToTimeline(imported.MediaIds[0]));
        Assert.True(await session.SaveAsAsync(Path.Combine(folderA, "corto")));

        // Como VisionDialogs: en memoria la referencia es absoluta aunque el proyecto ya esté guardado (tarea 236).
        string reference = LutReference.ForFile(matte);
        Assert.Equal("file:" + matte.Replace('\\', '/'), reference);
        string clipId = session.Sequence.VideoTracks[0].Clips[0].Id;
        Assert.True(session.Execute(Edits.SetImageMask(session.SequenceId, clipId, reference, Time.Zero)));
        Assert.True(await session.SaveAsAsync(Path.Combine(folderB, "corto")));

        var other = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        Assert.True(await other.OpenProjectAsync(Path.Combine(folderB, "corto.rodaje")));
        EffectInstance mask = other.Sequence.VideoTracks[0].Clips[0].Effects.Single(e => e.Type == BuiltInEffects.Mask);

        // Antes se guardaba «file:máscaras/fondo.mp4» en B y se resolvía contra B, donde no está.
        Assert.Equal(matte, LutReference.ResolveFile(mask.Options[BuiltInEffects.MaskOptions.Matte], folderB));
        Assert.Equal(video, other.Project.Media[0].Path);
    });

    [Theory]
    [InlineData("CON", "_CON.mp4")]
    [InlineData("aux", "_aux.mp4")]
    [InlineData("nul.final", "_nul.final.mp4")]
    [InlineData("Com1 ", "_Com1.mp4")]
    [InlineData("Consola", "Consola.mp4")]
    [InlineData("", "Exportación.mp4")]
    public void El_nombre_de_exportacion_por_omision_no_es_un_dispositivo(string project, string expected)
    {
        // Antes se proponía «CON.mp4»: Windows lo abre como la consola, no como un archivo.
        Assert.Equal(expected, ExportRanges.SuggestFileName(project, ExportPresets.YouTube1080p.Settings));
    }

    private static Project WithLut(string reference)
    {
        var clip = new Clip { Id = "c1", MediaId = "m1", SourceOut = Time.FromSeconds(1) };
        clip.Effects.Add(new EffectInstance { Id = "fx", Type = BuiltInEffects.Color, Options = { [BuiltInEffects.ColorOptions.Lut] = reference } });
        return new Project
        {
            Media = [new MediaRef { Id = "m1", Path = Path.Combine(Path.GetTempPath(), "a.mp4") }],
            Sequences = [new Sequence { Id = "s1", VideoTracks = [new Track { Id = "v1", Kind = TrackKind.Video, Clips = [clip] }] }],
        };
    }

    private static string Lut(Project project) => project.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Options[BuiltInEffects.ColorOptions.Lut];
}
