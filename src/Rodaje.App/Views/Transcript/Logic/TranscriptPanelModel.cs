using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Transcript;

public sealed class FillerChoice(int word, bool safe)
{
    public int Word { get; } = word;
    public bool Safe { get; } = safe;
    public bool IsChecked { get; set; } = safe;
}

public sealed class PauseChoice(string clipId, Time start, Time end)
{
    public string ClipId { get; } = clipId;
    public Time Start { get; } = start;
    public Time End { get; } = end;
    public bool IsChecked { get; set; } = true;
}

/// <summary>Selección y acciones del panel, sin controles ni dependencias de WinUI.</summary>
public sealed class TranscriptPanelModel
{
    private Project? _project;
    private Sequence? _sequence;
    private MediaTranscript[] _transcripts = [];
    private string? _guideTrackId;
    private long _version = -1;
    private int _anchor = -1;
    private int _caret = -1;
    private (int First, int Count)? _cut;
    private string _query = "";
    private int _match = -1;

    public static Time MinimumPause { get; } = Time.FromSeconds(0.6);
    public static Time KeepPause { get; } = Time.FromSeconds(0.15);
    public TranscriptDocument Document { get; private set; } = TranscriptDocument.Empty;
    public IReadOnlyList<FillerChoice> Fillers { get; private set; } = [];
    public IReadOnlyList<PauseChoice> Pauses { get; private set; } = [];
    public IReadOnlyList<(int First, int Count)> Matches { get; private set; } = [];
    public bool ShowRemoved { get; set; }
    public IReadOnlyList<RemovedTranscriptSpan> RemovedWords { get; private set; } = [];
    public MediaTranscript? CurrentTranscript => Document.Words.Count == 0 ? null
        : _transcripts.FirstOrDefault(t => t.Id == Document.Words[SelectionFirst >= 0 ? SelectionFirst : Math.Max(0, ActiveWord)].TranscriptId);
    public IReadOnlyList<TranscriptSpeaker> Speakers => CurrentTranscript?.Speakers ?? [];
    public int ActiveWord { get; private set; } = -1;
    public int SelectionFirst => _anchor < 0 ? -1 : Math.Min(_anchor, _caret);
    public int SelectionCount => _anchor < 0 ? 0 : Math.Abs(_caret - _anchor) + 1;
    public IEnumerable<int> SelectedWords => SelectionCount == 0 ? [] : Enumerable.Range(SelectionFirst, SelectionCount);

    // La sesión incrementa version incluso cuando un comando modifica la misma instancia de Sequence.
    public bool Rebuild(Project project, Sequence sequence, string? guideTrackId = null, long version = 0)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        if (ReferenceEquals(_project, project) && ReferenceEquals(_sequence, sequence) && _version == version
            && _guideTrackId == guideTrackId && _transcripts.SequenceEqual(project.Transcripts))
        {
            return false;
        }

        _project = project;
        _sequence = sequence;
        _version = version;
        _guideTrackId = guideTrackId;
        _transcripts = [.. project.Transcripts];
        Document = TranscriptDocument.Build(project, sequence, guideTrackId);
        RemovedWords = TranscriptEdits.RemovedWords(project, sequence, Document);
        _anchor = _caret = -1;
        _cut = null;
        ActiveWord = -1;
        var candidates = new SortedDictionary<int, bool>();
        foreach (string language in _transcripts.Select(t => t.Language).Distinct())
        {
            foreach ((int word, bool safe) in TranscriptEdits.FillerCandidates(Document, language))
            {
                if (_transcripts.Any(t => t.Id == Document.Words[word].TranscriptId && t.Language == language))
                {
                    candidates[word] = safe;
                }
            }
        }

