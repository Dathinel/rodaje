using Rodaje.App.Services;
using Rodaje.Core.Commands;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// Un arrastre de clip de principio a fin contra la sesión: abre un gesto, ejecuta en vivo los pasos posibles (se
/// funden por clave y el reproductor los ve enseguida), guarda para el final los que solo valen al soltar y cierra el
/// gesto como un paso del historial. Esc lo cancela entero.
/// </summary>
public sealed class ClipDragSession
{
    private readonly EditorSession _session;
    private Func<ICommand>? _pending;
    private bool _open;

    public ClipDragSession(EditorSession session, ClipDrag drag)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(drag);
        _session = session;
        Drag = drag;
    }

    public ClipDrag Drag { get; }

    /// <summary>El último plan calculado: dónde se dibuja el clip, la guía de ajuste y el corte magnético.</summary>
    public DragPlan? Plan { get; private set; }

    /// <summary>Cuántos pasos se ejecutaron en vivo (para medir y para las pruebas).</summary>
    public int LiveSteps { get; private set; }

    public bool IsOpen => _open && _session.IsInGesture;

    public static string Description(DragOperation operation) => operation switch
    {
        DragOperation.Move => "Mover clip",
        DragOperation.TrimStart => "Recortar inicio",
        DragOperation.TrimEnd => "Recortar final",
        DragOperation.StretchStart or DragOperation.StretchEnd => "Cambiar velocidad",
        _ => "Slip",
    };

    public void Begin()
    {
        _session.BeginGesture(Description(Drag.Operation));
        _open = true;
    }

    /// <summary>Un movimiento del puntero; devuelve el plan para dibujar.</summary>
    public DragPlan Move(Time position, TrackRow? row)
    {
        DragPlan plan = Drag.Update(_session.Project, position, row);
        Plan = plan;
        _pending = plan.IsLive ? null : plan.Build;
        // Sin ensayo sobre una copia: un paso que no cabe se revierte solo y no se avisa.
        if (IsOpen && plan.IsLive && plan.Build is { } build && _session.TryExecute(build()))
        {
            LiveSteps++;
        }

        return plan;
    }

    /// <summary>Suelta: ejecuta lo que quedó pendiente (si falla, la sesión avisa el porqué) y cierra el paso.</summary>
    public void Complete()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        try
        {
            if (_pending is { } pending && _session.IsInGesture)
            {
                _session.Execute(pending());
            }
        }
        finally
        {
            if (_session.IsInGesture)
            {
                _session.EndGesture();
            }
        }
    }

    public void Cancel()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        _session.CancelGesture();
    }
}
