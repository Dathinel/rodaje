using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services;
using Rodaje.App.Services.Packages;
using Rodaje.Core.Commands;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Rodaje.App.Views.Timeline;

/// <summary>Transiciones en la línea de tiempo: seleccionar, cambiar la duración con las manijas, el fundido del símbolo y soltar.</summary>
internal sealed partial class TimelineView
{
    private string? _transitionId;
    private string? _dropTransition;
    // Nulo mientras se lee lo arrastrado: se acepta por si acaso hasta saberlo.
    private bool? _dropIsTransition;

    private Track? SymbolTrack => State.AutoCrossfade ? Session!.MainTrack : null;

    private TransitionHit HitTransition(TrackLayout layout, Point p) =>
        TransitionPlanning.Hit(layout, Viewport, p.X, ContentY(p.Y), SymbolTrack);

    /// <summary>Un clic sobre una transición o un símbolo de fundido; <c>false</c> si no había ninguno.</summary>
    private bool TryPressTransition(TrackLayout layout, Point p, PointerRoutedEventArgs e)
    {
        EditorSession session = Session!;
        TransitionHit hit = HitTransition(layout, p);
        switch (hit.Kind)
        {
            case TransitionHitKind.None:
                return false;
            case TransitionHitKind.AutoCrossfade:
                SequenceEditCommand add = TransitionPlanning.BuildAutoCrossfade(session.SequenceId, hit.From!.Id);
                if (session.Execute(add) && add.CreatedIds.Count > 0)
                {
                    session.SelectTransition(add.CreatedIds[0]);
                }

                return true;
            case TransitionHitKind.Body:
                session.SelectTransition(hit.Transition!.Id);
                return true;
            default:
                session.SelectTransition(hit.Transition!.Id);
                if (hit.Row!.Track.Locked)
                {
                    return true;
                }

                _transitionId = hit.Transition.Id;
                _mode = PointerMode.DraggingTransition;
                session.BeginGesture("Cambiar duración de la transición");
                Capture(LanesHost, e);
                return true;
        }
    }

    private void ContinueTransitionDrag(Point p)
    {
        EditorSession session = Session!;
        if (_transitionId is not { } id || session.FindTransition(id) is not { } found)
        {
            return;
        }

        Time duration = TransitionPlanning.DurationForHandle(found.Track, found.Transition, Viewport.XToTime(p.X), session.Sequence.FrameRate);
        if (duration != found.Transition.Duration)
        {
            session.TryExecute(Edits.SetTransitionDuration(session.SequenceId, id, duration));
        }
    }

    private void FinishTransitionDrag()
    {
        _transitionId = null;
        if (Session!.IsInGesture)
        {
            Session.EndGesture();
        }
    }

    private static bool CarriesElement(DataPackageView view) =>
        view.Contains(TransitionPlanning.DragFormat) || view.Contains(LibraryElement.DragFormat);

    private async void ReadDroppedTransition(DragEventArgs e)
    {
        _dropTransition = null;
        _dropIsTransition = null;
        bool package = e.DataView.Contains(TransitionPlanning.DragFormat);
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            string format = package ? TransitionPlanning.DragFormat : LibraryElement.DragFormat;
            string? reference = (await e.DataView.GetDataAsync(format))?.ToString();
            bool isTransition = TransitionPlanning.IsTransitionReference(reference, package);
            _dropIsTransition = isTransition;
            _dropTransition = isTransition ? reference : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _dropTransition = null;
            _dropIsTransition = false;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>El corte bajo el puntero donde caería la transición arrastrada.</summary>
    private (Track Track, Clip From, Time Cut)? PlanTransitionDrop(DragEventArgs e)
    {
        if (_renderer?.Layout is not { } layout)
        {
            return null;
        }

        Point p = e.GetPosition(LanesHost);
        if (layout.RowStrictlyAt(ContentY(p.Y)) is not { } row || row.Track.Locked
            || (BuiltInTransitions.IsAudio(_dropTransition) && row.Track.Kind != TrackKind.Audio))
        {
            return null;
        }

        Time reach = Viewport.PixelsToDuration(TransitionPlanning.DropReachPixels);
        return TransitionPlanning.NearestCut(row.Track, Viewport.XToTime(p.X), reach) is { } cut ? (row.Track, cut.From, cut.Cut) : null;
    }

    private void DragOverTransition(DragEventArgs e)
    {
        e.Handled = true;
        (Track Track, Clip From, Time Cut)? target = _dropIsTransition != false ? PlanTransitionDrop(e) : null;
        e.AcceptedOperation = target is null ? DataPackageOperation.None : DataPackageOperation.Copy;
        if (target is { } t)
        {
            TransitionInstance? existing = t.Track.TransitionFrom(t.From.Id);
            var ghost = existing?.Clone() ?? new TransitionInstance { Duration = EffectRegistry.DefaultTransitionDuration };
            e.DragUIOverride.Caption = existing is null ? "Transición" : "Cambiar transición";
            _overlay.Ghost = (t.Track.Id, t.Cut - ghost.Before, t.Cut + ghost.After);
            _overlay.InsertLine = (t.Track.Id, t.Cut);
        }
        else
        {
            ClearDragOverlay();
        }

        Invalidate();
    }

    private void DropTransition(DragEventArgs e)
    {
        EditorSession session = Session!;
        e.Handled = true;
        try
        {
            if (_dropTransition is { } reference && PlanTransitionDrop(e) is { } target)
            {
                SequenceEditCommand command = TransitionPlanning.BuildDrop(session.SequenceId, target.From.Id, reference);
                if (session.Execute(command) && command.CreatedIds.Count > 0)
                {
                    session.SelectTransition(command.CreatedIds[0]);
                }
            }
        }
        finally
        {
            _dropTransition = null;
            _dropIsTransition = null;
            ClearDragOverlay();
            Invalidate();
        }
    }
}
