using Rodaje.App.Services.Effects;
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

    /// <summary>Ya está en <paramref name="defaultValue"/> y sin keyframes: restablecer no cambiaría nada.</summary>
    public bool IsDefault(double defaultValue) => !IsAnimated && Math.Abs(Value - defaultValue) < 1e-9;
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

    /// <summary>
    /// A qué clips va cada parte del panel con la selección de ahora: los de video seleccionados y el audio (los de audio
    /// seleccionados y el enlazado de cada video). Si se mezclan clips de video y de audio, solo queda lo común: el
    /// audio, y solo si todos los de video tienen el suyo. El audio enlazado de un video seleccionado va con su video.
    /// </summary>
    public (IReadOnlyList<Clip> Video, IReadOnlyList<Clip> Audio) Targets()
    {
        List<Clip> video = Selected(TrackKind.Video);
        List<Clip> audio = Selected(TrackKind.Audio);
        List<Clip?> linked = video.ConvertAll(v => (Clip?)Session.Sequence.GetLinkGroup(v.Id).Find(g => g.Track.Kind == TrackKind.Audio).Clip);
        audio.RemoveAll(linked.Contains);
        List<Clip> sound = [.. audio.Concat(linked.OfType<Clip>()).Distinct()];
        if (video.Count > 0 && audio.Count > 0)
        {
            return ([], linked.Contains(null) ? [] : sound);
        }

        return (video, sound);
    }

    /// <summary>Cuántos clips hay seleccionados, con los enlazados (el video y su audio) como uno solo.</summary>
    public int SelectedCount() =>
        Session.SelectedClipIds.Select(Session.Sequence.FindClip).OfType<(Track Track, Clip Clip)>().Select(f => f.Clip.LinkId ?? f.Clip.Id).Distinct().Count();

    /// <summary>El parámetro no vale lo mismo en todos los clips en el cursor: el inspector lo muestra como «—».</summary>
    public bool IsMixed(IReadOnlyList<Clip> clips, string effectType, string name, double defaultValue = 0)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (clips.Count < 2)
        {
            return false;
        }

        double first = Read(clips[0], effectType, name, defaultValue).Value;
        return clips.Skip(1).Any(c => Math.Abs(Read(c, effectType, name, defaultValue).Value - first) >= 1e-6);
    }

    /// <summary>El parámetro no vale lo mismo en los efectos indicados: el inspector lo muestra como «—».</summary>
    public bool IsMixed(IReadOnlyList<(Clip Clip, string? EffectId)> targets, string effectType, string name, double defaultValue = 0)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count < 2)
        {
            return false;
        }

        double first = Read(targets[0].Clip, effectType, name, defaultValue, targets[0].EffectId).Value;
        return targets.Skip(1).Any(t => Math.Abs(Read(t.Clip, effectType, name, defaultValue, t.EffectId).Value - first) >= 1e-6);
    }

    public bool IsPlayheadInside(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        Time at = Session.EditPosition;
        return clip.Start <= at && at < clip.End;
    }

    /// <summary>El cursor en el tiempo de los keyframes del clip (el del origen, o el virtual de un congelado), acotado al clip.</summary>
    public Time KeyframeTimeAtPlayhead(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return EffectCommands.KeyframeTime(clip, Time.Max(clip.Start, Session.EditPosition));
    }

    /// <summary>
    /// El efecto que se edita: el de <paramref name="effectId"/> si se da (la pestaña Filtros edita cada uno de la pila);
    /// si no, el primero activo de ese tipo (el que aplica el motor) o, si no hay, el primero.
    /// </summary>
    public static EffectInstance? FindEffect(Clip clip, string type, string? effectId = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (effectId is not null)
        {
            return clip.Effects.Find(e => e.Id == effectId && e.Type == type);
        }

        return clip.Effects.Find(e => e.Type == type && e.Enabled) ?? clip.Effects.Find(e => e.Type == type);
    }

    public ParameterState Read(Clip clip, string effectType, string name, double defaultValue, string? effectId = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        Time at = KeyframeTimeAtPlayhead(clip);
        if (FindEffect(clip, effectType, effectId) is not { } effect || !effect.Parameters.TryGetValue(name, out Parameter? parameter))
        {
            return new ParameterState(defaultValue, 0, null, null, null);
        }

        List<Keyframe> keys = parameter.Keyframes;
        Keyframe? here = KeyframeNear(clip, keys, at);
        Time tolerance = HalfFrame(clip);
        // Cortar o recortar deja en el efecto los keyframes de fuera del trozo: ◀ y ▶ solo van a los de dentro.
        Time first = EffectCommands.KeyframeTime(clip, clip.Start);
        Time last = EffectCommands.KeyframeTime(clip, clip.End - Time.FromTicks(1));
        Time low = Time.Min(first, last);
        Time high = Time.Max(first, last);
        Keyframe? previous = keys.FindLast(k => k.Time < at - tolerance && k.Time >= low && k.Time <= high);
        Keyframe? next = keys.Find(k => k.Time > at + tolerance && k.Time >= low && k.Time <= high);
        return new ParameterState(
            PathValue(effect, name, parameter, at),
            keys.Count,
            here,
            previous is null ? null : ToTimeline(clip, previous.Time),
            next is null ? null : ToTimeline(clip, next.Time));
    }

    /// <summary>Empieza un arrastre del parámetro; el instante queda fijo en el cursor de ahora.</summary>
    public void BeginChange(Clip clip, string effectType, string name, string description, string? effectId = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        EndChange();
        bool needsEffect = effectId is null && FindEffect(clip, effectType) is null;
        if (needsEffect)
        {
            Session.BeginGesture(description);
        }
        else
        {
            Session.BreakMerge();
        }

        _drag = new Drag(clip.Id, effectType, effectId, name, description, KeyframeTimeAtPlayhead(clip), IsPlayheadInside(clip), needsEffect);
    }

    /// <summary>Empieza un arrastre del parámetro en los efectos indicados de cada clip.</summary>
    public void BeginChange(IReadOnlyList<(Clip Clip, string? EffectId)> targets, string effectType, string name, string description)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            return;
        }

        if (targets.Count == 1)
        {
            BeginChange(targets[0].Clip, effectType, name, description, targets[0].EffectId);
            return;
        }

        EndChange();
        Session.BreakMerge();
        _drag = new Drag(targets[0].Clip.Id, effectType, targets[0].EffectId, name, description, KeyframeTimeAtPlayhead(targets[0].Clip), IsPlayheadInside(targets[0].Clip), false)
        {
            Targets = [.. targets.Select(t => new Target(t.Clip.Id, t.EffectId, KeyframeTimeAtPlayhead(t.Clip), IsPlayheadInside(t.Clip)))],
        };
    }

    /// <summary>Empieza un arrastre del parámetro en todos los clips: cada cambio los pone a todos en el mismo valor.</summary>
    public void BeginChange(IReadOnlyList<Clip> clips, string effectType, string name, string description)
    {
        ArgumentNullException.ThrowIfNull(clips);
        BeginChange(clips.Select(c => (c, (string?)null)).ToList(), effectType, name, description);
    }

    /// <summary>Aplica el valor del arrastre en curso; <c>false</c> si no se pudo (y la sesión ya avisó).</summary>
    public bool Change(double value)
    {
        if (_drag is { Failed: false, Targets: { } targets } many)
        {
            return ChangeMany(many, targets, value);
        }

        if (_drag is not { Failed: false } drag || Session.Sequence.FindClip(drag.ClipId) is not { } found)
        {
            return false;
        }

        Clip clip = found.Clip;
        // Si el primer paso falla (por ejemplo, pista bloqueada), el resto del arrastre no insiste ni repite el aviso.
        if (EnsureEffect(clip, drag.EffectType, drag.EffectId) is not { } effectId)
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

            at = KeyframeNear(clip, parameter.Keyframes, at)?.Time ?? at;
        }

        bool done = Session.Execute(Edits.SetParameter(Session.SequenceId, drag.ClipId, effectId, drag.Name, value, at, drag.Description));
        drag.Failed = !done;
        return done;
    }

    // Un clip animado solo cambia con el cursor dentro, en su keyframe más cercano; uno fijo, en su valor fijo.
    private bool ChangeMany(Drag drag, IReadOnlyList<Target> targets, double value)
    {
        var points = new List<(string ClipId, string? EffectId, Time At)>();
        foreach (Target target in targets)
        {
            if (Session.Sequence.FindClip(target.ClipId) is not { } found)
            {
                continue;
            }

            Time at = target.SourceTime;
            if (FindEffect(found.Clip, drag.EffectType, target.EffectId) is { } effect && effect.Parameters.TryGetValue(drag.Name, out Parameter? parameter)
                && parameter.Keyframes.Count > 0)
            {
                if (!target.Inside)
                {
                    continue;
                }

                at = KeyframeNear(found.Clip, parameter.Keyframes, at)?.Time ?? at;
            }

            points.Add((target.ClipId, target.EffectId, at));
        }

        if (points.Count == 0)
        {
            return false;
        }

        bool done = Session.Execute(Edits.SetParameterOnClips(Session.SequenceId, points, drag.EffectType, drag.Name, value, drag.Description));
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
    public bool SetValue(Clip clip, string effectType, string name, double value, string description, string? effectId = null)
    {
        BeginChange(clip, effectType, name, description, effectId);
        try
        {
            return Change(value);
        }
        finally
        {
            EndChange();
        }
    }

    /// <summary>Un cambio suelto en varios efectos indicados, en un solo paso.</summary>
    public bool SetValue(IReadOnlyList<(Clip Clip, string? EffectId)> targets, string effectType, string name, double value, string description)
    {
        BeginChange(targets, effectType, name, description);
        try
        {
            return Change(value);
        }
        finally
        {
            EndChange();
        }
    }

    /// <summary>Un cambio suelto en todos los clips, en un solo paso.</summary>
    public bool SetValue(IReadOnlyList<Clip> clips, string effectType, string name, double value, string description) =>
        SetValue(clips.Select(c => (c, (string?)null)).ToList(), effectType, name, value, description);

    /// <summary>El rombo: quita el keyframe del cursor o crea uno con el valor que se ve.</summary>
    public bool ToggleKeyframe(Clip clip, string effectType, string name, double defaultValue, string? effectId = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsPlayheadInside(clip))
        {
            return false;
        }

        ParameterState state = Read(clip, effectType, name, defaultValue, effectId);
        if (state.KeyframeHere is { } existing)
        {
            string existingEffect = FindEffect(clip, effectType, effectId)!.Id;
            return Session.Execute(Edits.RemoveKeyframe(Session.SequenceId, clip.Id, existingEffect, name, existing.Time));
        }

        Time at = KeyframeTimeAtPlayhead(clip);
        string clipId = clip.Id;
        Session.BeginGesture("Añadir keyframe");
        try
        {
            return EnsureEffect(clip, effectType, effectId) is { } id
                && Session.Execute(Edits.SetKeyframe(Session.SequenceId, clipId, id, name, at, state.Value));
        }
        finally
        {
            Session.EndGesture();
        }
    }

    /// <summary>
    /// El rombo con varios clips, en un paso del historial: si el primero con el cursor dentro tiene keyframe ahí, se quita
    /// en todos los que lo tienen; si no, se crea en los que no lo tienen.
    /// </summary>
    public bool ToggleKeyframe(IReadOnlyList<Clip> clips, string effectType, string name, double defaultValue)
    {
        ArgumentNullException.ThrowIfNull(clips);
        List<Clip> inside = [.. clips.Where(IsPlayheadInside)];
        if (inside.Count <= 1)
        {
            return inside.Count == 1 && ToggleKeyframe(inside[0], effectType, name, defaultValue);
        }

        bool remove = Read(inside[0], effectType, name, defaultValue).KeyframeHere is not null;
        Session.BeginGesture(remove ? "Quitar keyframe" : "Añadir keyframe");
        try
        {
            bool done = false;
            foreach (Clip clip in inside)
            {
                if (Session.Sequence.FindClip(clip.Id) is { } found && (Read(found.Clip, effectType, name, defaultValue).KeyframeHere is not null) == remove)
                {
                    done |= ToggleKeyframe(found.Clip, effectType, name, defaultValue);
                }
            }

            return done;
        }
        finally
        {
            Session.EndGesture();
        }
    }

    /// <summary>Algún parámetro de la lista tiene keyframes o un valor distinto del de fábrica en alguno de los clips.</summary>
    public static bool CanReset(IEnumerable<(Clip Clip, string? EffectId)> targets, string effectType,
        IEnumerable<(string Name, double DefaultValue)> parameters)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(parameters);
        List<(string Name, double DefaultValue)> list = [.. parameters];
        return targets.Any(t => CanReset(t.Clip, effectType, list, t.EffectId));
    }

    /// <summary>Algún parámetro de la lista tiene keyframes o un valor distinto del de fábrica en alguno de los clips.</summary>
    public static bool CanReset(IEnumerable<Clip> clips, string effectType, IEnumerable<(string Name, double DefaultValue)> parameters, string? effectId = null)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(parameters);
        List<(string Name, double DefaultValue)> list = [.. parameters];
        return clips.Any(clip => CanReset(clip, effectType, list, effectId));
    }

    /// <summary>Keyframes que tienen entre todos los clips en los parámetros nombrados.</summary>
    public static int KeyframeCount(IEnumerable<(Clip Clip, string? EffectId)> targets, string effectType, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(names);
        List<string> list = [.. names];
        return targets.Sum(t => KeyframeCount(t.Clip, effectType, list, t.EffectId));
    }

    /// <summary>Algún parámetro de la lista tiene keyframes o un valor distinto del de fábrica.</summary>
    public static bool CanReset(Clip clip, string effectType, IEnumerable<(string Name, double DefaultValue)> parameters, string? effectId = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(parameters);
        return FindEffect(clip, effectType, effectId) is { } effect && ChangedFromDefault(effect, parameters).Count > 0;
    }

    /// <summary>Keyframes que tienen entre todos los clips en los parámetros nombrados.</summary>
    public static int KeyframeCount(IEnumerable<Clip> clips, string effectType, IEnumerable<string> names, string? effectId = null)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(names);
        List<string> list = [.. names];
        return clips.Sum(clip => KeyframeCount(clip, effectType, list, effectId));
    }

    /// <summary>Keyframes que tienen entre todos los parámetros nombrados.</summary>
    public static int KeyframeCount(Clip clip, string effectType, IEnumerable<string> names, string? effectId = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(names);
        return FindEffect(clip, effectType, effectId) is { } effect
            ? names.Sum(n => effect.Parameters.TryGetValue(n, out Parameter? p) ? p.Keyframes.Count : 0)
            : 0;
    }

    /// <summary>
    /// Restablece los parámetros en varios clips al valor de fábrica en un solo paso del historial (D-024).
    /// </summary>
    public Task<bool> ResetAsync(
        IReadOnlyList<Clip> clips,
        string effectType,
        IReadOnlyList<(string Name, double DefaultValue)> parameters,
        string description,
        Func<int, Task<bool>> confirmRemoval,
        string? effectId = null)
    {
        ArgumentNullException.ThrowIfNull(clips);
        return ResetAsync(clips.Select(c => (c, effectId)).ToList(), effectType, parameters, description, confirmRemoval);
    }

    /// <summary>
    /// Restablece los parámetros en varios clips con sus efectos específicos al valor de fábrica en un solo paso del historial (D-024).
    /// </summary>
    public async Task<bool> ResetAsync(
        IReadOnlyList<(Clip Clip, string? EffectId)> targets,
        string effectType,
        IReadOnlyList<(string Name, double DefaultValue)> parameters,
        string description,
        Func<int, Task<bool>> confirmRemoval)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(confirmRemoval);
        if (targets.Count == 0)
        {
            return false;
        }

        if (targets.Count == 1)
        {
            return await ResetAsync(targets[0].Clip, effectType, parameters, description, confirmRemoval, targets[0].EffectId);
        }

        EndChange();
        if (!CanReset(targets, effectType, parameters))
        {
            return false;
        }

        int keyframes = KeyframeCount(targets, effectType, parameters.Select(p => p.Name));
        if (keyframes > 0 && !await confirmRemoval(keyframes))
        {
            return false;
        }

        var results = new List<(string ClipId, string EffectId, IReadOnlyDictionary<string, double> Defaults)>();
        foreach ((Clip clip, string? effectId) in targets)
        {
            if (Session.Sequence.FindClip(clip.Id) is not { } found || FindEffect(found.Clip, effectType, effectId) is not { } effect)
            {
                continue;
            }

            Dictionary<string, double> changed = ChangedFromDefault(effect, parameters);
            if (changed.Count > 0)
            {
                results.Add((clip.Id, effect.Id, changed));
            }
        }

        return results.Count > 0 && Session.Execute(Edits.ResetParametersOnClips(Session.SequenceId, results, description));
    }

    /// <summary>
    /// Restablece los parámetros al valor de fábrica en un solo paso del historial (D-024). Si alguno tiene keyframes,
    /// antes pregunta con <paramref name="confirmRemoval"/> (recibe cuántos se borrarán); si se cancela, no cambia nada.
    /// </summary>
    /// <returns><c>false</c> si no había nada que cambiar, se canceló o la sesión rechazó la edición.</returns>
    public async Task<bool> ResetAsync(
        Clip clip,
        string effectType,
        IReadOnlyList<(string Name, double DefaultValue)> parameters,
        string description,
        Func<int, Task<bool>> confirmRemoval,
        string? effectId = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(confirmRemoval);
        EndChange();
        string clipId = clip.Id;
        if (!CanReset(clip, effectType, parameters, effectId))
        {
            return false;
        }

        int keyframes = KeyframeCount(clip, effectType, parameters.Select(p => p.Name), effectId);
        if (keyframes > 0 && !await confirmRemoval(keyframes))
        {
            return false;
        }

        // Mientras el aviso estaba abierto el clip pudo cambiar o desaparecer: se vuelve a leer.
        if (Session.Sequence.FindClip(clipId) is not { } found || FindEffect(found.Clip, effectType, effectId) is not { } effect)
        {
            return false;
        }

        Dictionary<string, double> changed = ChangedFromDefault(effect, parameters);
        return changed.Count > 0
            && Session.Execute(Edits.ResetParameters(Session.SequenceId, clipId, effect.Id, changed, description));
    }

    /// <summary>
    /// Quita todos los keyframes del parámetro y lo deja fijo en el valor que se ve en el cursor, tras confirmarlo con
    /// <paramref name="confirmRemoval"/>. Un solo paso del historial.
    /// </summary>
    public async Task<bool> ClearKeyframesAsync(
        Clip clip, string effectType, string name, string description, Func<int, Task<bool>> confirmRemoval, string? effectId = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(confirmRemoval);
        EndChange();
        string clipId = clip.Id;
        int keyframes = KeyframeCount(clip, effectType, [name], effectId);
        if (keyframes == 0 || !await confirmRemoval(keyframes))
        {
            return false;
        }

        if (Session.Sequence.FindClip(clipId) is not { } found || FindEffect(found.Clip, effectType, effectId) is not { } effect
            || !effect.Parameters.TryGetValue(name, out Parameter? parameter) || parameter.Keyframes.Count == 0)
        {
            return false;
        }

        double value = parameter.Evaluate(KeyframeTimeAtPlayhead(found.Clip));
        return Session.Execute(Edits.ResetParameters(
            Session.SequenceId, clipId, effect.Id, new Dictionary<string, double> { [name] = value }, description));
    }

    /// <summary>
    /// «Quitar keyframes» con varios clips: tras una sola confirmación, cada uno queda fijo en el valor que tiene en el
    /// cursor. Un paso del historial.
    /// </summary>
    public async Task<bool> ClearKeyframesAsync(
        IReadOnlyList<Clip> clips, string effectType, string name, string description, Func<int, Task<bool>> confirmRemoval)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(confirmRemoval);
        if (clips.Count == 1)
        {
            return await ClearKeyframesAsync(clips[0], effectType, name, description, confirmRemoval);
        }

        EndChange();
        int keyframes = KeyframeCount(clips, effectType, [name]);
        if (keyframes == 0 || !await confirmRemoval(keyframes))
        {
            return false;
        }

        // Mientras el aviso estaba abierto los clips pudieron cambiar: se vuelven a leer.
        var results = new List<(string ClipId, string EffectId, IReadOnlyDictionary<string, double> Defaults)>();
        foreach (Clip clip in clips)
        {
            if (Session.Sequence.FindClip(clip.Id) is { } found && FindEffect(found.Clip, effectType) is { } effect
                && effect.Parameters.TryGetValue(name, out Parameter? parameter) && parameter.Keyframes.Count > 0)
            {
                results.Add((clip.Id, effect.Id, new Dictionary<string, double> { [name] = parameter.Evaluate(KeyframeTimeAtPlayhead(found.Clip)) }));
            }
        }

        return results.Count > 0 && Session.Execute(Edits.ResetParametersOnClips(Session.SequenceId, results, description));
    }

    /// <summary>La curva del keyframe del cursor en cada clip que tiene uno ahí, en un paso del historial.</summary>
    public bool SetInterpolation(IReadOnlyList<Clip> clips, string effectType, string name, Interpolation interpolation)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (clips.Count == 1)
        {
            return SetInterpolation(clips[0], effectType, name, interpolation);
        }

        Session.BeginGesture("Cambiar interpolación");
        try
        {
            bool done = false;
            foreach (Clip clip in clips)
            {
                done |= Session.Sequence.FindClip(clip.Id) is { } found && SetInterpolation(found.Clip, effectType, name, interpolation);
            }

            return done;
        }
        finally
        {
            Session.EndGesture();
        }
    }

    /// <summary>Cambia la curva del keyframe del cursor hacia el siguiente.</summary>
    public bool SetInterpolation(Clip clip, string effectType, string name, Interpolation interpolation, string? effectId = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (FindEffect(clip, effectType, effectId) is not { } effect
            || !effect.Parameters.TryGetValue(name, out Parameter? parameter)
            || KeyframeNear(clip, parameter.Keyframes, KeyframeTimeAtPlayhead(clip)) is not { } keyframe
            || keyframe.Interpolation == interpolation)
        {
            return false;
        }

        // A Bézier se pasa con la forma que ya tenía (la Bézier más parecida a la curva fija), para seguirla en la gráfica.
        return interpolation == Interpolation.Bezier
            ? Session.Execute(Edits.SetKeyframeEase(Session.SequenceId, clip.Id, effect.Id, name, keyframe.Time, keyframe.Ease ?? KeyframeGraph.EaseOf(keyframe)))
            : Session.Execute(Edits.SetKeyframeInterpolation(Session.SequenceId, clip.Id, effect.Id, name, keyframe.Time, interpolation));
    }

    /// <summary>Lleva el cursor al keyframe anterior (<paramref name="direction"/> negativo) o siguiente.</summary>
    public bool GoToKeyframe(Clip clip, string effectType, string name, int direction, string? effectId = null)
    {
        ParameterState state = Read(clip, effectType, name, 0, effectId);
        if ((direction < 0 ? state.Previous : state.Next) is not { } target)
        {
            return false;
        }

        Session.Seek(target);
        return true;
    }

    /// <summary>X e Y de la posición se leen sobre la trayectoria, como los dibuja el motor.</summary>
    private static double PathValue(EffectInstance effect, string name, Parameter parameter, Time at)
    {
        if (effect.Type == BuiltInEffects.Transform && name is BuiltInEffects.TransformParameters.X or BuiltInEffects.TransformParameters.Y
            && effect.Parameters.TryGetValue(name == BuiltInEffects.TransformParameters.X ? BuiltInEffects.TransformParameters.Y : BuiltInEffects.TransformParameters.X,
                out Parameter? partner))
        {
            return KeyframeGraph.ValueAt(parameter, partner, name == BuiltInEffects.TransformParameters.X, at);
        }

        return parameter.Evaluate(at);
    }

    private List<Clip> Selected(TrackKind kind)
    {
        Sequence sequence = Session.Sequence;
        var clips = new List<Clip>();
        foreach (string id in Session.SelectedClipIds)
        {
            if (sequence.FindClip(id) is { } found && found.Track.Kind == kind)
            {
                clips.Add(found.Clip);
            }
        }

        return clips;
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

    private static Dictionary<string, double> ChangedFromDefault(EffectInstance effect, IEnumerable<(string Name, double DefaultValue)> parameters)
    {
        var changed = new Dictionary<string, double>();
        foreach ((string name, double defaultValue) in parameters)
        {
            if (effect.Parameters.TryGetValue(name, out Parameter? parameter)
                && (parameter.Keyframes.Count > 0 || Math.Abs(parameter.Value - defaultValue) >= 1e-9))
            {
                changed[name] = defaultValue;
            }
        }

        return changed;
    }

    private string? EnsureEffect(Clip clip, string type, string? effectId)
    {
        if (FindEffect(clip, type, effectId) is { } existing)
        {
            return existing.Id;
        }

        if (effectId is not null)
        {
            return null;
        }

        var command = Edits.EnsureEffect(Session.SequenceId, clip.Id, type);
        return Session.Execute(command) ? command.CreatedIds[0] : null;
    }

    private Keyframe? KeyframeNear(Clip clip, List<Keyframe> keys, Time at)
    {
        Time tolerance = HalfFrame(clip);
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

    // Medio cuadro de la línea de tiempo en el tiempo de los keyframes: a 2× abarca el doble de origen.
    private Time HalfFrame(Clip clip)
    {
        long half = Time.FromFrames(1, Session.Sequence.FrameRate).Ticks / 2;
        if (clip.Speed is null)
        {
            return Time.FromTicks(half);
        }

        ClipTimeMap map = clip.TimeMap;
        double rate = map.Freeze ? 1 : Math.Abs(map.RateAt(Session.EditPosition));
        return Time.FromTicks(Math.Max(1, (long)(half * rate)));
    }

    private static Time ToTimeline(Clip clip, Time keyframeTime) => EffectCommands.TimelineOfKeyframe(clip, keyframeTime);

    // Esc revirtió el gesto (con el efecto recién creado): el resto del arrastre no hace nada y al soltar no hay grupo que cerrar.
    private void OnProjectEdited(object? sender, ProjectEditedEventArgs e)
    {
        if (e.Kind == EditKind.GestureCancelled && _drag is { Grouped: true } drag)
        {
            _drag = drag with { Grouped = false, Failed = true };
        }
    }

    private sealed record Drag(string ClipId, string EffectType, string? EffectId, string Name, string Description, Time SourceTime, bool Inside, bool Grouped)
    {
        public bool Failed { get; set; }

        /// <summary>Con varios clips: todos, cada uno con su instante del origen.</summary>
        public IReadOnlyList<Target>? Targets { get; init; }
    }

    private sealed record Target(string ClipId, string? EffectId, Time SourceTime, bool Inside);
}
