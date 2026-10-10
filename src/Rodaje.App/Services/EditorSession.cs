using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;

namespace Rodaje.App.Services;

/// <summary>Por qué cambió el proyecto.</summary>
public enum EditKind
{
    Execute,
    Undo,
    Redo,
    Jump,
    GestureCancelled,
}

public sealed class ProjectEditedEventArgs(EditKind kind, ICommand? command) : EventArgs
{
    public EditKind Kind { get; } = kind;

    /// <summary>El comando ejecutado, deshecho o rehecho; <c>null</c> en saltos del historial y en varios pasos juntos.</summary>
    public ICommand? Command { get; } = command;
}

/// <summary>Una fila del historial: el estado tras <see cref="Position"/> pasos aplicados.</summary>
/// <param name="Position">Lo que hay que pasar a <see cref="EditorSession.JumpToHistory"/> para volver a este estado.</param>
/// <param name="IsApplied">Está aplicado (no es un paso deshecho que se puede rehacer).</param>
/// <param name="IsCurrent">Es el estado actual.</param>
/// <param name="IsSaved">Coincide con lo guardado en el archivo.</param>
public sealed record HistoryEntry(int Position, string Description, bool IsApplied, bool IsCurrent, bool IsSaved);

/// <summary>
/// Única fuente de verdad del editor: el proyecto, la secuencia que se ve, el historial, la selección, el transporte y el
/// archivo. Vive en el hilo de la interfaz: todo cambio pasa por aquí, y cada cambio llega al reproductor como una copia
/// del proyecto, así el hilo de render nunca lee el modelo mientras se edita.
/// </summary>
/// <remarks>
/// Las vistas leen <see cref="Project"/> y <see cref="Sequence"/> pero no los modifican: cada cambio es un
/// <see cref="ICommand"/> que pasa por <see cref="Execute"/>, y los gestos largos (arrastrar un clip o un deslizador) se
/// envuelven en <see cref="BeginGesture"/> y <see cref="EndGesture"/> para deshacerse en un paso.
/// </remarks>
public sealed partial class EditorSession : ObservableObject
{
    public const string UntitledName = "Sin título";

    private readonly IPlaybackEngine _playback;
    private readonly IMediaProber _prober;
    private readonly IEditorDialogs _dialogs;
    private readonly IIdGenerator _ids;
    private readonly int _threadId;
    private List<string> _selection = [];
    private PlaybackSnapshot _transport = PlaybackSnapshot.Empty;
    private string _originDescription = "Proyecto nuevo";
    private string? _filePath;
    private int _busy;
    private long _editVersion;

    public EditorSession(IPlaybackEngine playback, IMediaProber prober, IEditorDialogs dialogs, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(prober);
        ArgumentNullException.ThrowIfNull(dialogs);
        _playback = playback;
        _prober = prober;
        _dialogs = dialogs;
        _ids = ids ?? GuidIdGenerator.Instance;
        _threadId = Environment.CurrentManagedThreadId;
        Project project = CreateEmptyProject();
        Project = project;
        SequenceId = project.Sequences[0].Id;
        History = new UndoStack(project);
        History.Changed += OnHistoryChanged;
        _playback.Load(project.Clone(), SequenceId);
    }

    /// <summary>Se abrió otro proyecto (nuevo o desde archivo): las vistas deben reconstruirse enteras.</summary>
    public event EventHandler? ProjectOpened;

    /// <summary>El proyecto cambió por un comando, deshacer, rehacer o un salto del historial.</summary>
    public event EventHandler<ProjectEditedEventArgs>? ProjectEdited;

    /// <summary>Cambió el historial o su punto guardado (también al guardar).</summary>
    public event EventHandler? HistoryChanged;

    public event EventHandler? SelectionChanged;

    /// <summary>Cambió la posición, la duración o el estado de reproducción que publica el reproductor.</summary>
    public event EventHandler? TransportChanged;

    /// <summary>Un aviso para la persona (errores al abrir, guardar, importar o editar).</summary>
    public event EventHandler<EditorNotification>? Notified;

    public Project Project { get; private set; }

    public string SequenceId { get; private set; }

    public Sequence Sequence => Project.GetSequence(SequenceId);

    /// <summary>Pista principal (magnética): la primera de video, la de más abajo.</summary>
    public Track? MainTrack => Sequence.VideoTracks.FirstOrDefault();

    public UndoStack History { get; private set; }

    public TimelineState Timeline { get; } = new();

