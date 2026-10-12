using System.Globalization;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Render;
using Windows.Foundation;
using Windows.System;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Máscaras en el visor con la pestaña «Máscaras» a la vista. Se ven todas las del clip en su color; la elegida lleva su
/// caja (mover, tamaño por esquinas y bordes, girar) y, si es Bézier o pluma, sus vértices y manejadores. Herramientas:
/// elegir, rectángulo, elipse, estrella, pluma Bézier, pluma de trazo y mano alzada. Cada arrastre es un paso del
/// historial; con keyframes, se escribe el del cursor. Atajos con el puntero sobre el visor: V, P, Mayús+P, R, E, F, Supr,
/// Intro, Esc, flechas (Mayús: ×10) y Ctrl+A.
/// </summary>
internal sealed partial class PlayerView
{
    private enum MaskDrag
    {
        None,
        Move,
        Resize,
        Rotate,
        Vertices,
        Handle,
        Marquee,
        Shape,
        Freehand,
        PenHandle,
    }

    private MaskDrag _maskDrag;
    private MaskBox _maskStart;
    private MaskHit _maskHit;
    private Vector2 _maskFromImage;
    private Vector2 _maskFromPanel;
    private Vector2 _maskNowPanel;
    private Time _maskAt;
    private string? _maskMerge;
    private int _maskGesture;
    private readonly List<Vector2> _freehand = [];
    private readonly List<MaskVertex> _pen = [];
    private string? _penClipId;
    private bool _pointerOverVideo;
    private Vector2? _hoverPanel;
    private MaskPreview? _maskPreviewSent;

    private void AttachMaskHandles()
    {
        Session!.MaskEditingChanged += (_, _) => UpdateMaskHandles();
        Session.MaskToolChanged += (_, _) =>
        {
            if (Session.MaskTool is not (MaskTool.Pen or MaskTool.StrokePen))
            {
                CancelPen();
            }

            UpdateMaskHandles();
        };
        Session.SelectionChanged += (_, _) => UpdateMaskHandles();
        Session.ProjectEdited += (_, _) => UpdateMaskHandles();
        Session.TransportChanged += (_, _) => UpdateMaskHandles();
        VideoPanel.SizeChanged += (_, _) => UpdateMaskHandles();
        VideoPanel.PointerEntered += (_, _) => _pointerOverVideo = true;
        VideoPanel.PointerExited += (_, _) =>
        {
            _pointerOverVideo = false;
            _hoverPanel = null;
            if (_maskDrag == MaskDrag.None && (_pen.Count > 0))
            {
                UpdateMaskHandles();
            }
        };
    }

    /// <summary>El clip de video elegido con el cursor encima, que es donde se dibujan las máscaras nuevas.</summary>
    private Clip? MaskClip()
    {
        if (Session is not { IsMaskEditing: true } session)
        {
            return null;
        }

        foreach (string id in session.SelectedClipIds)
        {
            if (session.Sequence.FindClip(id) is { Track.Kind: TrackKind.Video } found && found.Clip.Generator is null
                && session.Position >= found.Clip.Start && session.Position < found.Clip.End)
            {
                return found.Clip;
            }
        }

        return null;
    }

    private MaskBox? EditedMask() =>
        Session is { IsMaskEditing: true, EditedMaskId: { } maskId } session && MaskClip() is { } clip
        && clip.Effects.Find(e => e.Id == maskId && e.Type == BuiltInEffects.Mask) is { } effect
            ? MaskHandles.Find(session.Project, session.Sequence, clip, effect, session.Position)
            : null;

    /// <summary>Las máscaras activas del clip (menos la de imagen), para dibujarlas y elegirlas con un clic.</summary>
    private List<MaskBox> ClipMasks(Clip clip)
    {
        var boxes = new List<MaskBox>();
        foreach (EffectInstance effect in clip.Effects)
        {
            if (effect.Enabled && effect.Type == BuiltInEffects.Mask
                && MaskHandles.Find(Session!.Project, Session.Sequence, clip, effect, Session.Position) is { Mask.Shape: not MaskShape.Image } box)
            {
                boxes.Add(box);
            }
        }

        return boxes;
    }

    /// <summary>La imagen de la capa del clip en el cursor (para dibujar una máscara nueva).</summary>
    private LayerPlacement? LayerOf(Clip clip) =>
        Session!.Project.FindMedia(clip.MediaId) is { Width: > 0, Height: > 0 } media
            ? LayerLayout.Place(Session.Sequence.Width, Session.Sequence.Height, media.Width, media.Height,
                MaskHandles.TransformAt(clip, clip.ToEffectTime(Session.Position)))
            : null;

