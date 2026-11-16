using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Transcript;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Transcription;

public sealed class TranscriptPanelModelTests
{
    private static Time S(double value) => Time.FromSeconds(value);

    private static Project Talk()
    {
        string[] words = ["Hola", "eh", "bueno", "hola", "fin"];
        return new Project
        {
            Id = "p", Name = "Prueba",
            Media = [new MediaRef { Id = "m", Path = @"C:\prueba.wav", Duration = S(6), HasAudio = true, SampleRate = 48000, AudioChannels = 2 }],
            Sequences = [new Sequence
            {
                Id = "s", Name = "Secuencia", FrameRate = Rational.Fps30,
                AudioTracks = [new Track { Id = "a", Name = "Voz", Kind = TrackKind.Audio,
                    Clips = [new Clip { Id = "c", MediaId = "m", SourceOut = S(6) }] }],
            }],
            Transcripts = [new MediaTranscript
            {
                Id = "t", MediaId = "m", Language = "es", Model = "prueba", MediaDuration = S(6),
                Words = [.. words.Select((text, i) => new SpokenWord { Text = text, Start = S(i), End = S(i + 0.2), Confidence = 1 })],
            }],
        };
    }

    private static TranscriptPanelModel Model(Project project)
    {
        var model = new TranscriptPanelModel();
        Assert.True(model.Rebuild(project, project.Sequences[0]));
        return model;
    }

