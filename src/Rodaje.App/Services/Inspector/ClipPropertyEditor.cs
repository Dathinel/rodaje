using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>Cómo se ve un parámetro en el cursor.</summary>
/// <param name="Value">Valor evaluado en el cursor (o el de siempre si no hay efecto).</param>
/// <param name="KeyframeCount">Keyframes del parámetro.</param>
/// <param name="KeyframeHere">El keyframe que cae en el cuadro del cursor, si hay uno.</param>
/// <param name="Previous">Instante de la línea de tiempo del keyframe anterior al cursor.</param>
/// <param name="Next">Instante de la línea de tiempo del keyframe siguiente al cursor.</param>
public sealed record ParameterState(double Value, int KeyframeCount, Keyframe? KeyframeHere, Time? Previous, Time? Next)
{
    public bool IsAnimated => KeyframeCount > 0;
}

/// <summary>
/// Edita los parámetros animables de un clip en el cursor: sin keyframes se cambia el valor fijo;
/// con keyframes, cambiar el valor crea o ajusta el keyframe del cuadro del cursor.
/// </summary>
/// <remarks>
/// Un arrastre (<see cref="BeginChange"/>, <see cref="Change"/>, <see cref="EndChange"/>) es un paso del historial: los
/// cambios se funden por clave, o van en un gesto si antes hay que crear el efecto. El instante se fija al empezar,
/// así un arrastre durante la reproducción no siembra keyframes por el camino.
/// </remarks>
public sealed class ClipPropertyEditor
{
    private Drag? _drag;

    public ClipPropertyEditor(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        session.ProjectEdited += OnProjectEdited;
    }

    public EditorSession Session { get; }

    public bool IsDragging => _drag is not null;

    /// <summary>El primer clip seleccionado de una pista de video.</summary>
    public Clip? VideoTarget() => FirstSelected(TrackKind.Video);

    /// <summary>El primer clip seleccionado de una pista de audio o, si no hay, el audio enlazado al clip de video.</summary>
    public Clip? AudioTarget()
    {
        if (FirstSelected(TrackKind.Audio) is { } audio)
        {
            return audio;
        }

        return VideoTarget() is { } video
            ? Session.Sequence.GetLinkGroup(video.Id).Find(g => g.Track.Kind == TrackKind.Audio).Clip
            : null;
    }

    public bool IsPlayheadInside(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        Time at = Session.EditPosition;
        return clip.Start <= at && at < clip.End;
    }

    /// <summary>El cursor en tiempo del origen del clip, llevado a sus bordes si cae fuera.</summary>
    public Time SourceTimeAtPlayhead(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        Time source = clip.ToSourceTime(Session.EditPosition);
        return Time.Max(clip.SourceIn, Time.Min(source, clip.SourceOut));
    }