    private void UpdateMaskHandles()
    {
        UpdateMaskView();
        MaskCanvas.Children.Clear();
        if (Image() is not { } frame || Session is not { IsMaskEditing: true } session || MaskClip() is not { } clip)
        {
            MaskCanvas.Visibility = Visibility.Collapsed;
            MaskHint.Visibility = Visibility.Collapsed;
            return;
        }

        MaskCanvas.Visibility = Visibility.Visible;
        MaskBox? edited = _maskDrag is MaskDrag.Move or MaskDrag.Resize or MaskDrag.Rotate ? EditedMask() ?? _maskStart : EditedMask();
        foreach (MaskBox box in ClipMasks(clip))
        {
            if (box.EffectId != edited?.EffectId)
            {
                AddOutline(box, frame, selected: false);
            }
        }

        if (edited is { } shown && shown.Mask.Shape != MaskShape.Image)
        {
            AddOutline(shown, frame, selected: true);
            if (session.MaskTool == MaskTool.Select)
            {
                AddBoxGrips(shown, frame);
                if (shown.EditsVertices)
                {
                    AddVertices(shown, frame, session.SelectedMaskVertices);
                }
            }
        }

        AddToolPreview(frame, clip);
        ShowMaskHint(session, edited);
    }

    private void ShowMaskHint(EditorSession session, MaskBox? edited)
    {
        string hint = session.MaskTool switch
        {
            MaskTool.Pen => _pen.Count == 0
                ? "Pluma: clic pone una esquina y arrastrar saca una curva."
                : "Clic: otro punto · arrastrar: curva · clic en el primero o Intro: cerrar · Retroceso: quitar el último · Esc: cancelar",
            MaskTool.StrokePen => _pen.Count == 0
                ? "Pluma de trazo: clic pone un punto y arrastrar curva el trazo."
                : "Clic: otro punto · arrastrar: curva · Intro: terminar · Retroceso: quitar el último · Esc: cancelar",
            MaskTool.Rectangle or MaskTool.Ellipse or MaskTool.Star => "Arrastra para dibujar; con Mayús, proporción 1:1.",
            MaskTool.Freehand => "Dibuja el contorno con el ratón; al soltar se cierra y se suaviza.",
            _ when edited is { EditsVertices: true } => session.SelectedMaskVertices.Count > 0
                ? "Arrastra los puntos o sus manejadores (Alt: quebrar) · Alt+clic: esquina o suave · Ctrl+clic en el contorno: punto nuevo · Supr: quitar"
                : "Clic en un punto para elegirlo (Mayús: varios) o arrastra un recuadro · Ctrl+clic en el contorno: punto nuevo",
            _ when edited is not null => "Arrastra para mover · esquinas y bordes: tamaño (Mayús: proporción) · círculo: girar (Mayús: de 15°)",
            _ => "Elige una máscara de la lista o dibuja una con las herramientas.",
        };
        if (edited is { Animated: true } && session.MaskTool == MaskTool.Select)
        {
            hint += " · forma animada: se edita el keyframe del cursor";
        }

        MaskHintText.Text = hint;
        MaskHint.Visibility = Visibility.Visible;
    }

    /// <summary>La vista de la máscara (gris o superpuesta) del grupo de la máscara elegida.</summary>
    private void UpdateMaskView()
    {
        MaskPreview? preview = null;
        if (Session is { IsMaskEditing: true, EditedMaskId: { } maskId, MaskView: not MaskViewMode.None } session && MaskClip() is { } clip)
        {
            int ordinal = 0;
            foreach (EffectInstance effect in clip.Effects)
            {
                if (effect.Type != BuiltInEffects.Mask || !effect.Enabled)
                {
                    continue;
                }

                if (effect.Id == maskId)
                {
                    preview = new MaskPreview(clip.Id, ordinal, session.MaskView);
                    break;
                }

                ordinal++;
            }
        }

        if (preview != _maskPreviewSent)
        {
            _maskPreviewSent = preview;
            _engine?.SetMaskView(preview);
        }
    }

