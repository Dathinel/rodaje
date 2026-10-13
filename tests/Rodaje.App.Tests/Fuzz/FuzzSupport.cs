using Rodaje.App.Services;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Tests.Fuzz;

/// <summary>
/// Reproductor de mentira para el fuzz: guarda solo la última copia recibida (las listas de <c>FakePlayback</c> crecerían
/// sin límite en miles de pasos) y lleva la posición que le pide la sesión, como el motor de verdad.
/// </summary>
internal sealed class FuzzPlayback : IPlaybackEngine
{
    public Project? Last { get; private set; }

    public string? LastSequenceId { get; private set; }

    public PlaybackSnapshot Snapshot { get; private set; } = PlaybackSnapshot.Empty;

    public void Load(Project project, string sequenceId)
    {
        Last = project;
        LastSequenceId = sequenceId;
        Snapshot = Snapshot with { Position = Time.Zero, Duration = DurationOf(project, sequenceId) };
    }

    public void Reload(Project project)
    {
        Last = project;
        Snapshot = Snapshot with { Duration = LastSequenceId is { } id ? DurationOf(project, id) : Time.Zero };
    }

    public void Play()
    {
    }

    public void Pause()
    {
    }

    public void TogglePlayPause()
    {
    }

    public void Seek(Time position) => Snapshot = Snapshot with { Position = position };

    public void StepFrames(int frames)
    {
    }

    public void SetProxyUsage(ProxyUsage usage)
    {
    }

    private static Time DurationOf(Project project, string sequenceId) =>
        project.FindSequence(sequenceId)?.Duration ?? Time.Zero;
}

/// <summary>Identificadores reproducibles que no chocan con los de <c>RandomTimeline</c> (<c>clip-1</c>, <c>clip-2</c>...).</summary>
internal sealed class FuzzIds : IIdGenerator
{
    private int _next;

    public string NewId(string prefix) => FormattableString.Invariant($"{prefix}-z{++_next}");
}
