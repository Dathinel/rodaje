using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

/// <summary>Un borde de clip elegido para recortarlo con el teclado («,» y «.»).</summary>
public sealed record SelectedEdge(string ClipId, TrimEdge Edge, bool Ripple);

/// <summary>
/// La línea de tiempo profesional desde la sesión: grupos, colores, empujones cuadro a cuadro, recorte del borde elegido,
/// huecos, sincronía de enlazados, las ediciones desde el origen que no son insertar ni sobrescribir, rango y multicámara.
/// </summary>
public sealed partial class EditorSession
{
    private SelectedEdge? _selectedEdge;

    /// <summary>El borde elegido con un clic (sin arrastrar); «,» y «.» lo recortan un cuadro.</summary>
    public SelectedEdge? SelectedEdge
    {
        get => _selectedEdge is { } edge && Sequence.FindClip(edge.ClipId) is not null ? edge : null;
        set
        {
            _selectedEdge = value;
            SelectedEdgeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SelectedEdgeChanged;

    /// <summary>
    /// Las multicámaras de la biblioteca (ángulos sincronizados por sonido); la biblioteca las pone al cargar su
    /// organización. Con el clip de la pista principal en una, 1 a 9 cortan a cada ángulo.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<MulticamAngle>> Multicams { get; set; } = [];

    /// <summary>La selección con los demás miembros de sus grupos (lo que se mueve junto); sin selección, vacía.</summary>
    public IReadOnlyList<string> SelectionWithGroups() =>
        Timeline.IgnoreGroups ? [.. SelectedClipIds] : [.. TimelineEdits.ExpandWithGroups(Sequence, SelectedClipIds).Select(m => m.Clip.Id)];

    public bool GroupSelection()
    {
        VerifyAccess();
        return SelectedClipIds.Count > 1 && Execute(Edits.GroupClips(SequenceId, SelectedClipIds, _ids));
    }

    public bool UngroupSelection()
    {
        VerifyAccess();
        return SelectedClipIds.Count > 0 && Execute(Edits.UngroupClips(SequenceId, SelectedClipIds, _ids));
    }

    /// <summary>Pone el color de etiqueta (o el de su tipo, con <c>null</c>) a la selección.</summary>
    public bool SetSelectionColor(string? color)
    {
        VerifyAccess();
        return SelectedClipIds.Count > 0 && Execute(Edits.SetClipColor(SequenceId, SelectedClipIds, color, _ids));
    }

    /// <summary>Mueve la selección (con grupos y enlazados) <paramref name="frames"/> cuadros sin pisar nada.</summary>
    public bool NudgeSelection(int frames)
    {
        VerifyAccess();
        if (SelectedClipIds.Count == 0)
        {
            return false;
        }

        return Execute(Edits.MoveClips(SequenceId, SelectedClipIds, Time.FromFrames(frames, Sequence.FrameRate), _ids, alone: Timeline.IgnoreGroups));
    }

    /// <summary>Recorta el borde elegido <paramref name="frames"/> cuadros (positivo: hacia la derecha).</summary>
    public bool TrimSelectedEdge(int frames)
    {
        VerifyAccess();
        if (SelectedEdge is not { } edge)
        {
            return false;
        }

        Time delta = Time.FromFrames(frames, Sequence.FrameRate);
        bool ripple = edge.Ripple || Timeline.AutoRipple
            || (Timeline.MagneticMainTrack && MainTrack is { } main && Sequence.FindClip(edge.ClipId)?.Track == main);
        return Execute(ripple
            ? Edits.RippleTrim(SequenceId, edge.ClipId, edge.Edge, delta, Timeline.RippleScope, _ids)
            : Edits.Trim(SequenceId, edge.ClipId, edge.Edge, delta, _ids));
    }

    /// <summary>
    /// Cierra los huecos de las pistas elegidas (la del clip seleccionado o, sin selección, la principal). Avisa cuántos
    /// cerró y cuántos dejó para no desincronizar.
    /// </summary>
    public bool CloseAllGaps(IReadOnlyCollection<string>? trackIds = null)
    {
        VerifyAccess();
        List<string> tracks = trackIds is { Count: > 0 } ? [.. trackIds]
            : SelectedClipIds.Select(id => Sequence.FindClip(id)?.Track.Id).OfType<string>().Distinct().ToList() is { Count: > 0 } selected ? selected
            : MainTrack is { } main ? [main.Id] : [];
        if (tracks.Count == 0)
        {
            return false;
        }

        int before = TimelineEdits.FindGaps(Sequence, tracks).Count;
        if (!Execute(Edits.CloseAllGaps(SequenceId, tracks, Timeline.RippleScope, _ids)))
        {
            return false;
        }

        int left = TimelineEdits.FindGaps(Sequence, tracks).Count;
        if (left > 0)
        {
            Notify(NotificationSeverity.Informational, "Huecos cerrados", $"Se cerraron {before - left} y quedan {left}: cerrarlos desincronizaría clips enlazados de otras pistas.");
        }

        return true;
    }

    /// <summary>Lleva el cursor al hueco siguiente o anterior de cualquier pista sin bloquear.</summary>
    public bool GoToGap(bool forward)
    {
        VerifyAccess();
        if (TimelineEdits.NextGap(Sequence, EditPosition, forward, Sequence.AllTracks.Where(t => !t.Locked).Select(t => t.Id)) is not { } gap)
        {
            Notify(NotificationSeverity.Informational, "No hay huecos", forward ? "No hay más huecos después del cursor." : "No hay huecos antes del cursor.");
            return false;
        }

        Seek(gap.Start);
        return true;
    }

    /// <summary>Vuelve a sincronizar un clip desfasado con su enlazado.</summary>
    public bool Resync(string clipId, ResyncMode mode)
    {
        VerifyAccess();
        return Execute(Edits.Resync(SequenceId, clipId, mode, _ids));
    }

    /// <summary>Marca la entrada y la salida de la secuencia (la herramienta de rango); el tramo vacío las quita.</summary>
    public void SetSequenceRange(Time start, Time end)
    {
        VerifyAccess();
        if (end > start)
        {
            (SequenceIn, SequenceOut) = (start, end);
        }
        else
        {
            (SequenceIn, SequenceOut) = (null, null);
        }
        SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reemplazar, ajustar a rellenar o superponer desde el origen, con sus marcas y el cursor del origen.</summary>
    public bool SourceAction(ThreePointAction action)
    {
        VerifyAccess();
        if (SourceMedia is null)
        {
            Notify(NotificationSeverity.Informational, "Falta el origen", "Abre un medio en el visor de origen (menú de la biblioteca) y marca su entrada y salida con I y O.");
            return false;
        }

        ThreePointRequest request = Request(PlacementMode.Overwrite, FourPointFit.Trim) with
        {
            SourcePlayhead = (IsSourceActive ? Position : _sourcePosition).SnapToFrame(SourceMedia.FrameRate ?? Sequence.FrameRate),
        };
        SequenceEditCommand command = Edits.ThreePointEdit(SequenceId, request, action, _ids);
        if (!Execute(command))
        {
            return false;
        }

        (SequenceIn, SequenceOut) = (null, null);
        Select([command.CreatedIds[0]]);
        SourceChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Corta al ángulo <paramref name="angle"/> de la multicámara en el cursor, sobre el clip de la pista principal. Va
    /// también mientras se reproduce: el corte cae en el cuadro que se ve.
    /// </summary>
    public bool SwitchAngle(IReadOnlyList<MulticamAngle> angles, int angle)
    {
        VerifyAccess();
        if (MainTrack is not { } main)
        {
            return false;
        }

        Time at = EditPosition;
        if (main.ClipAt(at) is { } clip && MulticamEdits.IndexOf(angles, clip.MediaId) == angle)
        {
            return false;
        }

        return Execute(Edits.SwitchAngle(SequenceId, angles, main.Id, at, angle, _ids));
    }
}