        Fillers = [.. candidates.Select(p => new FillerChoice(p.Key, p.Value))];
        Pauses = [.. TranscriptEdits.Pauses(Document, MinimumPause).Select(p => new PauseChoice(p.ClipId, p.Start, p.End))];
        Find(_query);
        return true;
    }

    public bool IsSelected(int word) => SelectionFirst >= 0 && word >= SelectionFirst && word < SelectionFirst + SelectionCount;

    public void SelectWord(int word, bool extend = false)
    {
        if (word < 0 || word >= Document.Words.Count)
        {
            return;
        }

        if (!extend || _anchor < 0) _anchor = word;
        _caret = word;
    }

    public void ExtendSelection(int direction)
    {
        if (Document.Words.Count == 0) return;
        SelectWord(_caret < 0 ? 0 : Math.Clamp(_caret + Math.Sign(direction), 0, Document.Words.Count - 1), extend: true);
    }

    public void SelectAll()
    {
        if (Document.Words.Count == 0) return;
        _anchor = 0;
        _caret = Document.Words.Count - 1;
    }

    public void Find(string query)
    {
        _query = query;
        Matches = Document.Find(query);
        _match = -1;
    }

    public bool FindNext(bool previous = false)
    {
        if (Matches.Count == 0) return false;
        _match = _match < 0 ? (previous ? Matches.Count - 1 : 0)
            : (_match + (previous ? -1 : 1) + Matches.Count) % Matches.Count;
        (int first, int count) = Matches[_match];
        _anchor = first;
        _caret = first + count - 1;
        return true;
    }

    public bool UpdateActiveWord(Time position)
    {
        int next = Document.ActiveWord(position);
        if (next == ActiveWord) return false;
        ActiveWord = next;
        return true;
    }

    public ICommand? DeleteSelection(bool ripple) => _sequence is null || SelectionCount == 0 ? null
        : Edits.DeleteWords(_sequence.Id, Document.TrackId, SelectedWords, ripple, RippleScope.Track);

    public TranscriptSpeaker? SpeakerOf(DocParagraph paragraph) => _transcripts.FirstOrDefault(t =>
        t.Id == Document.Words[paragraph.FirstWord].TranscriptId)?.Speakers.Find(s => s.Id == paragraph.SpeakerId);

    public ICommand? CorrectWord(string replacement)
    {
        if (SelectionCount != 1 || CurrentTranscript is not { } transcript) return null;
        int index = Document.Words[SelectionFirst].WordIndex;
        return new EditTranscriptCommand(transcript.MediaId, t => TranscriptEdits.CorrectWord(t, index, replacement), "Corregir palabra");
    }

    public ICommand? SplitParagraph()
    {
        if (SelectionCount == 0 || CurrentTranscript is not { } transcript) return null;
        int index = Document.Words[SelectionFirst].WordIndex;
        return new EditTranscriptCommand(transcript.MediaId, t => TranscriptEdits.SplitParagraph(t, index), "Partir párrafo");
    }

    public ICommand? AddSpeaker(string name, string? color)
    {
        if (CurrentTranscript is not { } transcript) return null;
        var speaker = new TranscriptSpeaker { Id = Guid.NewGuid().ToString("N"), Name = name, Color = color };
        return new EditTranscriptCommand(transcript.MediaId, t => TranscriptEdits.AddSpeaker(t, speaker), "Añadir hablante");
    }

    public ICommand? UpdateSpeaker(string speakerId, string name, string? color) => CurrentTranscript is not { } transcript ? null
        : new EditTranscriptCommand(transcript.MediaId,
            t => TranscriptEdits.UpdateSpeaker(t, speakerId, name, color), "Editar hablante");

    /// <summary>Las palabras elegidas son de más de un medio: no hay un juego de hablantes común.</summary>
    public bool SelectionCrossesMedia => SelectedWords.Select(i => Document.Words[i].TranscriptId).Distinct().Skip(1).Any();

    public ICommand? AssignSpeaker(int number)
    {
        if (SelectionCount == 0 || CurrentTranscript is not { } transcript || number < 0
            || number > transcript.Speakers.Count) return null;
        DocWord[] selected = [.. SelectedWords.Select(i => Document.Words[i])];
        if (selected.Any(w => w.TranscriptId != transcript.Id)) return null;
        int[] indices = [.. selected.Select(w => w.WordIndex).Distinct()];
        string? speakerId = number == 0 ? null : transcript.Speakers[number - 1].Id;
        return new EditTranscriptCommand(transcript.MediaId, t =>
        {
            foreach (int index in indices) TranscriptEdits.AssignSpeaker(t, index, 1, speakerId);
            return t;
        }, number == 0 ? "Quitar hablante" : "Asignar hablante");
    }

    public ICommand? Recover(RemovedTranscriptSpan span)
    {
        if (_sequence is null || !RemovedWords.Contains(span)) return null;
        string? trackId = Document.TrackId;
        return new SequenceEditCommand("Recuperar", _sequence.Id, ctx => TranscriptEdits.RecoverWords(ctx,
            TranscriptDocument.Build(ctx.Project, ctx.Sequence, trackId), span));
    }

    public void Cut() => _cut = SelectionCount == 0 ? null : (SelectionFirst, SelectionCount);

    public ICommand? Paste(int before)
    {
        if (_sequence is null || _cut is not { } cut || before < 0 || before > Document.Words.Count
            || (before >= cut.First && before <= cut.First + cut.Count)) return null;
        return Edits.MoveWords(_sequence.Id, Document.TrackId, cut.First, cut.Count, before, RippleScope.Track);
    }

    public ICommand? RemoveFillers()
    {
        int[] words = [.. Fillers.Where(f => f.IsChecked).Select(f => f.Word)];
        return _sequence is null || words.Length == 0 ? null
            : Edits.RemoveFillers(_sequence.Id, Document.TrackId, words, ripple: true, RippleScope.Track);
    }

    public ICommand? RemovePauses()
    {
        PauseChoice[] chosen = [.. Pauses.Where(p => p.IsChecked)];
        if (_sequence is null || chosen.Length == 0) return null;
        if (chosen.Length == Pauses.Count)
            return Edits.RemovePauses(_sequence.Id, Document.TrackId, MinimumPause, KeepPause, ripple: true, RippleScope.Track);

        return Edits.RemovePauses(_sequence.Id, [.. chosen.Select(p => (p.ClipId, p.Start, p.End))], KeepPause, ripple: true, RippleScope.Track);
    }
}
