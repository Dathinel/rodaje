using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.Playback.Composition;
using Rodaje.Core.Model;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Asas de la transformación de un clip de video en el visor: el contorno como lo dibujó el render, esquinas para escalar,
/// el asa de girar, la cruz del ancla (moverla no mueve la imagen) y la trayectoria de la posición con sus keyframes y
/// manejadores Bézier. Al mover, el centro y los bordes se pegan al centro, los tercios y los bordes del cuadro (Alt lo
/// apaga) y las guías se ven mientras dura. Cada arrastre es un paso del historial; Esc lo revierte.
/// </summary>
internal sealed partial class PlayerView
{
    private const double AnchorRadius = 7;

    private readonly Polygon _layerOutline = new() { StrokeThickness = 1 };
    private readonly Rectangle[] _layerGrips = [Grip(), Grip(), Grip(), Grip()];
    private readonly Line _layerStem = new() { StrokeThickness = 1 };
    private readonly Ellipse _layerRotate = new() { Width = 10, Height = 10, StrokeThickness = 1 };
    private readonly Path _anchor = new() { StrokeThickness = 1.5 };
    private readonly Polyline _path = new() { StrokeThickness = 1.5, StrokeDashArray = [2, 2] };
    private readonly Path _pathKeys = new() { StrokeThickness = 1 };
    private readonly Path _pathHandleLines = new() { StrokeThickness = 1 };
    private readonly Path _pathHandles = new() { StrokeThickness = 1.5 };
    private readonly Line _snapVertical = new() { StrokeThickness = 1, StrokeDashArray = [4, 3], Visibility = Visibility.Collapsed };
    private readonly Line _snapHorizontal = new() { StrokeThickness = 1, StrokeDashArray = [4, 3], Visibility = Visibility.Collapsed };

    private ClipTransformDrag? _clipDrag;
    private ClipGesture _clipGesture;
    private Vector2 _clipDragStart;
    private Vector2 _clipDragCenter;
    private Vector2 _clipHalfSize;
    private PathKey? _pathKey;
    private bool _pathOutgoing;
    private bool _transformCanvasBuilt;

    private static Rectangle Grip() => new() { Width = 8, Height = 8, StrokeThickness = 1 };

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private void BuildTransformCanvas()
    {
        if (_transformCanvasBuilt)
        {
            return;
        }

        _transformCanvasBuilt = true;
        Brush accent = Resource("AccentBrush");
        Brush text = Resource("TextBrush");
        Brush keyframe = Resource("KeyframeBrush");
        _layerOutline.Stroke = accent;
        foreach (Rectangle grip in _layerGrips)
        {
            grip.Fill = text;
            grip.Stroke = accent;
        }

        _layerStem.Stroke = accent;
        _layerRotate.Fill = text;
        _layerRotate.Stroke = accent;
        _anchor.Stroke = accent;
        _anchor.Fill = Resource("ViewerSurroundBrush");
        _path.Stroke = keyframe;
        _pathKeys.Fill = keyframe;
        _pathKeys.Stroke = Resource("BackgroundBrush");
        _pathHandleLines.Stroke = text;
        _pathHandleLines.Opacity = 0.7;
        _pathHandles.Fill = Resource("BackgroundBrush");
        _pathHandles.Stroke = keyframe;
        _snapVertical.Stroke = _snapHorizontal.Stroke = accent;
        UIElement[] shapes = [_snapVertical, _snapHorizontal, _path, _pathHandleLines, _layerOutline, .. _layerGrips, _layerStem, _layerRotate,
            _pathKeys, _pathHandles, _anchor];
        foreach (UIElement shape in shapes)
        {
            TransformCanvas.Children.Add(shape);
        }

        Session!.TransformEditingChanged += (_, _) => UpdateTransformHandles();
        Session.ProjectEdited += (_, e) =>
        {
            if (e.Kind == EditKind.GestureCancelled && _clipDrag is not null)
            {
                _clipDrag = null;
                VideoPanel.ReleasePointerCaptures();
                HideSnapLines();
            }

            UpdateTransformHandles();
        };
    }