    /// <summary>Archivo del proyecto, o <c>null</c> si nunca se guardó.</summary>
    public string? FilePath
    {
        get => _filePath;
        private set
        {
            if (SetProperty(ref _filePath, value))
            {
                OnPropertyChanged(nameof(SaveStateText));
            }
        }
    }

    public string ProjectName => Project.Name;

    public bool IsDirty => !History.IsClean;

    /// <summary>Texto corto para la barra de título.</summary>
    public string SaveStateText => IsDirty ? "Sin guardar" : FilePath is null ? "Nuevo" : "Guardado";

    /// <summary>Hay una apertura, un guardado o una importación en curso.</summary>
    public bool IsBusy => _busy > 0;

    public bool CanUndo => History.CanUndo;

    public bool CanRedo => History.CanRedo;

    public string? UndoDescription => History.UndoDescription;

    public string? RedoDescription => History.RedoDescription;

    public bool IsInGesture => History.IsInGroup;

    /// <summary>El historial completo, del estado inicial al último paso que se puede rehacer.</summary>
    public IReadOnlyList<HistoryEntry> HistoryEntries
    {
        get
        {
            IReadOnlyList<ICommand> done = History.UndoCommands;
            IReadOnlyList<ICommand> undone = History.RedoCommands;
            int? clean = History.CleanIndex;
            var entries = new List<HistoryEntry>(1 + done.Count + undone.Count)
            {
                new(0, _originDescription, true, done.Count == 0, clean == 0),
            };
            for (int i = 0; i < done.Count; i++)
            {
                entries.Add(new HistoryEntry(i + 1, done[i].Description, true, i + 1 == done.Count, clean == i + 1));
            }

            for (int j = undone.Count - 1; j >= 0; j--)
            {
                int position = done.Count + (undone.Count - j);
                entries.Add(new HistoryEntry(position, undone[j].Description, false, false, clean == position));
            }

            return entries;
        }
    }

    public IReadOnlyList<string> SelectedClipIds => _selection;

    public PlaybackSnapshot Transport => _transport;

    public Time Position => _transport.Position;

    public Time Duration => _transport.Duration;

    public bool IsPlaying => _transport.IsPlaying;

    public string PositionText => Timecode.Format(_transport.Position);

    public string DurationText => Timecode.Format(_transport.Duration);

