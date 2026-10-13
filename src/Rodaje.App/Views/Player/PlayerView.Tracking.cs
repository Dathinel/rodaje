using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services;
using Rodaje.App.Services.Tracking;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Windows.Foundation;
using O = Rodaje.Core.Model.BuiltInEffects.TrackOptions;

namespace Rodaje.App.Views.Player;

/// <summary>
/// El seguimiento en el visor con la pestaña «Seguimiento» a la vista: cada punto con su patrón (línea llena) y su zona de
/// búsqueda (punteada), en el color del seguimiento, naranja si el cuadro es dudoso y rojo si se perdió. Arrastrar el centro
/// mueve el punto de partida o, en un cuadro ya seguido, lo corrige a mano; las esquinas cambian el patrón o la búsqueda.
/// Cada arrastre es un paso del historial.
/// </summary>
internal sealed partial class PlayerView
{
    private TrackView? _trackStart;
    private int _trackPoint = -1;
    private TrackGrip _trackGrip;
    private string? _trackMerge;
    private int _trackGesture;

    private void AttachTrackHandles()
    {
        Session!.TrackEditingChanged += (_, _) => UpdateTrackHandles();
        Session.SelectionChanged += (_, _) => UpdateTrackHandles();
        Session.ProjectEdited += (_, _) => UpdateTrackHandles();
        Session.TransportChanged += (_, _) => UpdateTrackHandles();
        VideoPanel.SizeChanged += (_, _) => UpdateTrackHandles();
    }

    /// <summary>El seguimiento elegido en la pestaña sobre el clip elegido, si el cursor está en el clip.</summary>
    private TrackView? EditedTrack()
    {
        if (Session is not { IsTrackEditing: true, EditedTrackId: { } trackId } session)
        {
            return null;
        }

        foreach (string id in session.SelectedClipIds)
        {
            if (session.Sequence.FindClip(id) is { Track.Kind: TrackKind.Video } found
                && found.Clip.Effects.Find(e => e.Id == trackId && e.Type == BuiltInEffects.Track) is { } effect)
            {
                return TrackHandles.Find(session.Project, session.Sequence, found.Clip, effect, session.Position);
            }
        }

        return null;
    }

