using Rodaje.App.Services;
using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Session;

/// <summary>R2 del repaso del 19-nov: «El lugar está ocupado por el clip «clip-d4b6…»» en vez de «voz».</summary>
public sealed class EditFailureNoticeNamesTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    [Fact]
    public void AClipIdInsideGuillemetsBecomesItsMediaName()
    {
        var project = new Project
        {
            Id = "p", Name = "Prueba",
            Media = [new MediaRef { Id = "m", Name = "voz", Path = "voz.wav", Duration = S(5), HasAudio = true }],
            Sequences = [new Sequence { Id = "s", Name = "Secuencia", AudioTracks = [new Track { Id = "a", Name = "Audio 1", Kind = TrackKind.Audio,
                Clips = [new Clip { Id = "clip-d4b6cf93", MediaId = "m", SourceOut = S(5) }] }] }],
        };

        Assert.Equal("El lugar está ocupado por el clip «voz» en la pista «Audio 1».",
            ClipLabels.NameClipsIn(project, "El lugar está ocupado por el clip «clip-d4b6cf93» en la pista «Audio 1»."));
        Assert.Equal("Sin clip «clip-no-existe».", ClipLabels.NameClipsIn(project, "Sin clip «clip-no-existe»."));
    }

    [Fact]
    public void TheWarningOfAnEditThatDoesNotFitSaysTheClipByName() => UiThread.Run(() =>
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);
        var media = new MediaRef { Id = "m", Name = "voz", Path = "voz.wav", Duration = S(5), HasAudio = true };
        Assert.True(session.Execute(new AddMediaCommand(media)));
        string track = session.Sequence.AudioTracks[0].Id;
        Assert.True(session.Execute(Edits.InsertMedia(session.SequenceId, "m", Time.Zero, S(5), track, Time.Zero)));

        Assert.False(session.Execute(Edits.InsertMedia(session.SequenceId, "m", Time.Zero, S(2), track, S(1), PlacementMode.Strict)));

        EditorNotification warning = Assert.Single(notices, n => n.Severity == NotificationSeverity.Warning);
        Assert.Contains("«voz»", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("clip-", warning.Message, StringComparison.Ordinal);
    });
}
