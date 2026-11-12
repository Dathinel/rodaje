using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Tests.Session;

/// <summary>
/// Tarea 288 (revisión de #245): el aviso al abrir un proyecto acotado decía en una línea genérica «N valores (posición,
/// ancla, giro, tasa de cuadros o clips más allá del final)» sin decir cuáles. Ahora dice qué clase se acotó y cuántos de
/// cada una, y nombra el clip quitado cuando es uno solo (un clip con velocidad que pasa del final se quita, no se recorta).
/// </summary>
public class ClampedNoticeKindsSessionTests
{
    [Fact]
    public void Al_abrir_el_aviso_dice_la_tasa_el_clip_recortado_el_clip_quitado_por_su_nombre_y_los_valores() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string path = dir.File("viejo" + ProjectSerializer.FileExtension);
        Project legacy = Legacy(new Rational(1, 60));
        Track track = legacy.Sequences[0].VideoTracks[0];
        track.Clips.Add(new Clip { Id = "largo", MediaId = "m1", Start = Time.FromSeconds(10), SourceOut = Sequence.MaxTime });
        track.Clips.Add(new Clip
        {
            Id = "rapido", MediaId = "m2", Start = Sequence.MaxTime - Time.FromSeconds(1), SourceOut = Time.FromSeconds(10),
            Speed = new ClipSpeed { Rate = new Rational(2, 1) }, Duration = Time.FromSeconds(5),
        });
        track.Clips[0].Effects.Add(Rotation(1e9, -1e9));
        File.WriteAllText(path, ProjectSerializer.Serialize(legacy));

        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);

        Assert.True(await session.OpenProjectAsync(path));

        EditorNotification notice = Assert.Single(notices);
        Assert.Equal(NotificationSeverity.Warning, notice.Severity);
        Assert.Equal(
            "Al abrir, la tasa de fotogramas de 1 secuencia se llevó al rango de 1/5 a 1000 fps, " +
            "1 clip que pasaba del final de la línea de tiempo se recortó, " +
            "el clip «rápido.mp4», que pasaba del final de la línea de tiempo, se quitó " +
            "y 2 valores de efecto (posición, ancla o rotación) se acotaron al límite. Guarda el proyecto para conservar el cambio.",
            notice.Message);
        Assert.DoesNotContain('\n', notice.Message);
        Assert.Equal(["c1", "largo"], session.Sequence.VideoTracks[0].Clips.Select(c => c.Id));
        Assert.True(session.IsDirty);
    });

    [Fact]
    public void Con_varios_clips_quitados_los_cuenta_sin_nombrarlos() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string path = dir.File("viejo" + ProjectSerializer.FileExtension);
        Project legacy = Legacy(Rational.Fps25);
        Track track = legacy.Sequences[0].VideoTracks[0];
        track.Clips.Add(new Clip { Id = "lejos1", MediaId = "m2", Start = Sequence.MaxTime + Time.FromSeconds(1), SourceOut = Time.FromSeconds(1) });
        track.Clips.Add(new Clip { Id = "lejos2", MediaId = "m2", Start = Sequence.MaxTime + Time.FromSeconds(5), SourceOut = Time.FromSeconds(1) });
        File.WriteAllText(path, ProjectSerializer.Serialize(legacy));

        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);

        Assert.True(await session.OpenProjectAsync(path));

        Assert.Equal(
            "Al abrir, 2 clips que pasaban del final de la línea de tiempo se quitaron. Guarda el proyecto para conservar el cambio.",
            Assert.Single(notices).Message);
    });

    [Fact]
    public void Cada_clase_tiene_su_singular_y_su_plural()
    {
        var project = new Project();
        Assert.Equal(
            "Al abrir, las tasas de fotogramas de 2 secuencias se llevaron al rango de 1/5 a 1000 fps. Guarda el proyecto para conservar el cambio.",
            EditorSession.ClampedNotice(project, new ClampSummary(2, 0, [], 0)));
        Assert.Equal(
            "Al abrir, 3 clips que pasaban del final de la línea de tiempo se recortaron y 1 valor de efecto (posición, ancla o rotación) " +
            "se acotó al límite. Guarda el proyecto para conservar el cambio.",
            EditorSession.ClampedNotice(project, new ClampSummary(0, 3, [], 1)));

        // Un clip quitado cuyo medio ya no está en el proyecto: se cuenta sin nombre.
        Assert.Equal(
            "Al abrir, 1 clip que pasaba del final de la línea de tiempo se quitó. Guarda el proyecto para conservar el cambio.",
            EditorSession.ClampedNotice(project, new ClampSummary(0, 0, [new Clip { Id = "x", MediaId = "falta" }], 0)));
    }

    private static EffectInstance Rotation(params double[] values)
    {
        var transform = new EffectInstance { Id = "fx", Type = BuiltInEffects.Transform };
        transform.Parameters[T.Rotation] = new Parameter
        {
            Value = 0,
            Keyframes = [.. values.Select((v, i) => new Keyframe(Time.FromSeconds(0.1 * (i + 1)), v))],
        };
        return transform;
    }

    private static Project Legacy(Rational rate) => new()
    {
        Id = "p1",
        Name = "Proyecto de la v1.0.0",
        Media =
        [
            new MediaRef
            {
                Id = "m1", Path = Path.Combine(Path.GetTempPath(), "a.mp4"), Name = "a.mp4", Duration = Time.FromTicks(long.MaxValue),
                HasVideo = true, FrameRate = Rational.Fps30, Width = 1920, Height = 1080,
            },
            new MediaRef
            {
                Id = "m2", Path = Path.Combine(Path.GetTempPath(), "rápido.mp4"), Name = "rápido.mp4", Duration = Time.FromSeconds(60),
                HasVideo = true, FrameRate = Rational.Fps30, Width = 1920, Height = 1080,
            },
        ],
        Sequences =
        [
            new Sequence
            {
                Id = "s1", FrameRate = rate,
                VideoTracks = [new Track { Id = "v1", Kind = TrackKind.Video, Clips = [new Clip { Id = "c1", MediaId = "m1", SourceOut = Time.FromSeconds(1) }] }],
            },
        ],
    };
}
