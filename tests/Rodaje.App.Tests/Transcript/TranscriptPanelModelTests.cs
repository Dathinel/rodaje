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
    public void Corrections_capture_the_source_word_and_rebuild_search_and_paragraphs()
    {
        Project project = Talk();
        TranscriptPanelModel model = Model(project);
        model.SelectWord(1);
        ICommand correction = Assert.IsAssignableFrom<ICommand>(model.CorrectWord("una frase"));
        model.SelectWord(4);
        correction.Apply(project);
        Assert.True(model.Rebuild(project, project.Sequences[0]));
        Assert.Equal(["Hola", "una", "frase", "bueno", "hola", "fin"], model.Document.Words.Select(w => w.Text));
        model.Find("una frase");
        Assert.True(model.FindNext());
        Assert.Null(model.CorrectWord("dos"));
        model.SelectWord(2);
        Assert.IsAssignableFrom<ICommand>(model.SplitParagraph()).Apply(project);
        Assert.True(model.Rebuild(project, project.Sequences[0]));
        Assert.Contains(model.Document.Paragraphs, p => p.FirstWord == 2);
        model.SelectWord(2);
        Assert.IsAssignableFrom<ICommand>(model.CorrectWord("")).Apply(project);
        Assert.True(model.Rebuild(project, project.Sequences[0]));
        Assert.DoesNotContain(model.Document.Words, w => w.Text == "frase");
    }

    [Fact]
    public void Speaker_actions_change_source_words_and_expose_the_name_and_color_for_every_clip()
    {
        Project project = Talk();
        Clip repeat = project.Sequences[0].AudioTracks[0].Clips[0].Clone();
        repeat.Id = "repeat";
        repeat.Start = S(6);
        project.Sequences[0].AudioTracks[0].Clips.Add(repeat);
        TranscriptPanelModel model = Model(project);
        Assert.Null(model.AssignSpeaker(1));
        Assert.IsAssignableFrom<ICommand>(model.AddSpeaker("Ana", "#123456")).Apply(project);
        Assert.True(model.Rebuild(project, project.Sequences[0]));
        model.SelectWord(1);
        model.SelectWord(2, extend: true);
        Assert.Null(model.AssignSpeaker(2));
        Assert.Null(model.AssignSpeaker(-1));
        Assert.IsAssignableFrom<ICommand>(model.AssignSpeaker(1)).Apply(project);
        Assert.True(model.Rebuild(project, project.Sequences[0]));
        Assert.Equal(4, model.Document.Words.Count(w => w.SpeakerId is not null));
        DocParagraph[] paragraphs = [.. model.Document.Paragraphs.Where(p => p.SpeakerId is not null)];
        Assert.Equal(2, paragraphs.Length);
        Assert.All(paragraphs, p => Assert.Equal("Ana", model.SpeakerOf(p)?.Name));
        string id = model.Speakers[0].Id;
        Assert.IsAssignableFrom<ICommand>(model.UpdateSpeaker(id, "María", "#ABCDEF")).Apply(project);
        Assert.True(model.Rebuild(project, project.Sequences[0]));
        Assert.All(paragraphs, p => Assert.Equal(("María", "#ABCDEF"), (model.SpeakerOf(p)?.Name, model.SpeakerOf(p)?.Color)));
        model.SelectWord(6);
        Assert.IsAssignableFrom<ICommand>(model.AssignSpeaker(0)).Apply(project);
        Assert.True(model.Rebuild(project, project.Sequences[0]));
        Assert.Null(model.Document.Words[1].SpeakerId);
        Assert.Null(model.Document.Words[6].SpeakerId);
    }

    [Fact]
    public void Show_removed_preserves_document_indices_and_recovery_restores_duration_and_text()
    {
        Project project = Talk();
        TranscriptPanelModel model = Model(project);
        string[] before = [.. model.Document.Words.Select(w => w.Text)];
        Time duration = project.Sequences[0].Duration;
        Assert.False(model.ShowRemoved);
        Assert.Empty(model.RemovedWords);
        model.SelectWord(1);
        model.SelectWord(2, extend: true);
        Assert.IsAssignableFrom<ICommand>(model.DeleteSelection(true)).Apply(project);
        Assert.True(model.Rebuild(project, project.Sequences[0], version: 1));
        RemovedTranscriptSpan span = Assert.Single(model.RemovedWords);
        model.ShowRemoved = true;
        Assert.False(model.Rebuild(project, project.Sequences[0], version: 1));
        Assert.Equal(["Hola", "hola", "fin"], model.Document.Words.Select(w => w.Text));
        Assert.IsType<SequenceEditCommand>(model.Recover(span)).Apply(project);
        Assert.True(model.Rebuild(project, project.Sequences[0], version: 2));
        Assert.Equal(before, model.Document.Words.Select(w => w.Text));
        Assert.Equal(duration, project.Sequences[0].Duration);
        Assert.True(model.ShowRemoved);
        Assert.Empty(model.RemovedWords);
        Assert.Null(model.Recover(span));
    }

    [Fact]
    public void PanelKeysAreFixedAndOnlyTheMenuActionIsInTheCatalog()
    {
        Assert.Contains(ShortcutCatalog.All, action => action.Id == TranscriptShortcuts.RemoveFillers);
        Assert.DoesNotContain(ShortcutCatalog.All, action => action.Id == TranscriptShortcuts.Delete);
        Assert.Contains((TranscriptShortcuts.Delete, KeyChord.Parse("Supr")), TranscriptShortcuts.PanelKeys);
        Assert.Contains((TranscriptShortcuts.Lift, KeyChord.Parse("Retroceso")), TranscriptShortcuts.PanelKeys);
        Assert.Contains((TranscriptShortcuts.ExtendRight, KeyChord.Parse("Ctrl+Mayús+Derecha")), TranscriptShortcuts.PanelKeys);
        Assert.Contains((TranscriptShortcuts.CorrectWord, KeyChord.Parse("F2")), TranscriptShortcuts.PanelKeys);
        Assert.DoesNotContain(ShortcutCatalog.All, action => action.Id == TranscriptShortcuts.CorrectWord);
        for (int i = 0; i <= 9; i++)
        {
            Assert.Contains((TranscriptShortcuts.AssignSpeaker + i, KeyChord.Parse("Alt+" + i)), TranscriptShortcuts.PanelKeys);
            Assert.DoesNotContain(ShortcutCatalog.All, action => action.Id == TranscriptShortcuts.AssignSpeaker + i);
        }
        Assert.Equal(TranscriptShortcuts.PanelKeys.Count, TranscriptShortcuts.PanelKeys.Select(k => k.Chord).Distinct().Count());
    }

    [Fact]
    public void Without_selection_the_current_transcript_is_the_one_playing_and_a_mixed_selection_is_flagged()
    {
        Project project = Talk();
        project.Media.Add(new MediaRef { Id = "m2", Path = @"C:\otra.wav", Duration = S(6), HasAudio = true, SampleRate = 48000, AudioChannels = 2 });
        project.Sequences[0].AudioTracks[0].Clips.Add(new Clip { Id = "c2", MediaId = "m2", SourceOut = S(6), Start = S(6) });
        project.Transcripts.Add(new MediaTranscript
        {
            Id = "t2", MediaId = "m2", Language = "es", Model = "prueba", MediaDuration = S(6),
            Words = [new SpokenWord { Text = "otra", Start = S(1), End = S(1.2), Confidence = 1 }],
        });
        TranscriptPanelModel model = Model(project);

        model.UpdateActiveWord(S(7.5));
        Assert.Equal("t2", model.CurrentTranscript?.Id);

        model.SelectWord(4);
        model.SelectWord(5, extend: true);
        Assert.True(model.SelectionCrossesMedia);
        Assert.Null(model.AssignSpeaker(0));
    }

    [Fact]
    public void Removing_only_some_pauses_goes_through_the_core_and_keeps_the_others()
    {
        Project project = Talk();
        TranscriptPanelModel model = Model(project);
        Assert.Equal(4, model.Pauses.Count);
        foreach (PauseChoice pause in model.Pauses.Skip(1)) pause.IsChecked = false;

        ICommand command = model.RemovePauses()!;
        command.Apply(project);

        Assert.True(model.Rebuild(project, project.Sequences[0], version: 1));
        Assert.Equal(3, model.Pauses.Count);
        // La primera pausa (0,2–1 s) queda en lo que se deja, ajustado al cuadro: 0,6 s menos.
        Assert.Equal(S(5.4), project.Sequences[0].Duration);
    }
}