    /// <summary>La capa de video seleccionada tal como se dibujó, si no hay un título seleccionado ni recorte o máscara en edición.</summary>
    private TitleBox? SelectedVideoBox()
    {
        if (Session is null || _engine is null || !Session.TransformEditing || TitleCanvas.Visibility == Visibility.Visible || CropCanvas.Visibility == Visibility.Visible
            || MaskCanvas.Visibility == Visibility.Visible || SafeZoneEditCanvas.Visibility == Visibility.Visible)
        {
            return null;
        }

        IReadOnlyList<TitleBox> boxes = _engine.VideoBoxes;
        for (int i = boxes.Count - 1; i >= 0; i--)
        {
            if (Session.SelectedClipIds.Contains(boxes[i].ClipId))
            {
                return boxes[i];
            }
        }

        return null;
    }

    private EffectInstance? TransformOf(string clipId) =>
        Session?.Sequence.FindClip(clipId) is { } found ? ClipPropertyEditor.FindEffect(found.Clip, BuiltInEffects.Transform) : null;

    private void UpdateTransformHandles()
    {
        if (Session is null)
        {
            return;
        }

        BuildTransformCanvas();
        Vector2[]? corners = SelectedVideoBox() is { } box && Image() is { } frame
            ? TitleHandles.Corners(box, Session!.Sequence.Width, Session.Sequence.Height, frame)
            : null;
        TransformCanvas.Visibility = corners is null ? Visibility.Collapsed : Visibility.Visible;
        if (corners is null || SelectedVideoBox() is not { } selected || Image() is not { } image)
        {
            return;
        }

        var points = new PointCollection();
        foreach (Vector2 corner in corners)
        {
            points.Add(new Point(corner.X, corner.Y));
        }

        _layerOutline.Points = points;
        for (int i = 0; i < 4; i++)
        {
            Canvas.SetLeft(_layerGrips[i], corners[i].X - 4);
            Canvas.SetTop(_layerGrips[i], corners[i].Y - 4);
        }

        Vector2 rotate = TitleHandles.RotateGrip(corners);
        Vector2 top = (corners[0] + corners[1]) / 2;
        Canvas.SetLeft(_layerRotate, rotate.X - 5);
        Canvas.SetTop(_layerRotate, rotate.Y - 5);
        (_layerStem.X1, _layerStem.Y1, _layerStem.X2, _layerStem.Y2) = (top.X, top.Y, rotate.X, rotate.Y);

        Vector2 anchor = image.ToPanel(AnchorPoint(selected));
        var cross = new GeometryGroup();
        cross.Children.Add(new EllipseGeometry { Center = new Point(anchor.X, anchor.Y), RadiusX = AnchorRadius - 2, RadiusY = AnchorRadius - 2 });
        cross.Children.Add(new LineGeometry { StartPoint = new Point(anchor.X - AnchorRadius - 3, anchor.Y), EndPoint = new Point(anchor.X + AnchorRadius + 3, anchor.Y) });
        cross.Children.Add(new LineGeometry { StartPoint = new Point(anchor.X, anchor.Y - AnchorRadius - 3), EndPoint = new Point(anchor.X, anchor.Y + AnchorRadius + 3) });
        _anchor.Data = cross;
        DrawPath(selected.ClipId, image);
    }

    /// <summary>Dónde está el ancla en píxeles de la secuencia: el centro de la imagen más el ancla, transformado.</summary>
    private Vector2 AnchorPoint(TitleBox box)
    {
        LayerShape shape = box.Transform.Shape ?? LayerShape.None;
        return new Vector2(
            (float)((Session!.Sequence.Width / 2.0) + box.Transform.X + shape.AnchorX),
            (float)((Session.Sequence.Height / 2.0) + box.Transform.Y + shape.AnchorY));
    }

