using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;

namespace Rodaje.App.Tests.Session;

/// <summary>
/// La duración del visor (a la derecha del transporte) sigue a la secuencia al abrir y tras cada edición, deshacer y rehacer,
/// sin esperar a que el reproductor publique la suya: en pausa, con una secuencia de 59,94 fps, se quedaba en 0:00.000 hasta
/// darle a reproducir.
/// </summary>
public class ViewerDurationTests
{
    [Theory]
    [InlineData(60000, 1001)]
    [InlineData(30000, 1001)]
    [InlineData(30, 1)]
    public void Follows_the_sequence_without_the_player(long num, long den) => UiThread.Run(async () =>
    {
        var prober = new FakeProber().Video("vertical.mp4", 8, 1080, 2400, new Rational(num, den));
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        var changes = new List<string?>();
        session.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.Equal("0:00.000", session.DurationText);

        ImportResult result = await session.ImportMediaAsync(["vertical.mp4"]);
        Assert.True(session.AppendToTimeline(result.MediaIds[0]));
        string full = Timecode.Format(session.Sequence.Duration);
        Assert.NotEqual("0:00.000", full);
        Assert.Equal(full, session.DurationText);
        Assert.Contains(nameof(EditorSession.DurationText), changes);

        // El reproductor todavía sin la secuencia (duración 0) no la borra.
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(1) });
        Assert.Equal(full, session.DurationText);

        Assert.True(session.Undo());
        Assert.Equal("0:00.000", session.DurationText);
        Assert.True(session.Redo());
        Assert.Equal(full, session.DurationText);
    });
}
