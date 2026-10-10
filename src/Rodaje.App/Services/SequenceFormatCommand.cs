using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

/// <summary>Cambia la tasa y el tamaño de una secuencia vacía (al importar el primer video, la secuencia toma su formato).</summary>
internal sealed class SequenceFormatCommand : ICommand
{
    private readonly string _sequenceId;
    private readonly Rational _frameRate;
    private readonly int _width;
    private readonly int _height;
    private (Rational FrameRate, int Width, int Height)? _previous;

    public SequenceFormatCommand(string sequenceId, Rational frameRate, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(sequenceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _sequenceId = sequenceId;
        _frameRate = frameRate;
        _width = width;
        _height = height;
    }

    public string Description => "Formato de la secuencia";

    public void Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Sequence sequence = project.GetSequence(_sequenceId);
        if (sequence.AllTracks.Any(t => t.Clips.Count > 0))
        {
            throw new EditException("Solo se puede cambiar el formato de una secuencia vacía.");
        }

        _previous = (sequence.FrameRate, sequence.Width, sequence.Height);
        sequence.FrameRate = _frameRate;
        sequence.Width = _width;
        sequence.Height = _height;
    }

    public void Revert(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_previous is not { } previous)
        {
            throw new InvalidOperationException("No se puede revertir un comando que todavía no se ha aplicado.");
        }

        Sequence sequence = project.GetSequence(_sequenceId);
        (sequence.FrameRate, sequence.Width, sequence.Height) = previous;
    }

    public bool TryMerge(ICommand following) => false;
}
