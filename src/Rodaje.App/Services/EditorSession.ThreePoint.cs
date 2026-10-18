using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

/// <summary>Cómo terminó una edición de tres o cuatro puntos.</summary>
public enum ThreePointOutcome
{
    Done,

    Failed,

    /// <summary>Cuatro puntos con duraciones distintas: hay que preguntar si se ajusta la velocidad o se recorta.</summary>
    NeedsDecision,
}

/// <summary>
/// El visor de origen y la edición de tres y cuatro puntos. El reproductor muestra el origen (un medio de la biblioteca)
/// o el programa (la secuencia); I y O marcan la entrada y la salida del que se ve, y «Insertar» y «Sobrescribir» llevan
/// el tramo del origen a las pistas destino con las reglas clásicas.
/// </summary>
public sealed partial class EditorSession
{
    /// <summary>Id de la secuencia de una sola toma que el reproductor muestra como origen; nunca entra al proyecto.</summary>
    public const string SourceSequenceId = "rodaje-visor-origen";

    private Time _programPosition;
    private Time _sourcePosition;

    /// <summary>Cambió el medio del origen, el visor activo, una marca o un selector de pista.</summary>
    public event EventHandler? SourceChanged;

    public string? SourceMediaId { get; private set; }

    /// <summary>El reproductor muestra el origen y no la secuencia.</summary>
    public bool IsSourceActive { get; private set; }

    public Time? SourceIn { get; private set; }

    public Time? SourceOut { get; private set; }

    public Time? SequenceIn { get; private set; }

    public Time? SequenceOut { get; private set; }

    /// <summary>Selector de pista de origen V: el video del origen entra en la edición.</summary>
    public bool SourceVideoEnabled { get; private set; } = true;

    /// <summary>Selector de pista de origen A: el audio del origen entra en la edición.</summary>
    public bool SourceAudioEnabled { get; private set; } = true;

    /// <summary>Pista de video destino; <c>null</c> es la principal.</summary>
    public string? TargetVideoTrackId { get; private set; }

    /// <summary>Pista de audio destino; <c>null</c> es la primera de audio.</summary>
    public string? TargetAudioTrackId { get; private set; }

    /// <summary>El cursor de la secuencia, también mientras el reproductor muestra el origen.</summary>
    public Time ProgramPosition => IsSourceActive ? _programPosition : Position;

    public MediaRef? SourceMedia => SourceMediaId is { } id ? Project.FindMedia(id) : null;

    /// <summary>Abre el medio en el visor de origen; si es otro medio, sus marcas empiezan vacías.</summary>
    public bool OpenSource(string mediaId)
    {
        VerifyAccess();
        if (Project.FindMedia(mediaId) is null)
        {
            return false;
        }

        if (SourceMediaId != mediaId)
        {
            SourceMediaId = mediaId;
            (SourceIn, SourceOut, _sourcePosition) = (null, null, Time.Zero);
            IsSourceActive = false;
        }

        ShowSource();
        return true;
    }

    /// <summary>Pasa el reproductor al origen o al programa.</summary>
    public void ToggleSourceProgram()
    {
        VerifyAccess();
        if (IsSourceActive)
        {
            ShowProgram();
        }
        else
        {
            ShowSource();
        }
    }

