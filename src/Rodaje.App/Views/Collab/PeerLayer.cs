using Microsoft.UI.Dispatching;
using Rodaje.App.Views.Inspector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services;
using Rodaje.App.Services.Collab;
using Rodaje.App.Theme;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Model;
using Windows.Foundation;
using Windows.UI;

namespace Rodaje.App.Views.Collab;

/// <summary>
/// Lo que se ve de los demás encima de la línea de tiempo, sin tocar su dibujo: el cabezal de cada persona con su nombre y su
/// color, el contorno de lo que tiene seleccionado y, en la cabecera de cada pista que tiene reservada alguien, una franja de
/// su color y «Ana edita aquí». Todo con pointer transparente: no estorba. Se repinta solo cuando algo cambió.
/// </summary>
internal sealed class PeerLayer
{
    private const double HeadersWidth = 172;
    private const double TagHeight = 18;

    private readonly Canvas _canvas;
    private readonly CollabPresence _presence;
    private readonly EditorSession _session;
    private readonly TimelineRenderer _renderer;
    private readonly DispatcherQueueTimer _timer;
    private bool _dirty = true;
    private string _signature = "";
    private bool _empty = true;

    public PeerLayer(Canvas canvas, CollabPresence presence, EditorSession session, TimelineRenderer renderer, DispatcherQueue queue)
    {
        _canvas = canvas;
        _presence = presence;
        _session = session;
        _renderer = renderer;
        presence.Changed += () => queue.RunLater(() => _dirty = true);
        session.HistoryChanged += (_, _) => _dirty = true;
        session.SelectionChanged += (_, _) => _dirty = true;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(66);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        bool any = _presence.Peers.Count > 0 || _presence.Locks.Count > 0;
        if (!any && _empty)
        {
            return;
        }

        TimelineViewport vp = _renderer.Viewport;
        string signature = string.Join('|', vp.ScrollX, vp.PixelsPerSecond, vp.Width, _renderer.ScrollY, _renderer.LanesHeight, _renderer.Layout?.TotalHeight, _presence.Peers.Count);
        if (!_dirty && signature == _signature)
        {
            // Los cabezales se mueven sin que cambie nada más: se comparan sus posiciones.
            string moved = string.Join(',', _presence.Peers.Values.Select(p => p.PlayheadSeconds));
            if (moved == _lastPositions)
            {
                return;
            }

            _lastPositions = moved;
        }

        _signature = signature;
        _lastPositions = string.Join(',', _presence.Peers.Values.Select(p => p.PlayheadSeconds));
        _dirty = false;
        Render();
    }

    private string _lastPositions = "";

    private void Render()
    {
        _canvas.Children.Clear();
        _empty = true;
        TrackLayout? rows = _renderer.Layout;
        TimelineViewport vp = _renderer.Viewport;
        double laneTop = TimelineRenderer.RulerHeight;
        double height = laneTop + _renderer.LanesHeight;
        _canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, HeadersWidth + Math.Max(vp.Width, 0), Math.Max(height, 0)) };

        if (rows is not null)
        {
            Sequence sequence = _session.Sequence;
            foreach (TrackLock trackLock in _presence.Locks.Values)
            {
                TrackRow? row = rows.Rows.FirstOrDefault(r => r.TrackId == trackLock.TrackId);
                if (row is null)
                {
                    continue;
                }

                double top = laneTop + row.Top - _renderer.ScrollY;
                if (top + row.Height < laneTop || top > height)
                {
                    continue;
                }

                AddLock(trackLock, top, row.Height);
            }

            foreach (PeerPresence peer in _presence.Peers.Values)
            {
                AddSelection(peer, sequence, rows, vp, laneTop, height);
            }
        }

        foreach (PeerPresence peer in _presence.Peers.Values)
        {
            AddPlayhead(peer, vp, height);
        }
    }

    private void AddLock(TrackLock trackLock, double top, double rowHeight)
    {
        Color color = CollabPalette.ColorOf(trackLock.ColorIndex);
        var strip = new Rectangle { Width = 4, Height = Math.Max(rowHeight, 0), Fill = new SolidColorBrush(color) };
        Canvas.SetTop(strip, top);
        _canvas.Children.Add(strip);
        string text = trackLock.Mine ? "Reservada para ti" : $"{trackLock.OwnerAlias} edita aquí";
        var tag = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(trackLock.Mine ? (byte)120 : (byte)235, color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 1),
            MaxWidth = HeadersWidth - 12,
            Child = new TextBlock
            {
                Text = text,
                FontSize = ThemeResources.Size("CaptionFontSize"),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 20, 20, 20)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        Canvas.SetLeft(tag, 8);
        Canvas.SetTop(tag, top + Math.Max(rowHeight - TagHeight - 4, 2));
        _canvas.Children.Add(tag);
        _empty = false;
    }

    private void AddSelection(PeerPresence peer, Sequence sequence, TrackLayout rows, TimelineViewport vp, double laneTop, double bottom)
    {
        if (peer.SelectedClips.Count == 0)
        {
            return;
        }

        Color color = CollabPalette.ColorOf(peer.ColorIndex);
        HashSet<string> wanted = [.. peer.SelectedClips];
        foreach (Track track in sequence.VideoTracks.Concat(sequence.AudioTracks))
        {
            TrackRow? row = rows.Rows.FirstOrDefault(r => r.TrackId == track.Id);
            if (row is null)
            {
                continue;
            }

            double top = laneTop + row.Top - _renderer.ScrollY;
            if (top + row.Height < laneTop || top > bottom)
            {
                continue;
            }

            foreach (Clip clip in track.Clips.Where(c => wanted.Contains(c.Id)))
            {
                double x1 = HeadersWidth + vp.TimeToX(clip.Start);
                double x2 = HeadersWidth + vp.TimeToX(clip.End);
                if (x2 < HeadersWidth || x1 > HeadersWidth + vp.Width)
                {
                    continue;
                }

                var box = new Rectangle
                {
                    Width = Math.Max(x2 - x1, 2),
                    Height = Math.Max(row.Height - 2, 2),
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(34, color.R, color.G, color.B)),
                    RadiusX = 3,
                    RadiusY = 3,
                };
                Canvas.SetLeft(box, x1);
                Canvas.SetTop(box, top + 1);
                _canvas.Children.Add(box);
                _empty = false;
            }
        }
    }

    private void AddPlayhead(PeerPresence peer, TimelineViewport vp, double height)
    {
        double x = HeadersWidth + vp.TimeToX(Rodaje.Core.Timing.Time.FromSeconds(peer.PlayheadSeconds));
        if (x < HeadersWidth - 1 || x > HeadersWidth + vp.Width + 1)
        {
            return;
        }

        Color color = CollabPalette.ColorOf(peer.ColorIndex);
        var line = new Rectangle { Width = 2, Height = Math.Max(height, 0), Fill = new SolidColorBrush(color), Opacity = 0.85 };
        Canvas.SetLeft(line, Math.Round(x) - 1);
        _canvas.Children.Add(line);
        var tag = new Border
        {
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 1),
            Child = new TextBlock
            {
                Text = peer.Alias,
                FontSize = ThemeResources.Size("CaptionFontSize"),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 20, 20, 20)),
                MaxWidth = 120,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        Canvas.SetLeft(tag, Math.Round(x) + 3);
        Canvas.SetTop(tag, 2);
        _canvas.Children.Add(tag);
        _empty = false;
    }
}
