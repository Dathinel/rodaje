using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using K = Rodaje.Core.Model.BuiltInEffects.CompressorParameters;

namespace Rodaje.App.Services.Audio;

/// <summary>De quién es el compresor que se edita: un clip de audio, una pista de audio o la mezcla.</summary>
public enum DynamicsOwnerKind
{
    Clip,
    Track,
    Bus,
    Mix,
}

/// <param name="Id">Identificador del clip, de la pista o de la secuencia (la mezcla): el dueño de la pila de efectos.</param>
public sealed record DynamicsOwner(DynamicsOwnerKind Kind, string Id)
{
    public static DynamicsOwner Mix(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return new DynamicsOwner(DynamicsOwnerKind.Mix, sequence.Id);
    }

    /// <summary>La clave con la que la mezcla publica su medidor (<see cref="DynamicsMonitor"/>).</summary>
    public string MonitorKey => Kind switch
    {
        DynamicsOwnerKind.Clip => DynamicsMonitor.ForClip(Id),
        DynamicsOwnerKind.Track => DynamicsMonitor.ForTrack(Id),
        DynamicsOwnerKind.Bus => DynamicsMonitor.ForBus(Id),
        _ => DynamicsMonitor.ForMix(),
    };

    /// <summary>La pila de efectos del dueño; nula si ya no está en la secuencia.</summary>
    public List<EffectInstance>? Effects(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return Kind switch
        {
            DynamicsOwnerKind.Clip => sequence.FindClip(Id)?.Clip.Effects,
            DynamicsOwnerKind.Track => sequence.FindTrack(Id) is { Kind: TrackKind.Audio } track ? track.Effects : null,
            DynamicsOwnerKind.Bus => sequence.FindBus(Id)?.Effects,
            _ => sequence.Id == Id ? sequence.MasterEffects : null,
        };
    }
}

/// <summary>
/// Edita el compresor de un clip, una pista o la mezcla en el cursor, como <see cref="ClipPropertyEditor"/>: sin keyframes
/// cambia el valor fijo; con keyframes (umbral, relación y mezcla), crea o ajusta el del cursor. Los keyframes de un clip
/// van en su tiempo; los de una pista o la mezcla, en el de la secuencia.
/// </summary>
/// <remarks>Un arrastre es un paso del historial: los cambios del mismo parámetro se funden.</remarks>
public sealed class DynamicsEditor(EditorSession session)
{
    private const string Type = BuiltInEffects.Compressor;

    private string? _dragName;
    private string? _dragDescription;
    private Time _dragAt;
    private bool _dragGrouped;
    private bool _dragFailed;

    public EditorSession Session { get; } = session ?? throw new ArgumentNullException(nameof(session));

    public DynamicsOwner? Owner { get; set; }

    /// <summary>El compresor que aplica el motor: el primero activo, o el primero si están todos apagados.</summary>
    public EffectInstance? Compressor() =>
        Owner?.Effects(Session.Sequence) is { } effects
            ? effects.Find(e => e.Type == Type && e.Enabled) ?? effects.Find(e => e.Type == Type)
            : null;

    /// <summary>El cursor en el tiempo de los keyframes del dueño, y si cae dentro (un clip solo se anima donde está).</summary>
    public (Time At, bool Inside) KeyframeTime()
    {
        Time position = Session.EditPosition;
        if (Owner is { Kind: DynamicsOwnerKind.Clip } owner && Session.Sequence.FindClip(owner.Id) is { } found)
        {
            Clip clip = found.Clip;
            return (EffectCommands.KeyframeTime(clip, Time.Max(clip.Start, Time.Min(position, clip.End))), clip.Start <= position && position < clip.End);
        }

        return (Time.Max(Time.Zero, position), true);
    }

    public ParameterState Read(string name)
    {
        double fallback = Rodaje.Core.Effects.EffectRegistry.DefaultValue(Type, name);
        if (Compressor() is not { } effect || !effect.Parameters.TryGetValue(name, out Parameter? parameter))
        {
            return new ParameterState(fallback, 0, null, null, null);
        }

        (Time at, _) = KeyframeTime();
        Time tolerance = Time.FromTicks(Time.FromFrames(1, Session.Sequence.FrameRate).Ticks / 2);
        List<Keyframe> keys = parameter.Keyframes;
        Keyframe? here = keys.Find(k => (k.Time >= at ? k.Time - at : at - k.Time) <= tolerance);
        Keyframe? previous = keys.FindLast(k => k.Time < at - tolerance);
        Keyframe? next = keys.Find(k => k.Time > at + tolerance);
        return new ParameterState(parameter.Evaluate(at), keys.Count, here, previous is null ? null : ToTimeline(previous.Time),
            next is null ? null : ToTimeline(next.Time));
    }