    public void ShowSource()
    {
        VerifyAccess();
        if (IsSourceActive || SourceMedia is null)
        {
            return;
        }

        _programPosition = Position;
        IsSourceActive = true;
        _playback.Load(PlaybackProject(), SourceSequenceId);
        _playback.Seek(_sourcePosition);
        SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowProgram()
    {
        VerifyAccess();
        if (!IsSourceActive)
        {
            return;
        }

        _sourcePosition = Position;
        IsSourceActive = false;
        _playback.Load(PlaybackProject(), SequenceId);
        _playback.Seek(_programPosition);
        SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marca la entrada (I) en el cuadro que se ve, del origen o de la secuencia según el visor.</summary>
    public void MarkIn() => Mark(isIn: true);

    /// <summary>Marca la salida (O): el cuadro que se ve es el último que entra.</summary>
    public void MarkOut() => Mark(isIn: false);

    /// <summary>Quita las dos marcas del visor activo.</summary>
    public void ClearMarks()
    {
        VerifyAccess();
        if (IsSourceActive)
        {
            (SourceIn, SourceOut) = (null, null);
        }
        else
        {
            (SequenceIn, SequenceOut) = (null, null);
        }

        SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSourceTracks(bool video, bool audio, string? videoTrackId, string? audioTrackId)
    {
        VerifyAccess();
        (SourceVideoEnabled, SourceAudioEnabled, TargetVideoTrackId, TargetAudioTrackId) = (video, audio, videoTrackId, audioTrackId);
        SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Inserta (,) o sobrescribe (.) el tramo del origen; con cuatro puntos distintos y sin <paramref name="fit"/>, pide decidir.</summary>
    public ThreePointOutcome ThreePointEdit(PlacementMode mode, FourPointFit fit = FourPointFit.Ask)
    {
        VerifyAccess();
        if (SourceMedia is null)
        {
            Notify(NotificationSeverity.Informational, "Falta el origen", "Abre un medio en el visor de origen (menú de la biblioteca) y marca su entrada y salida con I y O.");
            return ThreePointOutcome.Failed;
        }

        ThreePointRequest request = Request(mode, fit);
        ThreePointPlan plan;
        try
        {
            plan = TimelineEdits.PlanThreePoint(Project, request);
            if (MagneticStart(request, plan) is { } start)
            {
                request = request with { SequenceIn = start, SequenceOut = request.SequenceOut is null ? null : start + plan.Duration };
                plan = TimelineEdits.PlanThreePoint(Project, request);
            }
        }
        catch (EditException ex)
        {
            Notify(NotificationSeverity.Warning, "No se pudo hacer", ex.Message);
            return ThreePointOutcome.Failed;
        }

        if (plan.NeedsDecision)
        {
            return ThreePointOutcome.NeedsDecision;
        }

        SequenceEditCommand command = Edits.ThreePointEdit(SequenceId, request, _ids);
        if (!Execute(command))
        {
            return ThreePointOutcome.Failed;
        }

        // Como en los editores clásicos: las marcas de la secuencia se gastan y el cursor queda al final del clip.
        (SequenceIn, SequenceOut) = (null, null);
        Time end = Sequence.GetClip(command.CreatedIds[0]).Clip.End;
        if (IsSourceActive)
        {
            _programPosition = end;
        }
        else
        {
            Seek(end);
        }

        SourceChanged?.Invoke(this, EventArgs.Empty);
        return ThreePointOutcome.Done;
    }

    /// <summary>La duración de lo que muestra el reproductor: el medio del origen o la secuencia.</summary>
    private Time PlaybackDuration => IsSourceActive && SourceMedia is { } media ? media.InitialClipDuration : Sequence.Duration;

    private string PlaybackSequenceId => IsSourceActive ? SourceSequenceId : SequenceId;

    private ThreePointRequest Request(PlacementMode mode, FourPointFit fit)
    {
        Sequence sequence = Sequence;
        string? video = SourceVideoEnabled ? TargetVideoTrackId is { } v && sequence.FindTrack(v) is not null ? v : MainTrack?.Id : null;
        string? audio = SourceAudioEnabled ? TargetAudioTrackId is { } a && sequence.FindTrack(a) is not null ? a : sequence.AudioTracks.FirstOrDefault()?.Id : null;
        return new ThreePointRequest(SourceMediaId!, SourceIn, SourceOut, SequenceIn, SequenceOut,
            ProgramPosition.SnapToFrame(sequence.FrameRate), video, audio, mode, fit, Timeline.RippleScope);
    }

    /// <summary>La pista principal magnética no deja huecos: lo que caería después de su final empieza en su final.</summary>
    private Time? MagneticStart(ThreePointRequest request, ThreePointPlan plan) =>
        Timeline.MagneticMainTrack && MainTrack is { } main && request.VideoTrackId == main.Id && SourceMedia?.HasVideo == true && plan.At > main.End
            ? main.End
            : null;

    private void Mark(bool isIn)
    {
        VerifyAccess();
        if (IsSourceActive && SourceMedia is { } media)
        {
            Rational rate = media.FrameRate ?? Sequence.FrameRate;
            Time at = Position.SnapToFrame(rate);
            if (isIn)
            {
                SourceIn = at;
            }
            else
            {
                SourceOut = Time.Min(media.InitialClipDuration, at + Time.FromFrames(1, rate));
            }
        }
        else
        {
            Rational rate = Sequence.FrameRate;
            Time at = ProgramPosition.SnapToFrame(rate);
            if (isIn)
            {
                SequenceIn = at;
            }
            else
            {
                SequenceOut = at + Time.FromFrames(1, rate);
            }
        }

        SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>El proyecto con la secuencia del origen: una toma entera del medio, con su video y su audio.</summary>
    private Project SourceProject(MediaRef media)
    {
        Project copy = Project.Clone();
        Sequence program = Sequence;
        var source = new Sequence
        {
            Id = SourceSequenceId,
            Name = media.Name,
            FrameRate = media.FrameRate ?? program.FrameRate,
            Width = program.Width,
            Height = program.Height,
            SampleRate = program.SampleRate,
        };
        if (media.HasVideo)
        {
            source.VideoTracks.Add(new Track { Id = SourceSequenceId + "-v", Kind = TrackKind.Video, Clips = [SourceClip(media, "v")] });
        }

        if (media.HasAudio)
        {
            source.AudioTracks.Add(new Track { Id = SourceSequenceId + "-a", Kind = TrackKind.Audio, Clips = [SourceClip(media, "a")] });
        }

        copy.Sequences.Add(source);
        return copy;
    }

    private static Clip SourceClip(MediaRef media, string kind) =>
        new() { Id = $"{SourceSequenceId}-{kind}-clip", MediaId = media.Id, SourceIn = Time.Zero, SourceOut = media.InitialClipDuration, Start = Time.Zero };

    /// <summary>Al cambiar de proyecto o de secuencia vuelve el programa; el origen se olvida si su medio ya no está.</summary>
    private void ResetSource(bool forget)
    {
        if (forget || SourceMedia is null)
        {
            (SourceMediaId, SourceIn, SourceOut, _sourcePosition) = (null, null, null, Time.Zero);
        }

        IsSourceActive = false;
        (SequenceIn, SequenceOut) = (null, null);
        SourceChanged?.Invoke(this, EventArgs.Empty);
    }
}
