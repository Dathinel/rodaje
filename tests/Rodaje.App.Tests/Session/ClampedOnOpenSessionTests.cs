using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Tests.Session;

/// <summary>
/// Tarea 240: un proyecto de la v1.0.0 con un giro, una posición o un ancla más allá del límite de hoy se acotaba al
/// abrir sin decir nada, y el proyecto quedaba «Guardado» aunque ya no era igual al archivo. Ahora avisa con la cuenta
/// en una línea y queda con cambios hasta guardarlo.
/// </summary>
public class ClampedOnOpenSessionTests
{
    [Fact]
    public void Un_keyframe_fuera_de_rango_abre_avisa_con_la_cuenta_y_queda_modificado() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string path = dir.File("viejo" + ProjectSerializer.FileExtension);
        ProjectSerializer.Save(WithRotationKeyframe(1e9), path);

        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);

        Assert.True(await session.OpenProjectAsync(path));

        Assert.Equal(EffectRegistry.MaxRotation, Rotation(session.Project).Keyframes[1].Value);
        Assert.True(session.IsDirty);
        Assert.Equal("Sin guardar", session.SaveStateText);
        EditorNotification notice = Assert.Single(notices);
        Assert.Equal(NotificationSeverity.Warning, notice.Severity);
        Assert.Equal(EditorSession.ClampedNotice(session.Project, new ClampSummary(0, 0, [], 1)), notice.Message);
        Assert.Contains("1 valor de efecto", notice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', notice.Message);
        Assert.False(session.CanUndo);

        // Al guardar queda limpio, y el archivo ya trae el valor acotado: abrirlo otra vez no avisa.
        Assert.True(await session.SaveAsync());
        Assert.False(session.IsDirty);

        var again = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        var later = new List<EditorNotification>();
        again.Notified += (_, n) => later.Add(n);
        Assert.True(await again.OpenProjectAsync(path));
        Assert.False(again.IsDirty);
        Assert.Empty(later);
    });

    [Fact]
    public void Un_proyecto_en_rango_abre_guardado_y_sin_aviso() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string path = dir.File("normal" + ProjectSerializer.FileExtension);
        ProjectSerializer.Save(WithRotationKeyframe(90), path);

        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);

        Assert.True(await session.OpenProjectAsync(path));

        Assert.False(session.IsDirty);
        Assert.Equal("Guardado", session.SaveStateText);
        Assert.Empty(notices);
    });

    [Fact]
    public void El_aviso_cuenta_en_plural()
    {
        string notice = EditorSession.ClampedNotice(new Project(), new ClampSummary(0, 0, [], 3));
        Assert.Contains("3 valores", notice, StringComparison.Ordinal);
        Assert.Contains("se acotaron", notice, StringComparison.Ordinal);
    }

    private static Project WithRotationKeyframe(double value)
    {
        var clip = new Clip { Id = "c1", MediaId = "m1", SourceOut = Time.FromSeconds(1) };
        var transform = new EffectInstance { Id = "fx", Type = BuiltInEffects.Transform };
        transform.Parameters[T.Rotation] = new Parameter
        {
            Value = 0,
            Keyframes = [new Keyframe(Time.Zero, 0), new Keyframe(Time.FromSeconds(0.5), value)],
        };
        clip.Effects.Add(transform);
        return new Project
        {
            Id = "p1",
            Name = "Proyecto de la v1.0.0",
            Media =
            [
                new MediaRef
                {
                    Id = "m1", Path = Path.Combine(Path.GetTempPath(), "a.mp4"), Name = "a", Duration = Time.FromSeconds(60),
                    HasVideo = true, FrameRate = Rational.Fps30, Width = 1920, Height = 1080,
                },
            ],
            Sequences = [new Sequence { Id = "s1", VideoTracks = [new Track { Id = "v1", Kind = TrackKind.Video, Clips = [clip] }] }],
        };
    }

    private static Parameter Rotation(Project project) =>
        project.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters[T.Rotation];
}