    /// <summary>Lo que dibuja el panel: la curva con los valores que se ven en el cursor.</summary>
    public (double Threshold, double Ratio, double Knee, double Makeup) Curve()
    {
        double makeup = Read(K.Makeup).Value;
        double threshold = Read(K.Threshold).Value;
        double ratio = Read(K.Ratio).Value;
        if (Read(K.AutoMakeup).Value >= 0.5)
        {
            makeup += Rodaje.Audio.Processing.CompressorSettings.AutoMakeupDb(threshold, ratio);
        }

        return (threshold, ratio, Read(K.Knee).Value, makeup);
    }

    public void BeginChange(string name, string description)
    {
        EndChange();
        _dragGrouped = Compressor() is null;
        if (_dragGrouped)
        {
            Session.BeginGesture(description);
        }
        else
        {
            Session.BreakMerge();
        }

        _dragName = name;
        _dragDescription = description;
        _dragAt = KeyframeTime().At;
        _dragFailed = false;
    }

    /// <summary>Aplica el valor del arrastre en curso; <c>false</c> si no se pudo (y la sesión ya avisó).</summary>
    public bool Change(double value)
    {
        if (_dragName is not { } name || _dragFailed || Owner is not { } owner || EnsureCompressor() is not { } effectId)
        {
            _dragFailed = _dragName is not null;
            return false;
        }

        EffectInstance effect = Compressor()!;
        Time at = _dragAt;
        if (effect.Parameters.TryGetValue(name, out Parameter? parameter) && parameter.Keyframes.Count > 0)
        {
            if (!KeyframeTime().Inside)
            {
                return false;
            }

            at = Read(name).KeyframeHere?.Time ?? at;
        }

        bool done = Session.Execute(Edits.SetParameter(Session.SequenceId, owner.Id, effectId, name, value, at, _dragDescription!));
        _dragFailed = !done;
        return done;
    }

    public void EndChange()
    {
        if (_dragName is null)
        {
            return;
        }

        _dragName = null;
        if (_dragGrouped)
        {
            Session.EndGesture();
        }
        else
        {
            Session.BreakMerge();
        }
    }

    /// <summary>Un cambio suelto (un interruptor o un número escrito).</summary>
    public bool SetValue(string name, double value, string description)
    {
        BeginChange(name, description);
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
    public bool ToggleKeyframe(string name)
    {
        if (Owner is not { } owner || !KeyframeTime().Inside)
        {
            return false;
        }

        ParameterState state = Read(name);
        if (state.KeyframeHere is { } existing)
        {
            return Session.Execute(Edits.RemoveKeyframe(Session.SequenceId, owner.Id, Compressor()!.Id, name, existing.Time));
        }

        (Time at, _) = KeyframeTime();
        Session.BeginGesture("Añadir keyframe");
        try
        {
            return EnsureCompressor() is { } id && Session.Execute(Edits.SetKeyframe(Session.SequenceId, owner.Id, id, name, at, state.Value));
        }
        finally
        {
            Session.EndGesture();
        }
    }

    public bool SetInterpolation(string name, Interpolation interpolation)
    {
        if (Owner is not { } owner || Compressor() is not { } effect || Read(name).KeyframeHere is not { } keyframe || keyframe.Interpolation == interpolation)
        {
            return false;
        }

        return Session.Execute(Edits.SetKeyframeInterpolation(Session.SequenceId, owner.Id, effect.Id, name, keyframe.Time, interpolation));
    }

    public bool GoToKeyframe(string name, int direction)
    {
        ParameterState state = Read(name);
        if ((direction < 0 ? state.Previous : state.Next) is not { } target)
        {
            return false;
        }

        Session.Seek(target);
        return true;
    }

    /// <summary>Vuelve los parámetros al valor de fábrica (sin keyframes), en un paso.</summary>
    public bool Reset(IEnumerable<string> names, string description)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (Owner is not { } owner || Compressor() is not { } effect)
        {
            return false;
        }

        var defaults = names.Where(effect.Parameters.ContainsKey)
            .ToDictionary(n => n, n => Rodaje.Core.Effects.EffectRegistry.DefaultValue(Type, n));
        return defaults.Count > 0 && Session.Execute(Edits.ResetParameters(Session.SequenceId, owner.Id, effect.Id, defaults, description));
    }