    [Fact]
    public void SessionRebuildsAfterCommandsOnTheSameSequenceAndSelectsTheWordClips() => UiThread.Run(() =>
    {
        Project sample = Talk();
        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs());
        Assert.True(session.Execute(new AddMediaCommand(sample.Media[0])));
        Assert.True(session.Execute(Edits.InsertMedia(session.SequenceId, "m", Time.Zero, S(6), session.Sequence.AudioTracks[0].Id, Time.Zero)));
        Assert.True(session.Execute(new SetTranscriptCommand("m", sample.Transcripts[0])));
        var model = new TranscriptPanelModel();
        Assert.True(session.RebuildTranscript(model));
        Assert.False(session.RebuildTranscript(model));
        model.SelectWord(1);
        session.SelectTranscriptWords(model);
        Assert.Contains(model.Document.Words[1].ClipId, session.SelectedClipIds);
        Sequence sameSequence = session.Sequence;
        Assert.True(session.ExecuteTranscript(model.DeleteSelection(true)));
        Assert.Same(sameSequence, session.Sequence);
        Assert.True(session.RebuildTranscript(model));
        Assert.Equal(["Hola", "bueno", "hola", "fin"], model.Document.Words.Select(w => w.Text));
        session.Undo();
        Assert.True(session.RebuildTranscript(model));
        Assert.Equal(5, model.Document.Words.Count);
        Assert.False(session.RebuildTranscript(model));
    });

    [Fact]
    public void ClickShiftAndExtensionKeepTheAnchorAndAllowCrossingIt()
    {
        TranscriptPanelModel model = Model(Talk());
        model.SelectWord(2);
        Assert.Equal([2], model.SelectedWords);
        model.SelectWord(4, extend: true);
        Assert.Equal([2, 3, 4], model.SelectedWords);
        model.ExtendSelection(-1);
        Assert.Equal([2, 3], model.SelectedWords);
        model.SelectWord(0, extend: true);
        Assert.Equal([0, 1, 2], model.SelectedWords);
        model.SelectWord(3);
        Assert.Equal([3], model.SelectedWords);
        model.SelectAll();
        Assert.Equal([0, 1, 2, 3, 4], model.SelectedWords);
        model.ExtendSelection(1);
        Assert.Equal(5, model.SelectionCount);
    }

    [Fact]
    public void EmptyDocumentIgnoresSelectionAndCommands()
    {
        var model = new TranscriptPanelModel();
        model.SelectAll();
        model.SelectWord(0);
        model.ExtendSelection(1);
        Assert.Empty(model.SelectedWords);
        Assert.Null(model.DeleteSelection(true));
        Assert.Null(model.Paste(0));
        Assert.False(model.FindNext());
        Assert.False(model.UpdateActiveWord(Time.Zero));
    }

    [Fact]
    public void SearchNextAndPreviousWrapAndResetForANewQuery()
    {
        TranscriptPanelModel model = Model(Talk());
        model.Find("HÓLA");
        Assert.Equal(2, model.Matches.Count);
        Assert.True(model.FindNext());
        Assert.Equal(0, model.SelectionFirst);
        Assert.True(model.FindNext());
        Assert.Equal(3, model.SelectionFirst);
        Assert.True(model.FindNext());
        Assert.Equal(0, model.SelectionFirst);
        Assert.True(model.FindNext(previous: true));
        Assert.Equal(3, model.SelectionFirst);
        model.Find("hola");
        Assert.True(model.FindNext(previous: true));
        Assert.Equal(3, model.SelectionFirst);
        model.Find("eh bueno");
        Assert.True(model.FindNext());
        Assert.Equal([1, 2], model.SelectedWords);
        model.Find("ausente");
        Assert.False(model.FindNext());
    }

    [Fact]
    public void ActiveWordOnlyReportsTransitionsIncludingBackwards()
    {
        TranscriptPanelModel model = Model(Talk());
        Assert.True(model.UpdateActiveWord(S(0)));
        Assert.Equal(0, model.ActiveWord);
        Assert.False(model.UpdateActiveWord(S(0.05)));
        Assert.False(model.UpdateActiveWord(S(0.9)));
        Assert.True(model.UpdateActiveWord(S(1)));
        Assert.Equal(1, model.ActiveWord);
        Assert.False(model.UpdateActiveWord(S(1.1)));
        Assert.True(model.UpdateActiveWord(S(0.1)));
        Assert.Equal(0, model.ActiveWord);
    }

    [Fact]
    public void SafeFillersAreCheckedDoubtfulOnesAreNotAndPausesAreChecked()
    {
        TranscriptPanelModel model = Model(Talk());
        Assert.Collection(model.Fillers,
            f => { Assert.Equal(1, f.Word); Assert.True(f.Safe); Assert.True(f.IsChecked); },
            f => { Assert.Equal(2, f.Word); Assert.False(f.Safe); Assert.False(f.IsChecked); });
        Assert.Equal(4, model.Pauses.Count);
        Assert.All(model.Pauses, p => Assert.True(p.IsChecked));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeleteCommandRemovesExactlyTheSelectionAndUndoRestoresIt(bool ripple)
    {
        Project project = Talk();
        TranscriptPanelModel model = Model(project);
        Time duration = project.Sequences[0].Duration;
        model.SelectWord(1);
        model.SelectWord(2, extend: true);
        ICommand command = Assert.IsAssignableFrom<ICommand>(model.DeleteSelection(ripple));
        command.Apply(project);
        TranscriptDocument document = TranscriptDocument.Build(project, project.Sequences[0]);
        Assert.Equal(["Hola", "hola", "fin"], document.Words.Select(w => w.Text));
        Assert.Equal(ripple, project.Sequences[0].Duration < duration);
        command.Revert(project);
        Assert.Equal(duration, project.Sequences[0].Duration);
        Assert.Equal(model.Document.Words, TranscriptDocument.Build(project, project.Sequences[0]).Words);
    }

    [Fact]
    public void FillerCommandUsesOnlyCheckedWords()
    {
        Project project = Talk();
        TranscriptPanelModel model = Model(project);
        model.Fillers[0].IsChecked = false;
        model.Fillers[1].IsChecked = true;
        Assert.IsAssignableFrom<ICommand>(model.RemoveFillers()).Apply(project);
        Assert.Equal(["Hola", "eh", "hola", "fin"], TranscriptDocument.Build(project, project.Sequences[0]).Words.Select(w => w.Text));
    }

    [Fact]
    public void PauseCommandUsesOnlyCheckedPausesAndKeepsAllWords()
    {
        Project project = Talk();
        TranscriptPanelModel model = Model(project);
        foreach (PauseChoice choice in model.Pauses) choice.IsChecked = false;
        Assert.Null(model.RemovePauses());
        model.Pauses[1].IsChecked = true;
        Assert.IsAssignableFrom<ICommand>(model.RemovePauses()).Apply(project);
        TranscriptDocument after = TranscriptDocument.Build(project, project.Sequences[0]);
        Assert.Equal(model.Document.Words.Select(w => w.Text), after.Words.Select(w => w.Text));
        Assert.Equal(3, TranscriptEdits.Pauses(after, TranscriptPanelModel.MinimumPause).Count);
        Assert.Equal(model.Document.Words[1].SeqStart, after.Words[1].SeqStart);
        Assert.True(after.Words[2].SeqStart < model.Document.Words[2].SeqStart);
    }

    [Fact]
    public void CutRemembersTheRangeWhileClickChoosesThePasteDestination()
    {
        Project project = Talk();
        TranscriptPanelModel model = Model(project);
        model.SelectWord(1);
        model.Cut();
        Assert.Null(model.Paste(1));
        Assert.Null(model.Paste(2));
        model.SelectWord(4);
        Assert.IsAssignableFrom<ICommand>(model.Paste(model.SelectionFirst)).Apply(project);
        Assert.Equal(["Hola", "bueno", "hola", "eh", "fin"], TranscriptDocument.Build(project, project.Sequences[0]).Words.Select(w => w.Text));
    }

    [Fact]
    public void RebuildCachesReferencesAndVersionButInvalidatesCutAndChoicesAfterEditing()
    {
        Project project = Talk();
        TranscriptPanelModel model = Model(project);
        TranscriptDocument original = model.Document;
        model.SelectWord(1);
        model.Cut();
        Assert.False(model.Rebuild(project, project.Sequences[0]));
        Assert.Same(original, model.Document);
        Assert.Equal(1, model.SelectionFirst);
        Assert.True(model.Rebuild(project, project.Sequences[0], version: 1));
        Assert.NotSame(original, model.Document);
        Assert.Null(model.Paste(4));
        Assert.True(model.Rebuild(project, project.Sequences[0], "a", version: 1));
        Assert.False(model.Rebuild(project, project.Sequences[0], "a", version: 1));
        project.Transcripts[0] = project.Transcripts[0].Clone();
        Assert.True(model.Rebuild(project, project.Sequences[0], "a", version: 1));
        project.Sequences[0] = project.Sequences[0].Clone();
        Assert.True(model.Rebuild(project, project.Sequences[0], "a", version: 1));
    }

    [Fact]
    public void PanelKeysAreFixedAndOnlyTheMenuActionIsInTheCatalog()
    {
        Assert.Contains(ShortcutCatalog.All, action => action.Id == TranscriptShortcuts.RemoveFillers);
        Assert.DoesNotContain(ShortcutCatalog.All, action => action.Id == TranscriptShortcuts.Delete);
        Assert.Contains((TranscriptShortcuts.Delete, KeyChord.Parse("Supr")), TranscriptShortcuts.PanelKeys);
        Assert.Contains((TranscriptShortcuts.Lift, KeyChord.Parse("Retroceso")), TranscriptShortcuts.PanelKeys);
        Assert.Contains((TranscriptShortcuts.ExtendRight, KeyChord.Parse("Ctrl+Mayús+Derecha")), TranscriptShortcuts.PanelKeys);
        Assert.Equal(TranscriptShortcuts.PanelKeys.Count, TranscriptShortcuts.PanelKeys.Select(k => k.Chord).Distinct().Count());
    }
}