    private static Windows.UI.Color ParseColor(string text, byte alpha = 255) =>
        text is ['#', ..] && text.Length == 7 && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)
            ? ColorHelper.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : ColorHelper.FromArgb(alpha, 0x4F, 0xC3, 0xF7);

    private void AddOutline(in MaskBox box, ImageFrame frame, bool selected)
    {
        (MaskVertex[] vertices, bool closed) = MaskHandles.Shape(box);
        if (vertices.Length < 2)
        {
            return;
        }

        MaskBox b = box;
        Path path = BezierPath(vertices, closed, v => MaskHandles.ToPanel(b, frame, v));
        path.Stroke = new SolidColorBrush(ParseColor(box.Color, selected ? (byte)255 : (byte)170));
        path.StrokeThickness = selected ? 1.6 : 1;
        if (!selected)
        {
            path.StrokeDashArray = [4, 3];
        }

        // Un halo oscuro debajo, para que la línea se vea sobre cualquier imagen.
        Path halo = BezierPath(vertices, closed, v => MaskHandles.ToPanel(b, frame, v));
        halo.Stroke = new SolidColorBrush(ColorHelper.FromArgb(selected ? (byte)140 : (byte)90, 0, 0, 0));
        halo.StrokeThickness = path.StrokeThickness + 2;
        MaskCanvas.Children.Add(halo);
        MaskCanvas.Children.Add(path);
    }

    private static Path BezierPath(IReadOnlyList<MaskVertex> vertices, bool closed, Func<Vector2, Vector2> map)
    {
        var figure = new PathFigure { StartPoint = ToPoint(map(vertices[0].Point)), IsClosed = closed, IsFilled = false };
        int n = vertices.Count;
        for (int s = 0; s < (closed ? n : n - 1); s++)
        {
            MaskVertex a = vertices[s], c = vertices[(s + 1) % n];
            figure.Segments.Add(new BezierSegment
            {
                Point1 = ToPoint(map(a.Point + a.Out)),
                Point2 = ToPoint(map(c.Point + c.In)),
                Point3 = ToPoint(map(c.Point)),
            });
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return new Path { Data = geometry };
    }

    private static Point ToPoint(Vector2 v) => new(v.X, v.Y);

    private void AddBoxGrips(in MaskBox box, ImageFrame frame)
    {
        (Vector2[] grips, Vector2 rotate, Vector2 stem) = MaskHandles.BoxGrips(box, frame);
        var outline = new Polygon
        {
            Points = [ToPoint(grips[0]), ToPoint(grips[1]), ToPoint(grips[2]), ToPoint(grips[3])],
            Stroke = (Brush)Application.Current.Resources["TextMutedBrush"],
            StrokeThickness = 1,
            StrokeDashArray = [2, 3],
        };
        MaskCanvas.Children.Add(outline);
        MaskCanvas.Children.Add(new Line
        {
            X1 = stem.X, Y1 = stem.Y, X2 = rotate.X, Y2 = rotate.Y,
            Stroke = (Brush)Application.Current.Resources["AccentBrush"], StrokeThickness = 1,
        });
        foreach (Vector2 g in grips)
        {
            AddDot(new Rectangle(), g, 8, (Brush)Application.Current.Resources["TextBrush"], (Brush)Application.Current.Resources["AccentBrush"]);
        }

        AddDot(new Ellipse(), rotate, 10, (Brush)Application.Current.Resources["TextBrush"], (Brush)Application.Current.Resources["AccentBrush"]);
    }

    private void AddVertices(in MaskBox box, ImageFrame frame, IReadOnlyList<int> selected)
    {
        Brush color = new SolidColorBrush(ParseColor(box.Color));
        Brush dark = new SolidColorBrush(ColorHelper.FromArgb(255, 0x1D, 0x1D, 0x1D));
        Brush white = (Brush)Application.Current.Resources["TextBrush"];
        for (int i = 0; i < box.Vertices.Length; i++)
        {
            MaskVertex v = box.Vertices[i];
            Vector2 point = MaskHandles.ToPanel(box, frame, v.Point);
            bool isSelected = selected.Contains(i);
            if (isSelected)
            {
                foreach (Vector2 handle in new[] { v.In, v.Out })
                {
                    if (handle == Vector2.Zero)
                    {
                        continue;
                    }

                    Vector2 h = MaskHandles.ToPanel(box, frame, v.Point + handle);
                    MaskCanvas.Children.Add(new Line { X1 = point.X, Y1 = point.Y, X2 = h.X, Y2 = h.Y, Stroke = color, StrokeThickness = 1 });
                    AddDot(new Ellipse(), h, 7, color, dark);
                }
            }

            // Esquina: cuadrado; suave: círculo. Los elegidos, llenos de blanco.
            Shape dot = v.IsCorner ? new Rectangle() : new Ellipse();
            AddDot(dot, point, isSelected ? 9 : 8, isSelected ? white : dark, color);
        }
    }

    private void AddDot(Shape shape, Vector2 at, double size, Brush fill, Brush stroke)
    {
        shape.Width = size;
        shape.Height = size;
        shape.Fill = fill;
        shape.Stroke = stroke;
        shape.StrokeThickness = 1.2;
        Canvas.SetLeft(shape, at.X - (size / 2));
        Canvas.SetTop(shape, at.Y - (size / 2));
        MaskCanvas.Children.Add(shape);
    }

    /// <summary>Lo que se está dibujando: la pluma con su línea al puntero, la caja de una forma, el trazo a mano o el recuadro.</summary>
    private void AddToolPreview(ImageFrame frame, Clip clip)
    {
        Brush accent = (Brush)Application.Current.Resources["AccentBrush"];
        if (_pen.Count > 0 && _penClipId == clip.Id && LayerOf(clip) is { } layer)
        {
            var size = new Vector2(layer.LocalWidth, layer.LocalHeight);
            Vector2 Map(Vector2 image) => frame.ToPanel(layer.Map(image.X * size.X, image.Y * size.Y));
            if (_pen.Count >= 2)
            {
                Path path = BezierPath(_pen, false, Map);
                path.Stroke = accent;
                path.StrokeThickness = 1.6;
                MaskCanvas.Children.Add(path);
            }

            Vector2 last = Map(_pen[^1].Point);
            if (_hoverPanel is { } hover && _maskDrag != MaskDrag.PenHandle)
            {
                MaskCanvas.Children.Add(new Line { X1 = last.X, Y1 = last.Y, X2 = hover.X, Y2 = hover.Y, Stroke = accent, StrokeThickness = 1, StrokeDashArray = [3, 3] });
            }

            for (int i = 0; i < _pen.Count; i++)
            {
                Vector2 p = Map(_pen[i].Point);
                bool closing = i == 0 && _pen.Count >= 3 && Session!.MaskTool == MaskTool.Pen && _hoverPanel is { } h && Vector2.Distance(h, p) <= MaskHandles.GripRadius;
                if (i == _pen.Count - 1 && _pen[i].Out != Vector2.Zero)
                {
                    Vector2 o = Map(_pen[i].Point + _pen[i].Out), n = Map(_pen[i].Point + _pen[i].In);
                    MaskCanvas.Children.Add(new Line { X1 = n.X, Y1 = n.Y, X2 = o.X, Y2 = o.Y, Stroke = accent, StrokeThickness = 1 });
                    AddDot(new Ellipse(), o, 6, accent, accent);
                    AddDot(new Ellipse(), n, 6, accent, accent);
                }

                AddDot(new Rectangle(), p, closing ? 12 : 8, (Brush)Application.Current.Resources["TextBrush"], accent);
            }
        }

        switch (_maskDrag)
        {
            case MaskDrag.Marquee:
                var marquee = new Rectangle
                {
                    Width = Math.Abs(_maskNowPanel.X - _maskFromPanel.X),
                    Height = Math.Abs(_maskNowPanel.Y - _maskFromPanel.Y),
                    Stroke = accent,
                    StrokeThickness = 1,
                    StrokeDashArray = [3, 2],
                    Fill = new SolidColorBrush(ColorHelper.FromArgb(32, 0xFF, 0xFF, 0xFF)),
                };
                Canvas.SetLeft(marquee, Math.Min(_maskNowPanel.X, _maskFromPanel.X));
                Canvas.SetTop(marquee, Math.Min(_maskNowPanel.Y, _maskFromPanel.Y));
                MaskCanvas.Children.Add(marquee);
                break;
            case MaskDrag.Shape when ShapeBox() is { } shape:
                Vector2 c = shape.Center, half = shape.Size / 2;
                MaskVertex[] vertices = Session!.MaskTool switch
                {
                    MaskTool.Ellipse => MaskPath.Ellipse(),
                    MaskTool.Star => MaskPath.Star(5),
                    _ => MaskPath.Rectangle(),
                };
                Path preview = BezierPath(vertices, true, v => c + (v * half * 2));
                preview.Stroke = accent;
                preview.StrokeThickness = 1.6;
                preview.StrokeDashArray = [4, 2];
                MaskCanvas.Children.Add(preview);
                break;
            case MaskDrag.Freehand when _freehand.Count >= 2 && LayerOf(clip) is { } freeLayer:
                var line = new Polyline { Stroke = accent, StrokeThickness = 2 };
                foreach (Vector2 p in _freehand)
                {
                    line.Points.Add(ToPoint(frame.ToPanel(freeLayer.Map(p.X * freeLayer.LocalWidth, p.Y * freeLayer.LocalHeight))));
                }

                MaskCanvas.Children.Add(line);
                break;
        }
    }

    /// <summary>La caja que se está dibujando en el panel (Mayús: cuadrada), o nula si es muy chica.</summary>
    private (Vector2 Center, Vector2 Size)? ShapeBox()
    {
        Vector2 a = _maskFromPanel, b = _maskNowPanel;
        if (IsShiftDown())
        {
            float side = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            b = a + new Vector2(Math.Sign(b.X - a.X) * side, Math.Sign(b.Y - a.Y) * side);
        }

        Vector2 size = Vector2.Abs(b - a);
        return size.X < 3 || size.Y < 3 ? null : ((a + b) / 2, size);
    }

    private static bool IsShiftDown() => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static bool IsAltDown() => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static bool IsControlDown() => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>Atiende el clic si la pestaña de máscaras está a la vista y cae en su terreno.</summary>
    private bool MaskPointerPressed(PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(VideoPanel);
        if (!point.Properties.IsLeftButtonPressed || Session is not { IsMaskEditing: true } session || Image() is not { } frame
            || MaskClip() is not { } clip || _properties is null)
        {
            return false;
        }

        var panel = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _maskFromPanel = _maskNowPanel = panel;
        _maskAt = _properties.KeyframeTimeAtPlayhead(clip);
        switch (session.MaskTool)
        {
            case MaskTool.Rectangle or MaskTool.Ellipse or MaskTool.Star:
                StartMaskDrag(e, MaskDrag.Shape);
                return true;
            case MaskTool.Freehand:
                _freehand.Clear();
                if (ImageFraction(clip, panel) is { } start)
                {
                    _freehand.Add(start);
                }

                StartMaskDrag(e, MaskDrag.Freehand);
                return true;
            case MaskTool.Pen or MaskTool.StrokePen:
                PenPressed(clip, frame, panel);
                StartMaskDrag(e, MaskDrag.PenHandle);
                return true;
        }

        MaskBox? edited = EditedMask();
        MaskHit hit = edited is { } box ? MaskHandles.HitTest(box, frame, panel, session.SelectedMaskVertices, showBox: true) : default;
        if (hit.Grip == MaskGrip.None)
        {
            // Otra máscara del clip bajo el puntero: se elige y se puede arrastrar enseguida.
            foreach (MaskBox other in Enumerable.Reverse(ClipMasks(clip)))
            {
                if (other.EffectId != edited?.EffectId && MaskHandles.HitTest(other, frame, panel, [], showBox: false).Grip != MaskGrip.None)
                {
                    session.PickMask(other.EffectId);
                    edited = other;
                    hit = new MaskHit(MaskGrip.Body);
                    break;
                }
            }
        }

        if (edited is not { } shown)
        {
            return false;
        }

        _maskStart = shown;
        _maskHit = hit;
        _maskFromImage = MaskHandles.ToImage(shown.Layer, frame.ToSequence(panel.X, panel.Y));
        switch (hit.Grip)
        {
            case MaskGrip.Vertex:
                if (IsAltDown())
                {
                    SetVertices(shown, [.. shown.Vertices.Select((v, i) => i == hit.Index ? MaskPath.Toggle(shown.Vertices, i, shown.Mask.Shape != MaskShape.Stroke) : v)],
                        "Cambiar esquina o suave", merge: false);
                    session.SelectMaskVertices([hit.Index]);
                    e.Handled = true;
                    return true;
                }

                IReadOnlyList<int> selection = session.SelectedMaskVertices;
                if (IsShiftDown())
                {
                    session.SelectMaskVertices(selection.Contains(hit.Index) ? [.. selection.Where(i => i != hit.Index)] : [.. selection, hit.Index]);
                }
                else if (!selection.Contains(hit.Index))
                {
                    session.SelectMaskVertices([hit.Index]);
                }

                StartMaskDrag(e, MaskDrag.Vertices, "Mover puntos de la máscara");
                return true;
            case MaskGrip.InHandle or MaskGrip.OutHandle:
                StartMaskDrag(e, MaskDrag.Handle, "Mover un manejador");
                return true;
            case MaskGrip.Segment when IsControlDown():
                MaskVertex[] split = MaskPath.Split(shown.Vertices, hit.Index, hit.T, shown.Mask.Shape != MaskShape.Stroke);
                SetVertices(shown, split, "Añadir un punto", merge: false);
                session.SelectMaskVertices([hit.Index + 1]);
                e.Handled = true;
                return true;
            case MaskGrip.Rotate:
                StartMaskDrag(e, MaskDrag.Rotate, "Girar la máscara");
                return true;
            case >= MaskGrip.TopLeft and <= MaskGrip.Left:
                StartMaskDrag(e, MaskDrag.Resize, "Cambiar el tamaño de la máscara");
                return true;
            case MaskGrip.Body or MaskGrip.Segment:
                if (!IsShiftDown())
                {
                    session.SelectMaskVertices([]);
                }

                StartMaskDrag(e, MaskDrag.Move, "Mover la máscara");
                return true;
            default:
                if (shown.EditsVertices)
                {
                    session.SelectMaskVertices([]);
                    StartMaskDrag(e, MaskDrag.Marquee);
                    return true;
                }

                return false;
        }
    }

    private void StartMaskDrag(PointerRoutedEventArgs e, MaskDrag kind, string? gesture = null)
    {
        _maskDrag = kind;
        _maskMerge = $"mascara-{++_maskGesture}";
        if (gesture is not null)
        {
            Session!.BeginGesture(gesture);
        }

        VideoPanel.CapturePointer(e.Pointer);
        e.Handled = true;
        UpdateMaskHandles();
    }

    private bool MaskPointerMoved(PointerRoutedEventArgs e)
    {
        if (Image() is not { } frame || Session is not { IsMaskEditing: true } session)
        {
            return false;
        }

        Point position = e.GetCurrentPoint(VideoPanel).Position;
        var panel = new Vector2((float)position.X, (float)position.Y);
        _hoverPanel = panel;
        if (_maskDrag == MaskDrag.None)
        {
            UpdateMaskCursor(session, frame, panel);
            if (_pen.Count > 0)
            {
                UpdateMaskHandles();
            }

            return false;
        }

        _maskNowPanel = panel;
        Vector2 sequence = frame.ToSequence(position.X, position.Y);
        MaskBox start = _maskStart;
        string clipId = start.ClipId, effectId = start.EffectId;
        Time at = _maskAt;
        switch (_maskDrag)
        {
            case MaskDrag.Move:
            {
                (double x, double y) = MaskHandles.Moved(start, _maskFromImage, MaskHandles.ToImage(start.Layer, sequence));
                Session!.Execute(new SequenceEditCommand("Mover la máscara", session.SequenceId, ctx =>
                {
                    EffectEdits.SetParameter(ctx, clipId, effectId, M.X, Math.Round(x, 5), at);
                    EffectEdits.SetParameter(ctx, clipId, effectId, M.Y, Math.Round(y, 5), at);
                }, mergeKey: _maskMerge));
                break;
            }

            case MaskDrag.Resize:
            {
                (double width, double height) = MaskHandles.Resized(start, _maskHit.Grip, MaskHandles.ToImage(start.Layer, sequence), IsShiftDown());
                session.Execute(new SequenceEditCommand("Cambiar el tamaño de la máscara", session.SequenceId, ctx =>
                {
                    EffectEdits.SetParameter(ctx, clipId, effectId, M.Width, Math.Round(width, 5), at);
                    EffectEdits.SetParameter(ctx, clipId, effectId, M.Height, Math.Round(height, 5), at);
                }, mergeKey: _maskMerge));
                break;
            }

            case MaskDrag.Rotate:
            {
                double rotation = MaskHandles.Rotated(start, _maskFromImage, MaskHandles.ToImage(start.Layer, sequence), IsShiftDown());
                session.Execute(new SequenceEditCommand("Girar la máscara", session.SequenceId,
                    ctx => EffectEdits.SetParameter(ctx, clipId, effectId, M.Rotation, Math.Round(rotation, 3), at), mergeKey: _maskMerge));
                break;
            }

            case MaskDrag.Vertices:
            {
                Vector2 delta = MaskHandles.FromPanel(start, frame, panel) - MaskHandles.FromPanel(start, frame, _maskFromPanel);
                SetVertices(start, MaskHandles.MoveVertices(start.Vertices, session.SelectedMaskVertices, delta), "Mover puntos de la máscara", merge: true);
                break;
            }

            case MaskDrag.Handle:
            {
                Vector2 target = MaskHandles.FromPanel(start, frame, panel);
                SetVertices(start, MaskHandles.MoveHandle(start.Vertices, _maskHit.Index, _maskHit.Grip == MaskGrip.OutHandle, target, IsAltDown()),
                    "Mover un manejador", merge: true);
                break;
            }

            case MaskDrag.Freehand:
                if (Vector2.Distance(panel, _maskFromPanel) >= 2 && MaskClip() is { } clip && ImageFraction(clip, panel) is { } p)
                {
                    _freehand.Add(p);
                    _maskFromPanel = panel;
                    UpdateMaskHandles();
                }

                break;
            case MaskDrag.PenHandle:
                if (_pen.Count > 0 && MaskClip() is { } penClip && ImageFraction(penClip, panel) is { } handle
                    && Vector2.Distance(panel, _maskFromPanel) > 3)
                {
                    MaskVertex last = _pen[^1];
                    _pen[^1] = last with { Out = handle - last.Point, In = last.Point - handle };
                }

                UpdateMaskHandles();
                break;
            default:
                UpdateMaskHandles();
                break;
        }

        e.Handled = true;
        return true;
    }

    private void UpdateMaskCursor(EditorSession session, ImageFrame frame, Vector2 panel)
    {
        InputSystemCursorShape shape = InputSystemCursorShape.Arrow;
        if (session.MaskTool != MaskTool.Select)
        {
            shape = InputSystemCursorShape.Cross;
        }
        else if (EditedMask() is { } box)
        {
            shape = MaskHandles.HitTest(box, frame, panel, session.SelectedMaskVertices, showBox: true).Grip switch
            {
                MaskGrip.Body => InputSystemCursorShape.SizeAll,
                MaskGrip.Vertex or MaskGrip.InHandle or MaskGrip.OutHandle => InputSystemCursorShape.Hand,
                MaskGrip.Rotate => InputSystemCursorShape.Hand,
                MaskGrip.TopLeft or MaskGrip.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
                MaskGrip.TopRight or MaskGrip.BottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
                MaskGrip.Top or MaskGrip.Bottom => InputSystemCursorShape.SizeNorthSouth,
                MaskGrip.Left or MaskGrip.Right => InputSystemCursorShape.SizeWestEast,
                MaskGrip.Segment => IsControlDown() ? InputSystemCursorShape.Cross : InputSystemCursorShape.SizeAll,
                _ => InputSystemCursorShape.Arrow,
            };
        }

        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    private bool MaskPointerReleased()
    {
        if (_maskDrag == MaskDrag.None)
        {
            return false;
        }

        MaskDrag drag = _maskDrag;
        _maskDrag = MaskDrag.None;
        VideoPanel.ReleasePointerCaptures();
        if (Session is not { } session)
        {
            return true;
        }

        switch (drag)
        {
            case MaskDrag.Move or MaskDrag.Resize or MaskDrag.Rotate or MaskDrag.Vertices or MaskDrag.Handle:
                session.EndGesture();
                break;
            case MaskDrag.Marquee when Image() is { } frame && EditedMask() is { } box:
                session.SelectMaskVertices(MaskHandles.InRectangle(box, frame, _maskFromPanel, _maskNowPanel));
                break;
            case MaskDrag.Shape:
                FinishShape();
                break;
            case MaskDrag.Freehand:
                FinishFreehand();
                break;
        }

        UpdateMaskHandles();
        return true;
    }

    /// <summary>Doble clic con la pestaña de máscaras: en la pluma termina; sobre un punto lo vuelve esquina o suave.</summary>
    private bool MaskDoubleTapped(Point position)
    {
        if (Session is not { IsMaskEditing: true } session || Image() is not { } frame)
        {
            return false;
        }

        if (_pen.Count > 0)
        {
            FinishPen();
            return true;
        }

        if (session.MaskTool == MaskTool.Select && EditedMask() is { EditsVertices: true } box)
        {
            MaskHit hit = MaskHandles.HitTest(box, frame, new Vector2((float)position.X, (float)position.Y), session.SelectedMaskVertices, false);
            if (hit.Grip == MaskGrip.Vertex)
            {
                SetVertices(box, [.. box.Vertices.Select((v, i) => i == hit.Index ? MaskPath.Toggle(box.Vertices, i, box.Mask.Shape != MaskShape.Stroke) : v)],
                    "Cambiar esquina o suave", merge: false);
                return true;
            }

            if (hit.Grip == MaskGrip.Segment)
            {
                SetVertices(box, MaskPath.Split(box.Vertices, hit.Index, hit.T, box.Mask.Shape != MaskShape.Stroke), "Añadir un punto", merge: false);
                session.SelectMaskVertices([hit.Index + 1]);
                return true;
            }
        }

        return EditedMask() is not null;
    }

    private void SetVertices(in MaskBox box, IReadOnlyList<MaskVertex> vertices, string description, bool merge)
    {
        if (Session is not { } session)
        {
            return;
        }

        session.Execute(Edits.SetMaskVertices(session.SequenceId, box.ClipId, box.EffectId, vertices, _maskAt, description, merge ? _maskMerge : null));
    }

    /// <summary>Un punto del panel en la imagen del clip (0–1).</summary>
    private Vector2? ImageFraction(Clip clip, Vector2 panel) =>
        Image() is { } frame && LayerOf(clip) is { } layer
            ? MaskHandles.ToImage(layer, frame.ToSequence(panel.X, panel.Y)) / new Vector2(layer.LocalWidth, layer.LocalHeight)
            : null;

    private void FinishShape()
    {
        if (ShapeBox() is not { } shape || Session is not { } session || MaskClip() is not { } clip || ImageFraction(clip, shape.Center) is not { } center
            || ImageFraction(clip, shape.Center - (shape.Size / 2)) is not { } corner || ImageFraction(clip, shape.Center + (shape.Size / 2)) is not { } far)
        {
            return;
        }

        // La caja en la imagen: el visor puede mostrar la capa girada; se mide a lo largo de los ejes de la imagen.
        double width = Math.Abs(far.X - corner.X), height = Math.Abs(far.Y - corner.Y);
        SequenceEditCommand command = session.MaskTool switch
        {
            MaskTool.Ellipse => Edits.AddMask(session.SequenceId, clip.Id, MaskShape.Ellipse, center.X, center.Y, width, height),
            MaskTool.Star => Edits.AddVertexMask(session.SequenceId, clip.Id, MaskShape.Bezier, center.X, center.Y, width, height, MaskPath.Star(5)),
            _ => Edits.AddMask(session.SequenceId, clip.Id, MaskShape.Rectangle, center.X, center.Y, width, height),
        };
        if (session.Execute(command))
        {
            session.PickMask(command.CreatedIds[0]);
            session.SetMaskTool(MaskTool.Select);
        }
    }

    private void FinishFreehand()
    {
        if (Session is not { } session || MaskClip() is not { } clip || LayerOf(clip) is not { } layer || _freehand.Count < 4)
        {
            _freehand.Clear();
            return;
        }

        MaskVertex[] smooth = MaskGeometry.SmoothStroke(_freehand, layer.LocalWidth, layer.LocalHeight, closed: true);
        _freehand.Clear();
        (double x, double y, double width, double height, MaskVertex[] vertices) = MaskPath.Fit(smooth);
        if (vertices.Length >= 3 && width * layer.LocalWidth >= 4 && height * layer.LocalHeight >= 4)
        {
            SequenceEditCommand command = Edits.AddVertexMask(session.SequenceId, clip.Id, MaskShape.Bezier, x, y, width, height, vertices, "Trazo libre");
            if (session.Execute(command))
            {
                session.PickMask(command.CreatedIds[0]);
            }
        }
    }

    private void PenPressed(Clip clip, ImageFrame frame, Vector2 panel)
    {
        if (_penClipId != clip.Id)
        {
            _pen.Clear();
            _penClipId = clip.Id;
        }

        if (_pen.Count >= 3 && Session!.MaskTool == MaskTool.Pen && LayerOf(clip) is { } layer)
        {
            Vector2 first = frame.ToPanel(layer.Map(_pen[0].Point.X * layer.LocalWidth, _pen[0].Point.Y * layer.LocalHeight));
            if (Vector2.Distance(first, panel) <= MaskHandles.GripRadius)
            {
                FinishPen();
                return;
            }
        }

        if (ImageFraction(clip, panel) is { } point && _pen.Count < MaskPath.MaxVertices)
        {
            _pen.Add(new MaskVertex(point.X, point.Y));
        }
    }

    private void FinishPen()
    {
        if (Session is not { } session || _penClipId is not { } clipId)
        {
            CancelPen();
            return;
        }

        MaskShape shape = session.MaskTool == MaskTool.StrokePen ? MaskShape.Stroke : MaskShape.Bezier;
        MaskVertex[] points = [.. _pen];
        CancelPen();
        if (points.Length < MaskGeometry.MinimumPoints(shape))
        {
            session.Notify(NotificationSeverity.Informational, "Pluma", shape == MaskShape.Stroke
                ? "Un trazo necesita al menos dos puntos."
                : "Una Bézier necesita al menos tres puntos.");
            return;
        }

        (double x, double y, double width, double height, MaskVertex[] vertices) = MaskPath.Fit(points);
        SequenceEditCommand command = Edits.AddVertexMask(session.SequenceId, clipId, shape, x, y, width, height, vertices,
            shape == MaskShape.Stroke ? "Pluma de trazo" : "Pluma Bézier");
        if (session.Execute(command))
        {
            session.PickMask(command.CreatedIds[0]);
            session.SetMaskTool(MaskTool.Select);
        }
    }

    private void CancelPen()
    {
        _pen.Clear();
        _penClipId = null;
        UpdateMaskHandles();
    }

    /// <summary>
    /// Teclas de las máscaras, con la pestaña a la vista y el puntero sobre el visor (las demás pasan a los atajos de
    /// siempre). Devuelve <c>true</c> si la tecla era suya.
    /// </summary>
    internal bool HandleMaskKey(KeyChord chord)
    {
        if (Session is not { IsMaskEditing: true } session || (!_pointerOverVideo && _pen.Count == 0)
            || (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox or NumberBox or AutoSuggestBox))
        {
            return false;
        }

        var key = (VirtualKey)chord.Key;
        bool shift = (chord.Modifiers & KeyModifiers.Shift) != 0;
        bool control = (chord.Modifiers & KeyModifiers.Control) != 0;
        bool plain = chord.Modifiers is KeyModifiers.None or KeyModifiers.Shift;
        if (_pen.Count > 0)
        {
            switch (key)
            {
                case VirtualKey.Escape:
                    CancelPen();
                    return true;
                case VirtualKey.Enter:
                    FinishPen();
                    return true;
                case VirtualKey.Back:
                    _pen.RemoveAt(_pen.Count - 1);
                    UpdateMaskHandles();
                    return true;
            }
        }

        if (plain && !control)
        {
            MaskTool? tool = key switch
            {
                VirtualKey.V when !shift => MaskTool.Select,
                VirtualKey.P => shift ? MaskTool.StrokePen : MaskTool.Pen,
                VirtualKey.R when !shift => MaskTool.Rectangle,
                VirtualKey.E when !shift => MaskTool.Ellipse,
                VirtualKey.F when !shift => MaskTool.Freehand,
                _ => null,
            };
            if (tool is { } chosen)
            {
                session.SetMaskTool(chosen);
                return true;
            }
        }

        MaskBox? box = EditedMask();
        IReadOnlyList<int> selected = session.SelectedMaskVertices;
        switch (key)
        {
            case VirtualKey.Escape when session.MaskTool != MaskTool.Select:
                session.SetMaskTool(MaskTool.Select);
                return true;
            case VirtualKey.Escape when selected.Count > 0:
                session.SelectMaskVertices([]);
                return true;
            case VirtualKey.A when control && box is { EditsVertices: true } all:
                session.SelectMaskVertices([.. Enumerable.Range(0, all.Vertices.Length)]);
                return true;
            case VirtualKey.Delete or VirtualKey.Back when box is { EditsVertices: true } shown && selected.Count > 0:
                if (MaskHandles.Remove(shown.Vertices, selected, shown.Mask.Shape) is { } left)
                {
                    _maskAt = _properties?.KeyframeTimeAtPlayhead(session.Sequence.GetClip(shown.ClipId).Clip) ?? _maskAt;
                    SetVertices(shown, left, "Quitar puntos de la máscara", merge: false);
                    session.SelectMaskVertices([]);
                }
                else
                {
                    session.Notify(NotificationSeverity.Informational, "Quitar puntos", "La máscara se quedaría sin forma: quita la máscara desde su lista.");
                }

                return true;
            case VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down when box is { EditsVertices: true } nudged && selected.Count > 0 && !control:
                float step = shift ? 10 : 1;
                Vector2 pixels = key switch
                {
                    VirtualKey.Left => new(-step, 0),
                    VirtualKey.Right => new(step, 0),
                    VirtualKey.Up => new(0, -step),
                    _ => new(0, step),
                };
                Vector2 center = MaskHandles.BoxToImage(nudged, Vector2.Zero);
                Vector2 delta = MaskHandles.ImageToBox(nudged, center + pixels);
                _maskAt = _properties?.KeyframeTimeAtPlayhead(session.Sequence.GetClip(nudged.ClipId).Clip) ?? _maskAt;
                session.Execute(Edits.SetMaskVertices(session.SequenceId, nudged.ClipId, nudged.EffectId,
                    MaskHandles.MoveVertices(nudged.Vertices, selected, delta), _maskAt, "Mover puntos de la máscara", $"mascara-flechas|{nudged.EffectId}"));
                return true;
        }

        return false;
    }
}
