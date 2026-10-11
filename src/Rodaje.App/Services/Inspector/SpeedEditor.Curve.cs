using Rodaje.Core.Commands;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Inspector;

/// <summary>Una opción del selector de curvas: sin curva, la propia del usuario o un preajuste.</summary>
public sealed record CurveOption(string Name, SpeedPreset? Preset, bool IsCustom);

/// <summary>La curva del clip que muestra la pestaña.</summary>
/// <param name="Points">Nulo si el clip no tiene curva.</param>
/// <param name="Option">La opción del selector (<see cref="SpeedEditor.CurveOptions"/>) que corresponde a la curva.</param>
/// <param name="From">Dónde empieza el clip en la curva (diezmilésimas; distinto de 0 en un trozo de un clip dividido).</param>
/// <param name="To">Dónde termina.</param>
/// <param name="CanEdit">No es un cuadro congelado.</param>
public sealed record CurveState(string ClipId, IReadOnlyList<SpeedPoint>? Points, int Option, double From, double To, bool CanEdit)
{
    public const int NoneOption = 0;
    public const int CustomOption = 1;
}

public sealed partial class SpeedEditor
{
    private PointDrag? _pointDrag;

    /// <summary>Ninguna, Personalizada y los preajustes propios, en ese orden.</summary>
    public static IReadOnlyList<CurveOption> CurveOptions { get; } =
    [
        new("Ninguna", null, false),
        new("Personalizada", null, true),
        .. SpeedPresets.All.Select(p => new CurveOption(SpeedPresets.Name(p), p, false)),
    ];

    /// <summary>Se está arrastrando un punto de la curva.</summary>
    public bool IsDraggingPoint => _pointDrag is not null;

    /// <summary>El punto que se arrastra y la forma al empezar, para dibujar y calcular desde ahí.</summary>
    public (int Index, IReadOnlyList<SpeedPoint> StartPoints)? PointDragState =>
        _pointDrag is { } drag ? (drag.Index, drag.StartCurve.Points) : null;

    public CurveState? ReadCurve() => Target() is { } clip ? ReadCurve(clip) : null;

    public static CurveState ReadCurve(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        SpeedCurve? curve = clip.Speed is { Freeze: false } speed ? speed.Curve : null;
        (double from, double to) = SpeedCurveMath.Window(clip);
        return new CurveState(clip.Id, curve?.Points, OptionOf(curve), from, to, clip.Speed is not { Freeze: true });
    }

    /// <summary>La opción del selector para una curva: un preajuste si coincide punto por punto (<see cref="SpeedPresets.Match"/>).</summary>
    public static int OptionOf(SpeedCurve? curve)
    {
        if (curve is null)
        {
            return CurveState.NoneOption;
        }

        if (SpeedPresets.Match(curve) is { } preset)
        {
            for (int i = 0; i < CurveOptions.Count; i++)
            {
                if (CurveOptions[i].Preset == preset)
                {
                    return i;
                }
            }
        }

        return CurveState.CustomOption;
    }

    /// <summary>
    /// Elige una opción del selector, en un paso del historial: «Ninguna» vuelve a 1×, un preajuste pone su forma
    /// (<c>Edits.ApplySpeedPreset</c>) y «Personalizada» sin curva empieza con una plana a la velocidad de ahora (con una
    /// curva ya puesta no cambia nada: se personaliza moviendo sus puntos).
    /// </summary>
    public bool ChooseCurve(int option)
    {
        EndChange();
        if (option < 0 || option >= CurveOptions.Count || Target() is not { } clip || ReadCurve(clip) is not { CanEdit: true } state
            || state.Option == option)
        {
            return false;
        }

        CurveOption chosen = CurveOptions[option];
        ICommand command;
        if (chosen.Preset is { } preset)
        {
            command = Commands.ApplySpeedPreset(Session.SequenceId, clip.Id, preset, Session.SpeedScope);
        }
        else if (chosen.IsCustom)
        {
            if (state.Points is not null)
            {
                return false;
            }

            var flat = new SpeedCurve { Span = clip.Duration, Points = SpeedCurveMath.Flat(Read(clip).Rate.ToDouble()) };
            command = Commands.SetCurve(Session.SequenceId, clip.Id, flat, Session.SpeedScope);
        }
        else
        {
            command = Commands.SetCurve(Session.SequenceId, clip.Id, null, Session.SpeedScope);
        }

        return ExecuteStep(command);
    }

    /// <summary>Empieza a arrastrar el punto <paramref name="index"/> de la curva del clip de ahora.</summary>
    public bool BeginPointDrag(int index)
    {
        EndChange();
        if (Target() is not { Speed: { Freeze: false, Curve: { } curve } } clip || index < 0 || index >= curve.Points.Count)
        {
            return false;
        }

        Session.BeginGesture("Cambiar curva de velocidad");
        _pointDrag = new PointDrag(clip.Id, SequenceSnapshot.Capture(Session.Sequence), curve.Clone(), index);
        return true;
    }

    /// <summary>
    /// Lleva el punto arrastrado a (<paramref name="at"/>, <paramref name="hundredths"/>), siempre desde la forma del
    /// inicio del gesto; <c>false</c> si no se pudo (y la sesión ya avisó).
    /// </summary>
    public bool DragPoint(int at, int hundredths)
    {
        if (_pointDrag is not { Failed: false } drag || Session.Sequence.FindClip(drag.ClipId) is not { } found)
        {
            return false;
        }

        List<SpeedPoint> next = SpeedCurveMath.Move(drag.StartCurve.Points, drag.Index, at, hundredths);
        if (found.Clip.Speed?.Curve is { } current && current.Points.SequenceEqual(next))
        {
            return true;
        }

        ICommand? step = next.SequenceEqual(drag.StartCurve.Points)
            ? null
            : Commands.SetCurve(Session.SequenceId, drag.ClipId, new SpeedCurve { Span = drag.StartCurve.Span, Points = next }, Session.SpeedScope);
        drag.Failed = !Session.Execute(new FromGestureStart(Session.SequenceId, drag.Start, step));
        return !drag.Failed;
    }

    /// <summary>Suelta el punto: el arrastre queda como un paso del historial.</summary>
    public void EndPointDrag()
    {
        if (_pointDrag is null)
        {
            return;
        }

        _pointDrag = null;
        if (Session.IsInGesture)
        {
            Session.EndGesture();
        }
    }

    /// <summary>
    /// Doble clic: quita el punto <paramref name="index"/> o, si es nulo, añade uno en <paramref name="at"/> sobre la curva.
    /// Un paso del historial; los extremos no se quitan y la curva tiene de 2 a 64 puntos.
    /// </summary>
    public bool AddOrRemovePoint(int at, int? index)
    {
        EndChange();
        if (Target() is not { Speed: { Freeze: false, Curve: { } curve } } clip)
        {
            return false;
        }

        List<SpeedPoint>? next = index is { } i ? SpeedCurveMath.Remove(curve.Points, i) : SpeedCurveMath.Add(curve.Points, at);
        return next is not null
            && ExecuteStep(Commands.SetCurve(Session.SequenceId, clip.Id, new SpeedCurve { Span = curve.Span, Points = next }, Session.SpeedScope));
    }

    private bool ExecuteStep(ICommand command)
    {
        Session.BreakMerge();
        bool done = Session.Execute(command);
        Session.BreakMerge();
        return done;
    }

    private sealed record PointDrag(string ClipId, SequenceSnapshot Start, SpeedCurve StartCurve, int Index)
    {
        public bool Failed { get; set; }
    }
}