    private void DrawPath(string clipId, ImageFrame image)
    {
        EffectInstance? transform = TransformOf(clipId);
        var line = new PointCollection();
        var keys = new GeometryGroup();
        var handles = new GeometryGroup();
        var handleLines = new GeometryGroup();
        if (transform is not null && MotionPathView.IsAnimated(transform))
        {
            int width = Session!.Sequence.Width;
            int height = Session.Sequence.Height;
            foreach (Vector2 p in MotionPathView.Sample(transform, width, height))
            {
                Vector2 q = image.ToPanel(p);
                line.Add(new Point(q.X, q.Y));
            }

            foreach (PathKey key in MotionPathView.Handles(transform, width, height))
            {
                Vector2 at = image.ToPanel(key.Point);
                keys.Children.Add(new EllipseGeometry { Center = new Point(at.X, at.Y), RadiusX = 4, RadiusY = 4 });
                foreach (Vector2? handle in new[] { key.In, key.Out })
                {
                    if (handle is { } h)
                    {
                        Vector2 hp = image.ToPanel(h);
                        handleLines.Children.Add(new LineGeometry { StartPoint = new Point(at.X, at.Y), EndPoint = new Point(hp.X, hp.Y) });
                        handles.Children.Add(new EllipseGeometry { Center = new Point(hp.X, hp.Y), RadiusX = 3.5, RadiusY = 3.5 });
                    }
                }
            }
        }

        _path.Points = line;
        _pathKeys.Data = keys;
        _pathHandles.Data = handles;
        _pathHandleLines.Data = handleLines;
    }

    private bool TransformPointerPressed(PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(VideoPanel);
        if (!point.Properties.IsLeftButtonPressed || Session is null || _properties is null || TransformCanvas.Visibility != Visibility.Visible
            || SelectedVideoBox() is not { } box || Image() is not { } image
            || TitleHandles.Corners(box, Session.Sequence.Width, Session.Sequence.Height, image) is not { } corners)
        {
            return false;
        }

        var panel = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _pathKey = null;
        ClipGesture? gesture = null;
        if (Vector2.Distance(image.ToPanel(AnchorPoint(box)), panel) <= AnchorRadius + 2)
        {
            gesture = ClipGesture.Anchor;
        }
        else if (HitPathHandle(box.ClipId, image, panel) is { } handle)
        {
            gesture = ClipGesture.Path;
            (_pathKey, _pathOutgoing) = handle;
        }
        else
        {
            gesture = TitleHandles.Hit(corners, panel.X, panel.Y, withCorners: true) switch
            {
                TitleGrip.None => null,
                TitleGrip.Body => ClipGesture.Move,
                TitleGrip.Rotate => ClipGesture.Rotate,
                _ => ClipGesture.Scale,
            };
        }

        if (gesture is not { } g || ClipTransformDrag.Begin(Session, _properties, box.ClipId, g) is not { } drag)
        {
            return false;
        }

        _clipDrag = drag;
        _clipGesture = g;
        _clipDragStart = image.ToSequence(panel.X, panel.Y);
        _clipDragCenter = AnchorPoint(box);
        float minX = corners.Min(c => c.X);
        float maxX = corners.Max(c => c.X);
        float minY = corners.Min(c => c.Y);
        float maxY = corners.Max(c => c.Y);
        Vector2 boxCenter = image.ToSequence((minX + maxX) / 2, (minY + maxY) / 2);
        _clipHalfSize = new Vector2((float)((maxX - minX) / 2 / image.Scale), (float)((maxY - minY) / 2 / image.Scale));
        _clipBoxOffset = boxCenter - new Vector2((float)((Session.Sequence.Width / 2.0) + drag.Start.X), (float)((Session.Sequence.Height / 2.0) + drag.Start.Y));
        VideoPanel.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private Vector2 _clipBoxOffset;

    private (PathKey Key, bool Outgoing)? HitPathHandle(string clipId, ImageFrame image, Vector2 panel)
    {
        if (TransformOf(clipId) is not { } transform || !MotionPathView.IsAnimated(transform))
        {
            return null;
        }

        foreach (PathKey key in MotionPathView.Handles(transform, Session!.Sequence.Width, Session.Sequence.Height))
        {
            if (key.Out is { } o && Vector2.Distance(image.ToPanel(o), panel) <= TitleHandles.GripRadius)
            {
                return (key, true);
            }

            if (key.In is { } i && Vector2.Distance(image.ToPanel(i), panel) <= TitleHandles.GripRadius)
            {
                return (key, false);
            }
        }

        return null;
    }

    private bool TransformPointerMoved(PointerRoutedEventArgs e)
    {
        if (_clipDrag is not { } drag || Image() is not { } image || Session is null)
        {
            return false;
        }

        Point position = e.GetCurrentPoint(VideoPanel).Position;
        Vector2 now = image.ToSequence(position.X, position.Y);
        Vector2 delta = now - _clipDragStart;
        switch (_clipGesture)
        {
            case ClipGesture.Move:
            {
                Vector2 moved = new((float)drag.Start.X + delta.X, (float)drag.Start.Y + delta.Y);
                HideSnapLines();
                if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu))
                {
                    Vector2 center = moved + new Vector2(Session.Sequence.Width / 2f, Session.Sequence.Height / 2f) + _clipBoxOffset;
                    (Vector2 correction, IReadOnlyList<SnapLine> hits) = SnapGuides.Snap(center, _clipHalfSize, Session.Sequence.Width, Session.Sequence.Height, image.Scale);
                    moved += correction;
                    ShowSnapLines(hits, image);
                }

                drag.Move(moved.X, moved.Y);
                break;
            }

            case ClipGesture.Scale:
                drag.Scale(TitleHandles.Scale(drag.Start.Scale, _clipDragCenter, _clipDragStart, now));
                break;
            case ClipGesture.Rotate:
                drag.Rotate(TitleHandles.Rotation(drag.Start.Rotation, _clipDragCenter, _clipDragStart, now,
                    e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift)));
                break;
            case ClipGesture.Anchor:
            {
                // El ancla se mide en la imagen sin transformar: el desplazamiento en pantalla se lleva atrás por la escala y el giro.
                Vector2 local = Unrotate(delta, drag.Start);
                drag.MoveAnchor(drag.Start.AnchorX + local.X, drag.Start.AnchorY + local.Y);
                break;
            }