    /// <summary>Quita los keyframes del parámetro y lo deja fijo en el valor que se ve en el cursor, en un paso.</summary>
    public bool ClearKeyframes(string name, string description)
    {
        if (Owner is not { } owner || Compressor() is not { } effect || Read(name) is not { KeyframeCount: > 0 } state)
        {
            return false;
        }

        return Session.Execute(Edits.ResetParameters(Session.SequenceId, owner.Id, effect.Id, new Dictionary<string, double> { [name] = state.Value }, description));
    }

    /// <summary>La pista cuya mezcla dispara el compresor; nula, su propio audio. Solo en un clip o una pista.</summary>
    public string? Sidechain => Compressor()?.Options.GetValueOrDefault(BuiltInEffects.CompressorOptions.Sidechain);

    public bool SetSidechain(string? trackId)
    {
        if (Owner is not { Kind: DynamicsOwnerKind.Clip or DynamicsOwnerKind.Track } owner || (trackId ?? "") == (Sidechain ?? "")
            || (trackId is not null && !SidechainCandidates().Any(t => t.Id == trackId)))
        {
            return false;
        }

        Session.BeginGesture(trackId is null ? "Quitar sidechain" : "Elegir sidechain");
        try
        {
            return EnsureCompressor() is { } id
                && Session.Execute(Edits.SetEffectOption(Session.SequenceId, owner.Id, id, BuiltInEffects.CompressorOptions.Sidechain, trackId ?? ""));
        }
        finally
        {
            Session.EndGesture();
        }
    }

    /// <summary>Las pistas de audio que pueden hacer de sidechain: las demás de la secuencia (no la del propio dueño).</summary>
    public IReadOnlyList<Track> SidechainCandidates()
    {
        if (Owner is not { Kind: DynamicsOwnerKind.Clip or DynamicsOwnerKind.Track } owner)
        {
            return [];
        }

        string? own = owner.Kind == DynamicsOwnerKind.Track ? owner.Id : Session.Sequence.FindClip(owner.Id)?.Track.Id;
        return [.. Session.Sequence.AudioTracks.Where(t => t.Id != own)];
    }

    public bool ApplyPreset(AudioPreset? preset) =>
        Owner is { } owner && Session.Execute(AudioPresetCommands.Apply(Session.SequenceId, owner.Id, preset));

    public AudioPreset? MatchingPreset(out bool hasEffects)
    {
        hasEffects = false;
        return Owner?.Effects(Session.Sequence) is { } effects ? AudioPresetCommands.Match(effects, out hasEffects) : null;
    }

    public bool Add() => Owner is not null && Compressor() is null && EnsureCompressor() is not null;

    public bool Remove() =>
        Owner is { } owner && Compressor() is { } effect && Session.Execute(Edits.RemoveEffect(Session.SequenceId, owner.Id, effect.Id, description: "Quitar compresor"));

    public bool SetEnabled(bool enabled) =>
        Owner is { } owner && Compressor() is { } effect && effect.Enabled != enabled
        && Session.Execute(Edits.SetEffectEnabled(Session.SequenceId, owner.Id, effect.Id, enabled, description: enabled ? "Activar compresor" : "Desactivar compresor"));

    private string? EnsureCompressor()
    {
        if (Compressor() is { } existing)
        {
            return existing.Id;
        }

        if (Owner is not { } owner)
        {
            return null;
        }

        var command = Edits.EnsureEffect(Session.SequenceId, owner.Id, Type, description: "Añadir compresor");
        return Session.Execute(command) ? command.CreatedIds[0] : null;
    }

    private Time ToTimeline(Time keyframeTime) =>
        Owner is { Kind: DynamicsOwnerKind.Clip } owner && Session.Sequence.FindClip(owner.Id) is { } found
            ? EffectCommands.TimelineOfKeyframe(found.Clip, keyframeTime)
            : keyframeTime;
}
