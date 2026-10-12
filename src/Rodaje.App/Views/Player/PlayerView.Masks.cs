using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Windows.Foundation;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;

namespace Rodaje.App.Views.Player;

/// <summary>
/// MÃ¡scaras en el visor, con la pestaÃ±a Â«MÃ¡scarasÂ» a la vista: el contorno de la elegida, arrastrar dentro la mueve y una
/// esquina la agranda alrededor de su centro (un paso del historial por arrastre; con keyframes, el del cursor). Â«Trazo
/// libreÂ» dibuja el contorno con el ratÃ³n y lo vuelve una mÃ¡scara nueva; Esc lo cancela.
/// </summary>
internal sealed partial class PlayerView
{
    private MaskBox? _maskStart;
    private MaskGrip _maskGrip;
    private Vector2 _maskFrom;
    private Time _maskAt;
    private string? _drawClipId;
    private readonly List<Vector2> _stroke = [];

    private void AttachMaskHandles()
    {
        Session!.MaskEditingChanged += (_, _) => UpdateMaskHandles();
        Session.SelectionChanged += (_, _) => UpdateMaskHandles();
        Session.ProjectEdited += (_, _) => UpdateMaskHandles();
        Session.TransportChanged += (_, _) => UpdateMaskHandles();
        Session.MaskDrawRequested += (_, clipId) => BeginMaskDrawing(clipId);
        VideoPanel.SizeChanged += (_, _) => UpdateMaskHandles();
        KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && _drawClipId is not null)
            {
                EndMaskDrawing(commit: false);
                e.Handled = true;
            }
        };
    }

    private MaskBox? EditedMask()
    {
        if (Session is not { IsMaskEditing: true, EditedMaskId: { } maskId } session)
        {
            return null;
        }

        foreach (string id in session.SelectedClipIds)
        {
            if (session.Sequence.FindClip(id) is { } found && found.Clip.Effects.Find(e => e.Id == maskId && e.Enabled) is { } effect)
            {
                return MaskHandles.Find(session.Project, session.Sequence, found.Clip, effect, session.Position);
            }
        }

        return null;
    }

    private void UpdateMaskHandles()
    {
        if (Image() is not { } frame)
        {
            MaskCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        if (_drawClipId is not null)
        {
            MaskCanvas.Visibility = Visibility.Visible;
            MaskOutline.Points = [];
            SetGrips(null);
            return;
        }

        MaskBox? box = _maskStart is null ? EditedMask() : EditedMask() ?? _maskStart;
        if (box is not { } shown || shown.Mask.Shape == MaskShape.Image)
        {
            MaskCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        MaskCanvas.Visibility = Visibility.Visible;
        var points = new PointCollection();
        foreach (Vector2 p in MaskHandles.Outline(shown))
        {
            Vector2 panel = frame.ToPanel(p);
            points.Add(new Point(panel.X, panel.Y));
        }

        MaskOutline.Points = points;
        MaskStroke.Points = [];
        SetGrips([.. MaskHandles.Corners(shown).Select(frame.ToPanel)]);
    }

    private void SetGrips(Vector2[]? corners)
    {
        Rectangle[] grips = [MaskGripTopLeft, MaskGripTopRight, MaskGripBottomRight, MaskGripBottomLeft];
        for (int i = 0; i < 4; i++)
        {
            grips[i].Visibility = corners is null ? Visibility.Collapsed : Visibility.Visible;
            if (corners is not null)
            {
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(grips[i], corners[i].X - (grips[i].Width / 2));
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(grips[i], corners[i].Y - (grips[i].Height / 2));
            }
        }
    }

    /// <summary>Atiende el clic si es sobre la mÃ¡scara elegida o si se estÃ¡ dibujando un trazo.</summary>
    private bool MaskPointerPressed(PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(VideoPanel);
        if (!point.Properties.IsLeftButtonPressed || Session is null || Image() is not { } frame)
        {
            return false;
        }

        var panel = new Vector2((float)point.Position.X, (float)point.Position.Y);
        if (_drawClipId is not null)
        {
            _stroke.Clear();
            _stroke.Add(frame.ToSequence(panel.X, panel.Y));
            VideoPanel.CapturePointer(e.Pointer);
            e.Handled = true;
            return true;
        }

        if (EditedMask() is not { } box || box.Mask.Shape == MaskShape.Image
            || Session.Sequence.FindClip(box.ClipId) is not { } found || _properties is null || !_properties.IsPlayheadInside(found.Clip))
        {
            return false;
        }

        MaskGrip grip = MaskHandles.Hit([.. MaskHandles.Corners(box).Select(frame.ToPanel)], panel);
        if (grip == MaskGrip.None)
        {
            return false;
        }

        _maskStart = box;
        _maskGrip = grip;
        _maskFrom = MaskHandles.ToImage(box.Layer, frame.ToSequence(panel.X, panel.Y));
        _maskAt = _properties.KeyframeTimeAtPlayhead(found.Clip);
        Session.BeginGesture(grip == MaskGrip.Body ? "Mover la mÃ¡scara" : "Cambiar el tamaÃ±o de la mÃ¡scara");
        VideoPanel.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private bool MaskPointerMoved(PointerRoutedEventArgs e)
    {
        if (Image() is not { } frame || Session is null)
        {
            return false;
        }

        Point position = e.GetCurrentPoint(VideoPanel).Position;
        Vector2 sequence = frame.ToSequence(position.X, position.Y);
        if (_drawClipId is not null)
        {
            if (_stroke.Count > 0 && Vector2.Distance(_stroke[^1], sequence) * frame.Scale >= 2)
            {
                _stroke.Add(sequence);
                MaskStroke.Points.Add(position);
            }

            e.Handled = true;
            return true;
        }

        if (_maskStart is not { } start)
        {
            return false;
        }

        Vector2 now = MaskHandles.ToImage(start.Layer, sequence);
        string clipId = start.ClipId, effectId = start.EffectId;
        Time at = _maskAt;
        if (_maskGrip == MaskGrip.Body)
        {
            (double x, double y) = MaskHandles.Moved(start, _maskFrom, now);
            Session.Execute(new SequenceEditCommand("Mover la mÃ¡scara", Session.SequenceId, ctx =>
            {
                EffectEdits.SetParameter(ctx, clipId, effectId, M.X, Math.Round(x, 4), at);
                EffectEdits.SetParameter(ctx, clipId, effectId, M.Y, Math.Round(y, 4), at);
            }, mergeKey: $"mascara-mover|{effectId}|{at.Ticks}"));
        }
        else
        {
            (double width, double height) = MaskHandles.Resized(start, now);
            Session.Execute(new SequenceEditCommand("Cambiar el tamaÃ±o de la mÃ¡scara", Session.SequenceId, ctx =>
            {
                EffectEdits.SetParameter(ctx, clipId, effectId, M.Width, Math.Round(width, 4), at);
                EffectEdits.SetParameter(ctx, clipId, effectId, M.Height, Math.Round(height, 4), at);
            }, mergeKey: $"mascara-tamano|{effectId}|{at.Ticks}"));
        }

        e.Handled = true;
        return true;
    }

    private bool MaskPointerReleased()
    {
        if (_drawClipId is not null)
        {
            if (_stroke.Count > 0)
            {
                EndMaskDrawing(commit: true);
            }

            return true;
        }

        if (_maskStart is null)
        {
            return false;
        }

        _maskStart = null;
        Session?.EndGesture();
        VideoPanel.ReleasePointerCaptures();
        UpdateMaskHandles();
        return true;
    }

    private void BeginMaskDrawing(string clipId)
    {
        if (Session?.Sequence.FindClip(clipId) is not { } found || !(_properties?.IsPlayheadInside(found.Clip) ?? false))
        {
            Session?.Notify(NotificationSeverity.Informational, "Trazo libre", "Lleva el cursor sobre el clip para dibujar su mÃ¡scara en el visor.");
            return;
        }

        _drawClipId = clipId;
        _stroke.Clear();
        MaskStroke.Points = [];
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
        StatusText.Text = "Dibuja el contorno con el ratÃ³n; al soltar se cierra. Esc cancela.";
        UpdateMaskHandles();
        Focus(FocusState.Programmatic);
    }

    private void EndMaskDrawing(bool commit)
    {
        string? clipId = _drawClipId;
        _drawClipId = null;
        ProtectedCursor = null;
        StatusText.Text = "";
        VideoPanel.ReleasePointerCaptures();
        MaskStroke.Points = [];
        if (commit && clipId is not null && Session?.Sequence.FindClip(clipId) is { } found && _stroke.Count >= 3
            && Session.Project.FindMedia(found.Clip.MediaId) is { Width: > 0, Height: > 0 } media
            && Rodaje.Render.LayerLayout.Place(Session.Sequence.Width, Session.Sequence.Height, media.Width, media.Height,
                MaskHandles.TransformAt(found.Clip, found.Clip.ToEffectTime(Session.Position))) is { } layer)
        {
            Vector2[] image = [.. _stroke.Select(p => MaskHandles.ToImage(layer, p) / new Vector2(layer.LocalWidth, layer.LocalHeight))];
            (double x, double y, double width, double height, Vector2[] points) = MaskGeometry.FromStroke(image);
            if (points.Length >= 3 && width * layer.LocalWidth >= 4 && height * layer.LocalHeight >= 4)
            {
                Session.Execute(Edits.AddMask(Session.SequenceId, clipId, MaskShape.Path, x, y, width, height, points));
            }
        }

        _stroke.Clear();
        UpdateMaskHandles();
    }
}