            case ClipGesture.Path when _pathKey is { } key:
            {
                Vector2 handle = (_pathOutgoing ? key.Out : key.In) is { } h ? h + delta : now;
                ((double, double)? spatialIn, (double, double)? spatialOut) = MotionPathView.Tangents(key, _pathOutgoing, handle,
                    mirror: !e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu));
                drag.SetPath(key.Time, spatialIn, spatialOut);
                break;
            }
        }

        e.Handled = true;
        return true;
    }

    private static Vector2 Unrotate(Vector2 delta, TransformValues start) =>
        start.ToLocal(delta.X, delta.Y) is { } local ? new Vector2((float)local.X, (float)local.Y) : Vector2.Zero;

    private void ShowSnapLines(IReadOnlyList<SnapLine> hits, ImageFrame image)
    {
        int width = Session!.Sequence.Width;
        int height = Session.Sequence.Height;
        foreach (SnapLine hit in hits)
        {
            Line line = hit.Vertical ? _snapVertical : _snapHorizontal;
            Vector2 a = image.ToPanel(hit.Vertical ? new Vector2((float)hit.At, 0) : new Vector2(0, (float)hit.At));
            Vector2 b = image.ToPanel(hit.Vertical ? new Vector2((float)hit.At, height) : new Vector2(width, (float)hit.At));
            (line.X1, line.Y1, line.X2, line.Y2) = (a.X, a.Y, b.X, b.Y);
            line.Visibility = Visibility.Visible;
        }
    }

    private void HideSnapLines()
    {
        _snapVertical.Visibility = Visibility.Collapsed;
        _snapHorizontal.Visibility = Visibility.Collapsed;
    }

    private void EndTransformDrag()
    {
        ClipTransformDrag? drag = _clipDrag;
        _clipDrag = null;
        _pathKey = null;
        HideSnapLines();
        drag?.Complete();
    }
}
