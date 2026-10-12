using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Render;
using Windows.Foundation;
using K = Rodaje.Core.Model.BuiltInEffects.ChromaKeyParameters;
using P = Rodaje.Core.Model.BuiltInEffects.CropParameters;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Imagen en el visor: el recorte que se ajusta desde Filtros (imagen entera, lo quitado en sombra, ocho asas; un paso del
/// historial por arrastre y, con keyframes, el del cursor), el cuentagotas del croma (un clic toma el color de la imagen
/// sin croma ni color) y la cortina de antes y después, que solo cambia la vista previa.
/// </summary>
internal sealed partial class PlayerView
{
    private readonly Rectangle[] _cropGrips = new Rectangle[8];
    private CropBox? _cropStart;
    private CropGrip _cropGrip;
    private Vector2 _cropFrom;
    private Time _cropAt;
    private (string ClipId, string EffectId, ColorPick Purpose)? _keyPick;
    private double _compareSplit = 0.5;
    private bool _compareDragging;
    private PreviewBypass _bypassSent;

    private void AttachImageTools()
    {
        for (int i = 0; i < _cropGrips.Length; i++)
        {
            _cropGrips[i] = new Rectangle
            {
                Width = 8,
                Height = 8,
                Fill = (Brush)Application.Current.Resources["TextBrush"],
                Stroke = (Brush)Application.Current.Resources["AccentBrush"],
                StrokeThickness = 1,
            };
            CropCanvas.Children.Add(_cropGrips[i]);
        }

        Session!.CropEditingChanged += (_, _) => UpdateCropHandles();
        Session.SelectionChanged += (_, _) => UpdateCropHandles();
        Session.ProjectEdited += (_, _) => UpdateCropHandles();
        Session.TransportChanged += (_, _) => UpdateCropHandles();
        Session.KeyPickRequested += (_, target) => BeginKeyPick((target.ClipId, target.EffectId, ColorPick.Key));
        Session.ColorPickRequested += (_, target) => BeginKeyPick(target);
        VideoPanel.SizeChanged += (_, _) =>
        {
            UpdateCropHandles();
            UpdateCompare();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && _keyPick is not null)
            {
                EndKeyPick();
                e.Handled = true;
            }
        };
    }

    private CropBox? EditedCrop()
    {
        if (Session is not { EditedCropId: { } cropId } session)
        {
            return null;
        }

        foreach (string id in session.SelectedClipIds)
        {
            if (session.Sequence.FindClip(id) is { } found && found.Clip.Effects.Find(e => e.Id == cropId && e.Enabled) is { } effect)
            {
                return CropHandles.Find(session.Project, session.Sequence, found.Clip, effect, session.Position);
            }
        }

        return null;
    }

    private void UpdateCropHandles()
    {
        CropBox? box = _cropStart is null ? EditedCrop() : EditedCrop() ?? _cropStart;
        PreviewBypass bypass = box is null ? PreviewBypass.None : PreviewBypass.Crop;
        if (bypass != _bypassSent)
        {
            _bypassSent = bypass;
            _engine?.SetBypass(bypass);
        }

        if (box is not { } shown || Image() is not { } frame)
        {
            CropCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        CropCanvas.Visibility = Visibility.Visible;
        (Vector2[] image, Vector2[] crop, Vector2[] edges) = CropHandles.Outline(shown);
        var shade = new PathGeometry { FillRule = FillRule.EvenOdd };
        shade.Figures.Add(Figure(image));
        shade.Figures.Add(Figure(crop));
        CropShade.Data = shade;
        var points = new PointCollection();
        foreach (Vector2 p in crop)
        {
            Vector2 panel = frame.ToPanel(p);
            points.Add(new Point(panel.X, panel.Y));
        }

        CropOutline.Points = points;
        Vector2[] grips = [.. crop.Concat(edges).Select(frame.ToPanel)];
        for (int i = 0; i < _cropGrips.Length; i++)
        {
            Canvas.SetLeft(_cropGrips[i], grips[i].X - 4);
            Canvas.SetTop(_cropGrips[i], grips[i].Y - 4);
        }

        PathFigure Figure(Vector2[] corners)
        {
            var figure = new PathFigure { IsClosed = true, StartPoint = ToPoint(corners[0]) };
            for (int i = 1; i < corners.Length; i++)
            {
                figure.Segments.Add(new LineSegment { Point = ToPoint(corners[i]) });
            }

            return figure;
        }

        Point ToPoint(Vector2 sequence)
        {
            Vector2 panel = frame.ToPanel(sequence);
            return new Point(panel.X, panel.Y);
        }
    }

    /// <summary>Atiende el clic si es el cuentagotas o un asa del recorte que se ajusta.</summary>
    private bool ImagePointerPressed(PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(VideoPanel);
        if (!point.Properties.IsLeftButtonPressed || Session is null || Image() is not { } frame)
        {
            return false;
        }

        if (_keyPick is { } pick)
        {
            Vector2 sequence = frame.ToSequence(point.Position.X, point.Position.Y);
            EndKeyPick();
            _ = PickKeyAsync(pick, sequence);
            e.Handled = true;
            return true;
        }

        if (EditedCrop() is not { } box || Session.Sequence.FindClip(box.ClipId) is not { } found || _properties is null
            || !_properties.IsPlayheadInside(found.Clip))
        {
            return false;
        }

        (_, Vector2[] crop, Vector2[] edges) = CropHandles.Outline(box);
        var panel = new Vector2((float)point.Position.X, (float)point.Position.Y);
        CropGrip grip = CropHandles.Hit([.. crop.Select(frame.ToPanel)], [.. edges.Select(frame.ToPanel)], panel);
        if (grip == CropGrip.None)
        {
            return false;
        }

        _cropStart = box;
        _cropGrip = grip;
        _cropFrom = frame.ToSequence(panel.X, panel.Y);
        _cropAt = _properties.KeyframeTimeAtPlayhead(found.Clip);
        Session.BeginGesture(grip == CropGrip.Body ? "Mover el recorte" : "Recortar");
        VideoPanel.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private bool ImagePointerMoved(PointerRoutedEventArgs e)
    {
        if (_cropStart is not { } start || Image() is not { } frame || Session is null)
        {
            return false;
        }

        Point position = e.GetCurrentPoint(VideoPanel).Position;
        CropRect crop = CropHandles.Dragged(start, _cropGrip, _cropFrom, frame.ToSequence(position.X, position.Y));
        string clipId = start.ClipId, effectId = start.EffectId;
        Time at = _cropAt;
        Session.Execute(new SequenceEditCommand("Recortar", Session.SequenceId, ctx => SetCrop(ctx, clipId, effectId, crop, at),
            mergeKey: $"recorte|{effectId}|{at.Ticks}"));
        e.Handled = true;
        return true;
    }

    private bool ImagePointerReleased()
    {
        if (_cropStart is null)
        {
            return false;
        }

        _cropStart = null;
        Session?.EndGesture();
        VideoPanel.ReleasePointerCaptures();
        UpdateCropHandles();
        return true;
    }

    internal static void SetCrop(EditContext ctx, string clipId, string effectId, CropRect crop, Time at)
    {
        EffectEdits.SetParameter(ctx, clipId, effectId, P.Left, crop.Left, at);
        EffectEdits.SetParameter(ctx, clipId, effectId, P.Top, crop.Top, at);
        EffectEdits.SetParameter(ctx, clipId, effectId, P.Right, crop.Right, at);
        EffectEdits.SetParameter(ctx, clipId, effectId, P.Bottom, crop.Bottom, at);
    }

    private void BeginKeyPick((string ClipId, string EffectId, ColorPick Purpose) target)
    {
        if (Session?.Sequence.FindClip(target.ClipId) is not { } found || !(_properties?.IsPlayheadInside(found.Clip) ?? false))
        {
            Session?.Notify(NotificationSeverity.Informational, "Cuentagotas", "Lleva el cursor sobre el clip para tomar el color en el visor.");
            return;
        }

        _keyPick = target;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
        StatusText.Text = target.Purpose switch
        {
            ColorPick.WhiteBalance => "Haz clic en algo que debería ser blanco o gris. Esc cancela.",
            ColorPick.Qualifier => "Haz clic en el color que quieres cambiar. Esc cancela.",
            _ => "Haz clic en el fondo que quieres quitar. Esc cancela.",
        };
        Focus(FocusState.Programmatic);
    }

    private void EndKeyPick()
    {
        _keyPick = null;
        ProtectedCursor = null;
        StatusText.Text = "";
    }

    /// <summary>
    /// Compone el cuadro sin lo que el cuentagotas no debe ver, promedia 5×5 píxeles bajo el clic y lo aplica en un paso:
    /// el color clave (sin croma ni color), el balance de blancos (sin color) o el tono del calificador (sin él).
    /// </summary>
    private async Task PickKeyAsync((string ClipId, string EffectId, ColorPick Purpose) target, Vector2 sequence)
    {
        if (_engine is null || Session is null)
        {
            return;
        }

        PreviewBypass bypass = target.Purpose switch
        {
            ColorPick.WhiteBalance => PreviewBypass.Grading,
            ColorPick.Qualifier => PreviewBypass.Qualifier,
            _ => PreviewBypass.Key | PreviewBypass.Grading,
        };
        FrameCapture capture;
        try
        {
            capture = await _engine.CaptureFrameAsync(bypass).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or TaskCanceledException)
        {
            return;
        }

        if (KeyColor.Average(capture.Rgba, capture.Width, capture.Height, sequence.X / Session.Sequence.Width,
            sequence.Y / Session.Sequence.Height) is not { } color)
        {
            return;
        }

        if (target.Purpose != ColorPick.Key)
        {
            ApplyColorPick(target, color);
            return;
        }

        Session.Execute(new SequenceEditCommand("Elegir el color clave", Session.SequenceId, ctx =>
        {
            EffectEdits.SetParameter(ctx, target.ClipId, target.EffectId, K.Red, Math.Round(color.R, 4), Time.Zero);
            EffectEdits.SetParameter(ctx, target.ClipId, target.EffectId, K.Green, Math.Round(color.G, 4), Time.Zero);
            EffectEdits.SetParameter(ctx, target.ClipId, target.EffectId, K.Blue, Math.Round(color.B, 4), Time.Zero);
        }));
    }

    private void OnCompareClick(object sender, RoutedEventArgs e) => UpdateCompare();

    private void UpdateCompare()
    {
        bool on = CompareButton.IsChecked == true;
        _engine?.SetCompare(on ? (float)_compareSplit : null);
        if (!on || Placement() is not { } placement)
        {
            CompareCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        CompareCanvas.Visibility = Visibility.Visible;
        PanelRect image = placement.Image;
        double x = image.Left + (image.Width * _compareSplit);
        Canvas.SetLeft(CompareHit, x - (CompareHit.Width / 2));
        Canvas.SetTop(CompareHit, image.Top);
        CompareHit.Height = image.Height;
        Canvas.SetLeft(CompareLine, x - 1);
        Canvas.SetTop(CompareLine, image.Top);
        CompareLine.Height = image.Height;
        Canvas.SetLeft(CompareGrip, x - (CompareGrip.Width / 2));
        Canvas.SetTop(CompareGrip, image.Top + (image.Height / 2) - (CompareGrip.Height / 2));
        Canvas.SetLeft(CompareBefore, Math.Max(image.Left + 8, x - 60));
        Canvas.SetTop(CompareBefore, image.Top + 8);
        Canvas.SetLeft(CompareAfter, Math.Min(image.Right - 60, x + 10));
        Canvas.SetTop(CompareAfter, image.Top + 8);
    }

    private void OnComparePressed(object sender, PointerRoutedEventArgs e)
    {
        _compareDragging = ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnCompareMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_compareDragging || Placement() is not { } placement)
        {
            return;
        }

        PanelRect image = placement.Image;
        _compareSplit = Math.Clamp((e.GetCurrentPoint(CompareCanvas).Position.X - image.Left) / image.Width, 0, 1);
        UpdateCompare();
        e.Handled = true;
    }

    private void OnCompareReleased(object sender, PointerRoutedEventArgs e)
    {
        _compareDragging = false;
        ((UIElement)sender).ReleasePointerCaptures();
    }
}