    /// <summary>Aplica el comando y lo guarda en el historial. Si la edición no es posible, avisa y devuelve <c>false</c>.</summary>
    public bool Execute(ICommand command)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            History.Execute(command);
        }
        catch (EditException ex)
        {
            Notify(NotificationSeverity.Warning, "No se pudo hacer", ex.Message);
            return false;
        }

        AfterEdit(EditKind.Execute, command);
        return true;
    }

    public bool Undo()
    {
        VerifyAccess();
        if (!History.CanUndo)
        {
            return false;
        }

        ICommand command = History.UndoCommands[^1];
        History.Undo();
        AfterEdit(EditKind.Undo, command);
        return true;
    }

    public bool Redo()
    {
        VerifyAccess();
        if (!History.CanRedo)
        {
            return false;
        }

        ICommand command = History.RedoCommands[^1];
        History.Redo();
        AfterEdit(EditKind.Redo, command);
        return true;
    }

    /// <summary>Deshace o rehace hasta quedar con <paramref name="position"/> pasos aplicados (ver <see cref="HistoryEntry.Position"/>).</summary>
    public bool JumpToHistory(int position)
    {
        VerifyAccess();
        if (History.IsInGroup)
        {
            return false;
        }

        position = Math.Clamp(position, 0, History.Count + History.RedoCount);
        if (position == History.Count)
        {
            return false;
        }

        while (History.Count > position)
        {
            History.Undo();
        }

        while (History.Count < position)
        {
            History.Redo();
        }

        AfterEdit(EditKind.Jump, null);
        return true;
    }

    /// <summary>Fin de un gesto que se fundía (por ejemplo, soltar un deslizador): el siguiente cambio será otro paso.</summary>
    public void BreakMerge()
    {
        VerifyAccess();
        History.BreakMerge();
    }

    /// <summary>Empieza un gesto: todo lo que se ejecute hasta <see cref="EndGesture"/> se deshace en un paso.</summary>
    public void BeginGesture(string description)
    {
        VerifyAccess();
        History.BeginGroup(description);
    }

    /// <summary>Cierra el gesto abierto; si ya se canceló (Esc), no hace nada más que cortar la fusión.</summary>
    public void EndGesture()
    {
        VerifyAccess();
        if (History.IsInGroup)
        {
            History.EndGroup();
        }

        History.BreakMerge();
    }

    /// <summary>Revierte todo lo del gesto abierto (por ejemplo, Esc mientras se arrastra).</summary>
    public void CancelGesture()
    {
        VerifyAccess();
        if (!History.IsInGroup)
        {
            return;
        }

        History.CancelGroup();
        AfterEdit(EditKind.GestureCancelled, null);
    }

    public void Select(IEnumerable<string> clipIds)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(clipIds);
        SetSelection([.. clipIds.Distinct(StringComparer.Ordinal)]);
    }

    public void ToggleSelection(string clipId)
    {
        VerifyAccess();
        List<string> next = [.. _selection];
        if (!next.Remove(clipId))
        {
            next.Add(clipId);
        }

        SetSelection(next);
    }

    public void ClearSelection()
    {
        VerifyAccess();
        SetSelection([]);
    }

    public void Play() => _playback.Play();

    public void Pause() => _playback.Pause();

    public void TogglePlayPause() => _playback.TogglePlayPause();

    public void StepFrames(int frames) => _playback.StepFrames(frames);

    /// <summary>Mueve el cursor de reproducción, dentro de la secuencia.</summary>
    public void Seek(Time position)
    {
        Time end = Sequence.Duration;
        _playback.Seek(position < Time.Zero ? Time.Zero : position > end ? end : position);
    }

    public void SeekToStart() => Seek(Time.Zero);

    public void SeekToEnd() => Seek(Sequence.Duration);

    /// <summary>Copia el estado que publicó el reproductor; la ventana lo llama con su temporizador.</summary>
    public void UpdateTransport(PlaybackSnapshot snapshot)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(snapshot);
        PlaybackSnapshot previous = _transport;
        if (snapshot.Equals(previous))
        {
            return;
        }

        _transport = snapshot;
        OnPropertyChanged(nameof(Transport));
        if (snapshot.Position != previous.Position)
        {
            OnPropertyChanged(nameof(Position));
            OnPropertyChanged(nameof(PositionText));
        }

        if (snapshot.Duration != previous.Duration)
        {
            OnPropertyChanged(nameof(Duration));
            OnPropertyChanged(nameof(DurationText));
        }

        if (snapshot.IsPlaying != previous.IsPlaying)
        {
            OnPropertyChanged(nameof(IsPlaying));
        }

        TransportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Muestra un aviso en la ventana.</summary>
    public void Notify(NotificationSeverity severity, string title, string message) =>
        Notified?.Invoke(this, new EditorNotification(severity, title, message));

    /// <summary>Falla si se llama fuera del hilo que creó la sesión (el de la interfaz).</summary>
    public void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
        {
            throw new InvalidOperationException("EditorSession solo se usa desde el hilo de la interfaz.");
        }
    }

    private Project CreateEmptyProject() => ProjectFactory.CreateDefault(UntitledName, _ids);

    /// <summary>Cambia de proyecto: historial nuevo, selección vacía y el reproductor desde el principio.</summary>
    private void ReplaceProject(Project project, string sequenceId, string? filePath, string originDescription)
    {
        History.Changed -= OnHistoryChanged;
        Project = project;
        SequenceId = sequenceId;
        History = new UndoStack(project);
        History.Changed += OnHistoryChanged;
        _originDescription = originDescription;
        _selection = [];
        _editVersion++;
        FilePath = filePath;
        _playback.Load(project.Clone(), sequenceId);
        RaiseHistoryProperties();
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(ProjectName));
        ProjectOpened?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AfterEdit(EditKind kind, ICommand? command)
    {
        _editVersion++;
        PruneSelection();
        _playback.Reload(Project.Clone());
        OnPropertyChanged(nameof(ProjectName));
        ProjectEdited?.Invoke(this, new ProjectEditedEventArgs(kind, command));
    }

    private void PruneSelection()
    {
        Sequence sequence = Sequence;
        List<string> kept = _selection.FindAll(id => sequence.FindClip(id) is not null);
        if (kept.Count != _selection.Count)
        {
            SetSelection(kept);
        }
    }

    private void SetSelection(List<string> selection)
    {
        if (selection.SequenceEqual(_selection, StringComparer.Ordinal))
        {
            return;
        }

        _selection = selection;
        OnPropertyChanged(nameof(SelectedClipIds));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        RaiseHistoryProperties();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseHistoryProperties()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(SaveStateText));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
        OnPropertyChanged(nameof(IsInGesture));
        OnPropertyChanged(nameof(HistoryEntries));
    }

    private void BeginBusy()
    {
        if (_busy++ == 0)
        {
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    private void EndBusy()
    {
        if (--_busy == 0)
        {
            OnPropertyChanged(nameof(IsBusy));
        }
    }
}