    /// <summary>El efecto de ese tipo que se edita: el primero activo (el que aplica el motor) o, si no hay, el primero.</summary>
    public static EffectInstance? FindEffect(Clip clip, string type)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Effects.Find(e => e.Type == type && e.Enabled) ?? clip.Effects.Find(e => e.Type == type);
    }

    public ParameterState Read(Clip clip, string effectType, string name, double defaultValue)
    {
        ArgumentNullException.ThrowIfNull(clip);
        Time at = SourceTimeAtPlayhead(clip);
        if (FindEffect(clip, effectType) is not { } effect || !effect.Parameters.TryGetValue(name, out Parameter? parameter))
        {
            return new ParameterState(defaultValue, 0, null, null, null);
        }

        List<Keyframe> keys = parameter.Keyframes;
        Keyframe? here = KeyframeNear(keys, at);
        Time tolerance = HalfFrame();
        Keyframe? previous = keys.FindLast(k => k.Time < at - tolerance);
        Keyframe? next = keys.Find(k => k.Time > at + tolerance);
        return new ParameterState(
            parameter.Evaluate(at),
            keys.Count,
            here,
            previous is null ? null : ToTimeline(clip, previous.Time),
            next is null ? null : ToTimeline(clip, next.Time));
    }

    /// <summary>Empieza un arrastre del parámetro; el instante queda fijo en el cursor de ahora.</summary>
    public void BeginChange(Clip clip, string effectType, string name, string description)
    {
        ArgumentNullException.ThrowIfNull(clip);
        EndChange();
        bool needsEffect = FindEffect(clip, effectType) is null;
        if (needsEffect)
        {
            Session.BeginGesture(description);
        }
        else
        {
            Session.BreakMerge();
        }

        _drag = new Drag(clip.Id, effectType, name, description, SourceTimeAtPlayhead(clip), IsPlayheadInside(clip), needsEffect);
    }

    /// <summary>Aplica el valor del arrastre en curso; <c>false</c> si no se pudo (y la sesión ya avisó).</summary>
    public bool Change(double value)
    {
        if (_drag is not { Failed: false } drag || Session.Sequence.FindClip(drag.ClipId) is not { } found)
        {
            return false;
        }

        Clip clip = found.Clip;
        // Si el primer paso falla (por ejemplo, pista bloqueada), el resto del arrastre no insiste ni repite el aviso.
        if (EnsureEffect(clip, drag.EffectType) is not { } effectId)
        {
            drag.Failed = true;
            return false;
        }

        clip = Session.Sequence.GetClip(drag.ClipId).Clip;
        EffectInstance effect = clip.Effects.Find(e => e.Id == effectId)!;
        Time at = drag.SourceTime;
        if (effect.Parameters.TryGetValue(drag.Name, out Parameter? parameter) && parameter.Keyframes.Count > 0)
        {
            if (!drag.Inside)
            {
                return false;
            }

            at = KeyframeNear(parameter.Keyframes, at)?.Time ?? at;
        }

        bool done = Session.Execute(Edits.SetParameter(Session.SequenceId, drag.ClipId, effectId, drag.Name, value, at, drag.Description));
        drag.Failed = !done;
        return done;
    }

    public void EndChange()
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        if (drag.Grouped)
        {
            Session.EndGesture();
        }
        else
        {
            Session.BreakMerge();
        }
    }

    /// <summary>Un cambio suelto (por ejemplo, al escribir el número).</summary>
    public bool SetValue(Clip clip, string effectType, string name, double value, string description)
    {
        BeginChange(clip, effectType, name, description);
        try
        {
            return Change(value);
        }
        finally
        {
            EndChange();
        }
    }

    /// <summary>El rombo: quita el keyframe del cursor o crea uno con el valor que se ve.</summary>
    public bool ToggleKeyframe(Clip clip, string effectType, string name, double defaultValue)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsPlayheadInside(clip))
        {
            return false;
        }

        ParameterState state = Read(clip, effectType, name, defaultValue);
        if (state.KeyframeHere is { } existing)
        {
            string effectId = FindEffect(clip, effectType)!.Id;
            return Session.Execute(Edits.RemoveKeyframe(Session.SequenceId, clip.Id, effectId, name, existing.Time));
        }

        Time at = SourceTimeAtPlayhead(clip);
        string clipId = clip.Id;
        Session.BeginGesture("Añadir keyframe");
        try
        {
            return EnsureEffect(clip, effectType) is { } id
                && Session.Execute(Edits.SetKeyframe(Session.SequenceId, clipId, id, name, at, state.Value));
        }
        finally
        {
            Session.EndGesture();
        }
    }

    /// <summary>Cambia la curva del keyframe del cursor hacia el siguiente.</summary>
    public bool SetInterpolation(Clip clip, string effectType, string name, Interpolation interpolation)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (FindEffect(clip, effectType) is not { } effect
            || !effect.Parameters.TryGetValue(name, out Parameter? parameter)
            || KeyframeNear(parameter.Keyframes, SourceTimeAtPlayhead(clip)) is not { } keyframe
            || keyframe.Interpolation == interpolation)
        {
            return false;
        }

        return Session.Execute(Edits.SetKeyframeInterpolation(Session.SequenceId, clip.Id, effect.Id, name, keyframe.Time, interpolation));
    }

    /// <summary>Lleva el cursor al keyframe anterior (<paramref name="direction"/> negativo) o siguiente.</summary>
    public bool GoToKeyframe(Clip clip, string effectType, string name, int direction)
    {
        ParameterState state = Read(clip, effectType, name, 0);
        if ((direction < 0 ? state.Previous : state.Next) is not { } target)
        {
            return false;
        }

        Session.Seek(target);
        return true;
    }

    private Clip? FirstSelected(TrackKind kind)
    {
        Sequence sequence = Session.Sequence;
        foreach (string id in Session.SelectedClipIds)
        {
            if (sequence.FindClip(id) is { } found && found.Track.Kind == kind)
            {
                return found.Clip;
            }
        }

        return null;
    }

    private string? EnsureEffect(Clip clip, string type)
    {
        if (FindEffect(clip, type) is { } existing)
        {
            return existing.Id;
        }

        var command = Edits.EnsureEffect(Session.SequenceId, clip.Id, type);
        return Session.Execute(command) ? command.CreatedIds[0] : null;
    }

    private Keyframe? KeyframeNear(List<Keyframe> keys, Time at)
    {
        Time tolerance = HalfFrame();
        Keyframe? best = null;
        foreach (Keyframe key in keys)
        {
            Time distance = key.Time >= at ? key.Time - at : at - key.Time;
            if (distance <= tolerance && (best is null || distance < (best.Time >= at ? best.Time - at : at - best.Time)))
            {
                best = key;
            }
        }

        return best;
    }

    private Time HalfFrame() => Time.FromTicks(Time.FromFrames(1, Session.Sequence.FrameRate).Ticks / 2);

    private static Time ToTimeline(Clip clip, Time sourceTime) => clip.Start + (sourceTime - clip.SourceIn);

    // Esc revirtió el gesto (con el efecto recién creado): el resto del arrastre no hace nada y al soltar no hay grupo que cerrar.
    private void OnProjectEdited(object? sender, ProjectEditedEventArgs e)
    {
        if (e.Kind == EditKind.GestureCancelled && _drag is { Grouped: true } drag)
        {
            _drag = drag with { Grouped = false, Failed = true };
        }
    }

    private sealed record Drag(string ClipId, string EffectType, string Name, string Description, Time SourceTime, bool Inside, bool Grouped)
    {
        public bool Failed { get; set; }
    }
}
