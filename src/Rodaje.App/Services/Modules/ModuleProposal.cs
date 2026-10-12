using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Modules;

/// <summary>Qué dibuja la vista previa de una propuesta.</summary>
public enum PreviewMarkKind
{
    /// <summary>Un tramo marcado (silencio a quitar, segmento de subtítulo).</summary>
    Range,

    /// <summary>Un instante (pulso, corte).</summary>
    Marker,

    /// <summary>Un clip que se va a crear, atenuado.</summary>
    Clip,

    /// <summary>Un keyframe (reencuadre, volumen).</summary>
    Keyframe,
}

/// <summary>Un elemento de la vista previa, en tiempo de la secuencia. <paramref name="TrackId"/> es la pista donde se verá, si aplica.</summary>
public sealed record PreviewMark(PreviewMarkKind Kind, Time Start, Time End, string? Label = null, string? TrackId = null);

/// <summary>
/// El contrato para convertir el resultado de un módulo en ediciones (la IA no edita): una descripción para el historial,
/// lo que se ve antes de aplicar y cómo construir los comandos del núcleo contra el proyecto <b>en el momento de aplicar</b>
/// (si la persona editó mientras tanto, se construyen sobre lo actual). Todos los comandos van en un solo paso del historial.
/// </summary>
/// <param name="Description">Texto del historial, por ejemplo «Subtítulos automáticos».</param>
/// <param name="SequenceId">La secuencia a la que se refiere; si cambió la que se ve, la propuesta se descarta.</param>
/// <param name="Preview">Lo que se dibuja antes de aplicar.</param>
/// <param name="Build">Los comandos; puede lanzar <see cref="Rodaje.Core.Editing.EditException"/> si ya no se puede.</param>
public sealed record ModuleProposal(string Description, string SequenceId, IReadOnlyList<PreviewMark> Preview, Func<Project, IReadOnlyList<ICommand>> Build)
{
    /// <summary>El comando único que va al historial.</summary>
    public ICommand CreateCommand(Project project)
    {
        IReadOnlyList<ICommand> commands = Build(project);
        return commands.Count == 1 ? commands[0] : new CompositeCommand(Description, commands);
    }
}

/// <summary>
/// La propuesta pendiente de la sesión: se muestra (la línea de tiempo y el visor dibujan <see cref="Pending"/>), y luego
/// se aplica en un paso (un Ctrl+Z la deshace entera) o se descarta. Abrir otro proyecto la descarta. Hilo de la interfaz.
/// </summary>
public sealed class ModuleProposals
{
    private readonly EditorSession _session;

    public ModuleProposals(EditorSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        session.ProjectOpened += (_, _) => Discard();
    }

    public ModuleProposal? Pending { get; private set; }

    public event EventHandler? Changed;

    /// <summary>Muestra la propuesta (reemplaza la anterior sin aplicarla).</summary>
    public void Show(ModuleProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        Pending = proposal;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Aplica la pendiente como un solo paso del historial. <c>false</c> (con aviso de la sesión) si ya no se puede.</summary>
    public bool Apply()
    {
        if (Pending is not { } proposal)
        {
            return false;
        }

        Pending = null;
        Changed?.Invoke(this, EventArgs.Empty);
        if (_session.Project.Sequences.All(s => s.Id != proposal.SequenceId))
        {
            _session.Notify(NotificationSeverity.Warning, "No se pudo aplicar", "La secuencia ya no existe.");
            return false;
        }

        ICommand command;
        try
        {
            command = proposal.CreateCommand(_session.Project);
        }
        catch (Exception ex) when (ex is Rodaje.Core.Editing.EditException or KeyNotFoundException or ArgumentException)
        {
            _session.Notify(NotificationSeverity.Warning, "No se pudo aplicar", ex.Message);
            return false;
        }

        return _session.Execute(command);
    }

    public void Discard()
    {
        if (Pending is null)
        {
            return;
        }

        Pending = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Tiempos de los resultados (segundos del búfer que se le dio al módulo) llevados a la secuencia, ajustados al cuadro.</summary>
public static class ModuleTime
{
    /// <summary>
    /// El instante de la secuencia donde se ve <paramref name="seconds"/> del tramo entregado, que empezaba en
    /// <paramref name="bufferStart"/> del origen del clip; <c>null</c> si cae fuera del clip. Respeta velocidad y reversa.
    /// </summary>
    public static Time? ToSequence(Clip clip, Time bufferStart, double seconds, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            return null;
        }

        ClipTimeMap map = ClipTimeMap.Of(clip);
        Time source = bufferStart + Time.FromSeconds(seconds);
        if (source < map.SourceIn || source > map.SourceOut)
        {
            return null;
        }

        Time t = map.TimelineOf(source).SnapToFrame(frameRate);
        return Time.Min(Time.Max(t, map.Start), map.End);
    }
}
