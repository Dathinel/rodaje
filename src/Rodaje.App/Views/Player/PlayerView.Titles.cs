using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Windows.Foundation;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Asas del título en el visor: el contorno del título seleccionado tal como lo dibujó el render, arrastrar el cuerpo lo
/// mueve y arrastrar una esquina lo escala alrededor de su centro. Un clic sobre otro título lo selecciona y lo arrastra.
/// </summary>
internal sealed partial class PlayerView
{
    private ClipPropertyEditor? _properties;
    private TitleTransformDrag? _titleDrag;
    private TitleGrip _titleGrip;
    private Vector2 _dragStart;
    private Vector2 _dragCenter;

    private void AttachTitleHandles()
    {
        _properties = new ClipPropertyEditor(Session!);
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher = DispatcherQueue;
        _engine!.TitleBoxesChanged += () => dispatcher.TryEnqueue(UpdateTitleHandles);
        Session!.SelectionChanged += (_, _) => UpdateTitleHandles();
        Session.ProjectEdited += (_, e) =>
        {
            // Esc revirtió el gesto desde la ventana: el resto del arrastre no hace nada.
            if (e.Kind == EditKind.GestureCancelled && _titleDrag is not null)
            {
                _titleDrag = null;
                VideoPanel.ReleasePointerCaptures();
            }
        };
    }

    /// <summary>Dónde pone el render la imagen dentro del panel, en unidades del panel; nulo sin tamaño.</summary>
    private ImageFrame? Image()
    {
        if (Session is null)
        {
            return null;
        }

        int sequenceWidth = Session.Sequence.Width;
        int sequenceHeight = Session.Sequence.Height;
        double width = VideoPanel.ActualWidth;
        double height = VideoPanel.ActualHeight;
        if (width <= 0 || height <= 0 || sequenceWidth <= 0 || sequenceHeight <= 0)
        {
            return null;
        }

        // Con zoom, el render pone un píxel de la secuencia por píxel físico multiplicado por el zoom.
        double scale = _zoom > 0
            ? _zoom / Math.Max(0.01, VideoPanel.CompositionScaleX)
            : Math.Min(width / sequenceWidth, height / sequenceHeight);
        return new ImageFrame((width - (sequenceWidth * scale)) / 2, (height - (sequenceHeight * scale)) / 2, scale);
    }

    private TitleBox? SelectedTitleBox()
    {
        if (Session is null || _engine is null)
        {
            return null;
        }

        IReadOnlyList<TitleBox> boxes = _engine.TitleBoxes;
        for (int i = boxes.Count - 1; i >= 0; i--)
        {
            if (Session.SelectedClipIds.Contains(boxes[i].ClipId))
            {
                return boxes[i];
            }
        }

        return null;
    }

    private void UpdateTitleHandles()
    {
        Vector2[]? corners = SelectedTitleBox() is { } box && Image() is { } frame
            ? TitleHandles.Corners(box, Session!.Sequence.Width, Session.Sequence.Height, frame)
            : null;
        TitleCanvas.Visibility = corners is null ? Visibility.Collapsed : Visibility.Visible;
        if (corners is null)
        {
            return;
        }

        var points = new PointCollection();
        foreach (Vector2 corner in corners)
        {
            points.Add(new Point(corner.X, corner.Y));
        }

        TitleOutline.Points = points;
        Rectangle[] grips = [TitleGripTopLeft, TitleGripTopRight, TitleGripBottomRight, TitleGripBottomLeft];
        for (int i = 0; i < 4; i++)
        {
            Canvas.SetLeft(grips[i], corners[i].X - (grips[i].Width / 2));
            Canvas.SetTop(grips[i], corners[i].Y - (grips[i].Height / 2));
        }

        Vector2 rotate = TitleHandles.RotateGrip(corners);
        Vector2 top = (corners[0] + corners[1]) / 2;
        Canvas.SetLeft(TitleGripRotate, rotate.X - (TitleGripRotate.Width / 2));
        Canvas.SetTop(TitleGripRotate, rotate.Y - (TitleGripRotate.Height / 2));
        TitleRotateStem.X1 = top.X;
        TitleRotateStem.Y1 = top.Y;
        TitleRotateStem.X2 = rotate.X;
        TitleRotateStem.Y2 = rotate.Y;
    }

    private void OnVideoPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(VideoPanel);
        if (!point.Properties.IsLeftButtonPressed || Session is null || _engine is null || _properties is null || Image() is not { } frame)
        {
            return;
        }

        (TitleBox Box, TitleGrip Grip)? target = null;
        IReadOnlyList<TitleBox> boxes = _engine.TitleBoxes;
        for (int i = boxes.Count - 1; i >= 0 && target is null; i--)
        {
            if (TitleHandles.Corners(boxes[i], Session.Sequence.Width, Session.Sequence.Height, frame) is { } corners)
            {
                TitleGrip grip = TitleHandles.Hit(corners, point.Position.X, point.Position.Y, Session.SelectedClipIds.Contains(boxes[i].ClipId));
                if (grip != TitleGrip.None)
                {
                    target = (boxes[i], grip);
                }
            }
        }

        if (target is not { } hit)
        {
            return;
        }

        if (!Session.SelectedClipIds.Contains(hit.Box.ClipId))
        {
            Session.Select([hit.Box.ClipId]);
        }

        TitleGesture gesture = hit.Grip switch
        {
            TitleGrip.Body => TitleGesture.Move,
            TitleGrip.Rotate => TitleGesture.Rotate,
            _ => TitleGesture.Scale,
        };
        _titleDrag = TitleTransformDrag.Begin(Session, _properties, hit.Box.ClipId, gesture, hit.Box.Transform);
        if (_titleDrag is null)
        {
            return;
        }

        _titleGrip = hit.Grip;
        _dragStart = frame.ToSequence(point.Position.X, point.Position.Y);
        _dragCenter = TitleHandles.Center(hit.Box, Session.Sequence.Width, Session.Sequence.Height);
        VideoPanel.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnVideoPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_titleDrag is not { } drag || Image() is not { } frame)
        {
            return;
        }

        Point position = e.GetCurrentPoint(VideoPanel).Position;
        Vector2 now = frame.ToSequence(position.X, position.Y);
        if (_titleGrip == TitleGrip.Body)
        {
            drag.Move(drag.StartX + now.X - _dragStart.X, drag.StartY + now.Y - _dragStart.Y);
        }
        else if (_titleGrip == TitleGrip.Rotate)
        {
            bool snap = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
            drag.Rotate(TitleHandles.Rotation(drag.StartRotation, _dragCenter, _dragStart, now, snap));
        }
        else
        {
            drag.Scale(TitleHandles.Scale(drag.StartScale, _dragCenter, _dragStart, now));
        }

        e.Handled = true;
    }

    private void OnVideoPointerReleased(object sender, PointerRoutedEventArgs e) => EndTitleDrag();

    private void OnVideoPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndTitleDrag();

    private void EndTitleDrag()
    {
        TitleTransformDrag? drag = _titleDrag;
        _titleDrag = null;
        drag?.Complete();
        VideoPanel.ReleasePointerCaptures();
    }
}