    private void UpdateTrackHandles()
    {
        TrackCanvas.Children.Clear();
        if (Image() is not { } frame || EditedTrack() is not { } view)
        {
            TrackCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        TrackCanvas.Visibility = Visibility.Visible;
        Windows.UI.Color color = ParseColor(view.Color);
        if (view.Points.Length > 1)
        {
            var outline = new Polygon
            {
                Stroke = new SolidColorBrush(ColorHelper.FromArgb(150, color.R, color.G, color.B)),
                StrokeThickness = 1,
                StrokeDashArray = [4, 3],
            };
            foreach (TrackPointBox p in view.Points)
            {
                Vector2 c = TrackHandles.ToPanel(view, frame, p.Position);
                outline.Points.Add(new Point(c.X, c.Y));
            }

            if (view.Points.Length == 2)
            {
                TrackCanvas.Children.Add(new Line
                {
                    X1 = outline.Points[0].X, Y1 = outline.Points[0].Y, X2 = outline.Points[1].X, Y2 = outline.Points[1].Y,
                    Stroke = outline.Stroke, StrokeThickness = 1, StrokeDashArray = [4, 3],
                });
            }
            else
            {
                TrackCanvas.Children.Add(outline);
            }
        }

        for (int i = 0; i < view.Points.Length; i++)
        {
            TrackPointBox p = view.Points[i];
            Windows.UI.Color state = p.Confidence switch
            {
                0 => ColorHelper.FromArgb(255, 0xEF, 0x53, 0x50),
                > 0 when p.Confidence < view.Threshold => ColorHelper.FromArgb(255, 0xFF, 0xA7, 0x26),
                _ => color,
            };
            bool selected = Session!.SelectedTrackPoint == i;
            AddBox(TrackHandles.Box(view, frame, p.Position, p.Search), state, dashed: true, selected ? 1.2 : 1);
            AddBox(TrackHandles.Box(view, frame, p.Position, p.Pattern), state, dashed: false, selected ? 2 : 1.4);
            Vector2 c = TrackHandles.ToPanel(view, frame, p.Position);
            var brush = new SolidColorBrush(state);
            TrackCanvas.Children.Add(new Line { X1 = c.X - 6, Y1 = c.Y, X2 = c.X + 6, Y2 = c.Y, Stroke = brush, StrokeThickness = 1.4 });
            TrackCanvas.Children.Add(new Line { X1 = c.X, Y1 = c.Y - 6, X2 = c.X, Y2 = c.Y + 6, Stroke = brush, StrokeThickness = 1.4 });
            var label = new TextBlock
            {
                Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Foreground = brush,
                FontSize = 11,
            };
            Canvas.SetLeft(label, c.X + 6);
            Canvas.SetTop(label, c.Y - 18);
            TrackCanvas.Children.Add(label);
        }
    }

    private void AddBox(Vector2[] corners, Windows.UI.Color color, bool dashed, double thickness)
    {
        var halo = new Polygon { Stroke = new SolidColorBrush(ColorHelper.FromArgb(110, 0, 0, 0)), StrokeThickness = thickness + 2 };
        var box = new Polygon { Stroke = new SolidColorBrush(color), StrokeThickness = thickness };
        if (dashed)
        {
            box.StrokeDashArray = [3, 3];
        }

        foreach (Vector2 c in corners)
        {
            halo.Points.Add(new Point(c.X, c.Y));
            box.Points.Add(new Point(c.X, c.Y));
        }

        TrackCanvas.Children.Add(halo);
        TrackCanvas.Children.Add(box);
    }

    private bool TrackPointerPressed(PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(VideoPanel);
        if (!point.Properties.IsLeftButtonPressed || Session is null || Image() is not { } frame || EditedTrack() is not { } view)
        {
            return false;
        }

        (int index, TrackGrip grip) = TrackHandles.Hit(view, frame, new Vector2((float)point.Position.X, (float)point.Position.Y));
        if (grip == TrackGrip.None)
        {
            return false;
        }

        _trackStart = view;
        _trackPoint = index;
        _trackGrip = grip;
        _trackMerge = $"seguimiento-punto|{view.TrackId}|{++_trackGesture}";
        Session.SelectTrackPoint(index);
        Session.BeginGesture(grip switch
        {
            TrackGrip.Pattern => "Cambiar el patrón del punto",
            TrackGrip.Search => "Cambiar la zona de búsqueda",
            _ => view.Points[index].Confidence >= 0 ? "Corregir el punto a mano" : "Mover el punto de seguimiento",
        });
        VideoPanel.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private bool TrackPointerMoved(PointerRoutedEventArgs e)
    {
        if (_trackStart is not { } start || Session is null || Image() is not { } frame
            || Session.Sequence.FindClip(start.ClipId) is not { } found
            || found.Clip.Effects.Find(x => x.Id == start.TrackId) is not { } effect)
        {
            return false;
        }

        Point position = e.GetCurrentPoint(VideoPanel).Position;
        if (TrackHandles.ToImage(start, frame, new Vector2((float)position.X, (float)position.Y)) is not { } image)
        {
            return true;
        }

        TrackPointSpec[] specs = PointTrack.ParsePoints(effect.Options.GetValueOrDefault(O.Points));
        if (_trackPoint < 0 || _trackPoint >= specs.Length)
        {
            return true;
        }

        TrackPointBox shown = start.Points[_trackPoint];
        string clipId = start.ClipId, trackId = start.TrackId;
        switch (_trackGrip)
        {
            case TrackGrip.Center when shown.Confidence >= 0
                && PointTrack.Parse(effect.Options.GetValueOrDefault(O.Samples)) is { } samples && samples.IndexOf(start.Source) >= 0:
                // En un cuadro seguido: corrige ese cuadro (confianza 1); «Adelante» sigue desde aquí.
                Session.Execute(TrackEdits.SetSamples(Session.SequenceId, clipId, trackId, samples.WithPoint(start.Source, _trackPoint, image),
                    "Corregir el punto a mano", _trackMerge));
                break;
            case TrackGrip.Center:
                specs[_trackPoint] = specs[_trackPoint] with { Position = image };
                Session.Execute(TrackEdits.SetPoints(Session.SequenceId, clipId, trackId, specs, "Mover el punto de seguimiento", _trackMerge));
                break;
            case TrackGrip.Pattern:
                Vector2 pattern = TrackHandles.Resized(shown.Position, image, new Vector2(0.004f));
                specs[_trackPoint] = specs[_trackPoint] with { Pattern = pattern, Search = Vector2.Max(specs[_trackPoint].Search, pattern * 1.2f) };
                Session.Execute(TrackEdits.SetPoints(Session.SequenceId, clipId, trackId, specs, "Cambiar el patrón del punto", _trackMerge));
                break;
            case TrackGrip.Search:
                specs[_trackPoint] = specs[_trackPoint] with { Search = TrackHandles.Resized(shown.Position, image, specs[_trackPoint].Pattern * 1.2f) };
                Session.Execute(TrackEdits.SetPoints(Session.SequenceId, clipId, trackId, specs, "Cambiar la zona de búsqueda", _trackMerge));
                break;
        }

        e.Handled = true;
        return true;
    }

    private bool TrackPointerReleased()
    {
        if (_trackStart is null)
        {
            return false;
        }

        _trackStart = null;
        _trackGrip = TrackGrip.None;
        Session?.EndGesture();
        VideoPanel.ReleasePointerCaptures();
        UpdateTrackHandles();
        return true;
    }
}
