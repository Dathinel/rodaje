using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.SafeZones;
using Rodaje.Packages.Content;
using Windows.Foundation;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Zonas seguras del visor (<c>rodaje.safezones</c>): los márgenes clásicos o dónde tapa la interfaz de una red (botones,
/// pie de foto, nombre), dibujados a la manera de Rodaje, sin logotipos ni capturas. Se eligen en el botón de la cabecera:
/// Ninguno, Clásicos, una red, las de los paquetes o las propias, que se hacen arrastrando rectángulos.
/// </summary>
internal sealed partial class PlayerView
{
    private const string SafeZonesNone = "";

    private static readonly SolidColorBrush ZoneFill = new(ColorHelper.FromArgb(0x70, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush ZoneStroke = new(ColorHelper.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush ZoneText = new(ColorHelper.FromArgb(0xFF, 0xF2, 0xF2, 0xF2));
    private static readonly SolidColorBrush NoteFill = new(ColorHelper.FromArgb(0xC0, 0x1A, 0x1A, 0x1A));

    private IReadOnlyList<SafeZoneChoice> _safeZoneChoices = SafeZoneCatalog.BuiltIn;
    private SafeZoneEditor? _zoneEditor;

    /// <summary>Lo que el visor muestra ahora: el juego elegido, o nulo con «Ninguno».</summary>
    internal SafeZoneChoice? ShownSafeZones =>
        _services is { Settings.Current.ShowSafeAreas: true } services
            ? SafeZoneCatalog.Find(_safeZoneChoices, services.Settings.Current.SafeZones)
            : null;

    /// <summary>Para la autoprueba: elige un juego por su referencia (o ninguno) como desde el menú.</summary>
    internal void ChooseSafeZones(string? reference) => SelectSafeZones(reference ?? SafeZonesNone);

    /// <summary>Para la autoprueba: abre el editor con lo que se ve, añade una zona y la elige; con <c>false</c> lo cierra sin guardar.</summary>
    internal int EditSafeZonesForSelfTest(bool open)
    {
        if (!open)
        {
            EndSafeZoneEdit();
            return 0;
        }

        BeginSafeZoneEdit(null);
        OnSafeZoneAdd(this, new RoutedEventArgs());
        return _zoneEditor?.Zones.Count ?? 0;
    }

    private void AttachSafeZones()
    {
        if (_services is not { } services)
        {
            return;
        }

        RefreshSafeZoneChoices();
        services.Packages.Changed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            RefreshSafeZoneChoices();
            UpdateSafeAreas();
        });
    }

    private void RefreshSafeZoneChoices()
    {
        if (_services is null)
        {
            return;
        }

        var problems = new List<string>();
        _safeZoneChoices = SafeZoneCatalog.All(_services.Packages.Installed, problems);
        foreach (string problem in problems)
        {
            Session?.Notify(NotificationSeverity.Warning, "Zonas seguras", problem);
        }
    }

    private void ToggleSafeZones()
    {
        if (_services is null)
        {
            return;
        }

        bool show = !_services.Settings.Current.ShowSafeAreas;
        SelectSafeZones(show ? _services.Settings.Current.SafeZones ?? SafeZoneCatalog.Classic : SafeZonesNone);
    }

    private void SelectSafeZones(string reference)
    {
        if (_services is null)
        {
            return;
        }

        AppSettingsCurrent().ShowSafeAreas = reference.Length > 0;
        if (reference.Length > 0)
        {
            AppSettingsCurrent().SafeZones = reference;
        }

        UpdateSafeAreas();
        UpdateToolTips();
    }

    private Services.Settings.AppSettings AppSettingsCurrent() => _services!.Settings.Current;

    private string SafeZonesToolTip() => ShownSafeZones is { } shown
        ? $"Zonas seguras: {shown.Name}. Clic para elegir otras"
        : "Zonas seguras: márgenes clásicos o la interfaz de las apps de video corto…";

    private void OnSafeZonesMenuOpening(object sender, object e)
    {
        if (_services is null || Session is null)
        {
            return;
        }

        RefreshSafeZoneChoices();
        SafeZonesMenu.Items.Clear();
        SafeZoneChoice? shown = ShownSafeZones;
        AddZoneItem("Ninguno", SafeZonesNone, shown is null, null);
        foreach (SafeZoneChoice choice in _safeZoneChoices)
        {
            // Separadores: tras los clásicos y entre lo de fábrica y lo de paquetes o propio.
            int index = SafeZonesMenu.Items.Count;
            bool firstNetwork = choice.Reference == SafeZoneCatalog.BuiltIn[Math.Min(1, SafeZoneCatalog.BuiltIn.Count - 1)].Reference;
            bool firstExtra = choice.Origin != SafeZoneOrigin.BuiltIn
                && _safeZoneChoices.First(c => c.Origin != SafeZoneOrigin.BuiltIn).Reference == choice.Reference;
            if ((firstNetwork || firstExtra) && index > 0)
            {
                SafeZonesMenu.Items.Add(new MenuFlyoutSeparator());
            }

            string aspect = choice.Preset.AspectText;
            string label = choice.Origin == SafeZoneOrigin.Own ? $"{choice.Name} (propias)" : choice.Name;
            if (aspect.Length > 0 && !label.Contains(aspect, StringComparison.Ordinal))
            {
                label += " · " + aspect;
            }

            string? help = choice.Preset.Fits(Session.Sequence.Width, Session.Sequence.Height)
                ? null
                : $"Pensadas para {aspect}; el proyecto es {Services.Format.ProjectFormats.AspectName(Session.Sequence.Width, Session.Sequence.Height)}.";
            AddZoneItem(label, choice.Reference, shown?.Reference == choice.Reference, help);
        }

        SafeZonesMenu.Items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = "Nuevas zonas propias…" };
        ToolTipService.SetToolTip(create, "Empieza de las zonas que se ven ahora: arrastra los rectángulos y guárdalas con un nombre.");
        create.Click += (_, _) => BeginSafeZoneEdit(null);
        SafeZonesMenu.Items.Add(create);
        if (shown is { Origin: SafeZoneOrigin.Own })
        {
            var edit = new MenuFlyoutItem { Text = $"Editar «{shown.Name}»…" };
            edit.Click += (_, _) => BeginSafeZoneEdit(shown);
            SafeZonesMenu.Items.Add(edit);
        }

        void AddZoneItem(string text, string reference, bool isChecked, string? tip)
        {
            var item = new RadioMenuFlyoutItem { Text = text, GroupName = "SafeZones", IsChecked = isChecked };
            if (tip is not null)
            {
                ToolTipService.SetToolTip(item, tip);
            }

            item.Click += (_, _) => SelectSafeZones(reference);
            SafeZonesMenu.Items.Add(item);
        }
    }

    /// <summary>Dibuja las zonas elegidas (o las del editor) sobre el rectángulo de la imagen, con el zoom actual.</summary>
    private void UpdateSafeAreas()
    {
        UpdateCanvasOutline();
        SafeAreasButton.Foreground = (Brush)Application.Current.Resources[ShownSafeZones is null && _zoneEditor is null ? "TextBrush" : "AccentBrush"];
        SafeZonePreset? preset = _zoneEditor?.Preset ?? ShownSafeZones?.Preset;
        ViewerPlacement? placement = Placement();
        bool show = preset is not null && placement is not null;
        SafeAreaCanvas.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SafeAreaCanvas.Children.Clear();
        if (!show)
        {
            return;
        }

        ViewerPlacement p = placement!.Value;
        double line = p.Hairline;
        for (int i = 0; i < preset!.Zones.Count; i++)
        {
            DrawZone(preset.Zones[i], p, line, selected: _zoneEditor?.Selected == i);
        }

        if (preset.Center)
        {
            PanelRect center = p.Fraction(0.5, 0.5, 0, 0);
            AddRect(new PanelRect(center.Left - 8, center.Top, 16, line), (Brush)Application.Current.Resources["TextMutedBrush"], null, 0);
            AddRect(new PanelRect(center.Left, center.Top - 8, line, 16), (Brush)Application.Current.Resources["TextMutedBrush"], null, 0);
        }

        if (Session is { } session && !preset.Fits(session.Sequence.Width, session.Sequence.Height))
        {
            PanelRect image = p.Image;
            var note = new Border
            {
                Background = NoteFill,
                Padding = new Thickness(6, 2, 6, 2),
                CornerRadius = new CornerRadius(2),
                MaxWidth = Math.Max(0, image.Width - 8),
                Child = new TextBlock
                {
                    Text = $"Pensadas para {preset.AspectText}",
                    Style = (Style)Application.Current.Resources["CaptionTextStyle"],
                    Foreground = ZoneText,
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            note.Measure(new Size(image.Width, image.Height));
            Canvas.SetLeft(note, image.Left + ((image.Width - note.DesiredSize.Width) / 2));
            Canvas.SetTop(note, image.Top + (image.Height / 2) + 14);
            SafeAreaCanvas.Children.Add(note);
        }
    }

    private void DrawZone(SafeZone zone, ViewerPlacement placement, double line, bool selected)
    {
        PanelRect rect = placement.Fraction(zone.Left, zone.Top, zone.Width, zone.Height);
        Brush accent = (Brush)Application.Current.Resources["AccentBrush"];
        if (zone.Kind == SafeZoneKind.Safe)
        {
            Rectangle edge = AddRect(rect, null, selected ? accent : (Brush)Application.Current.Resources["TextMutedBrush"], line);
            edge.StrokeDashArray = [4, 3];
            AddLabel(zone.Label, rect, centered: false);
        }
        else
        {
            Rectangle shade = AddRect(rect, ZoneFill, selected ? accent : ZoneStroke, line);
            shade.StrokeDashArray = selected ? null : [3, 2];
            AddLabel(zone.Label, rect, centered: true);
        }

        if (selected)
        {
            foreach ((double x, double y) in (ReadOnlySpan<(double, double)>)[(rect.Left, rect.Top), (rect.Right, rect.Top), (rect.Right, rect.Bottom), (rect.Left, rect.Bottom)])
            {
                AddRect(new PanelRect(x - 4, y - 4, 8, 8), (Brush)Application.Current.Resources["TextBrush"], accent, 1);
            }
        }
    }

    private Rectangle AddRect(PanelRect rect, Brush? fill, Brush? stroke, double thickness)
    {
        var shape = new Rectangle { Fill = fill, Stroke = stroke, StrokeThickness = thickness };
        PlaceRect(shape, rect);
        SafeAreaCanvas.Children.Add(shape);
        return shape;
    }

    /// <summary>
    /// La etiqueta dentro de su zona; en una columna alta y angosta (los botones del costado) va girada para leerse entera.
    /// Si no cabe ni una línea, no se pone.
    /// </summary>
    private void AddLabel(string text, PanelRect rect, bool centered)
    {
        bool vertical = centered && rect.Width < 80 && rect.Height > rect.Width * 2;
        (double across, double along) = vertical ? (rect.Height - 8, rect.Width - 4) : (rect.Width - 8, rect.Height - 4);
        if (across < 16 || along < 12)
        {
            return;
        }

        var label = new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            Foreground = ZoneText,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = across,
            MaxHeight = along,
        };
        label.Measure(new Size(across, along));
        Size size = label.DesiredSize;
        if (vertical)
        {
            // Girado 90° alrededor de su centro, que queda en el centro de la zona.
            label.RenderTransformOrigin = new Point(0.5, 0.5);
            label.RenderTransform = new RotateTransform { Angle = 90 };
        }

        Canvas.SetLeft(label, centered ? rect.Left + ((rect.Width - size.Width) / 2) : rect.Left + 4);
        Canvas.SetTop(label, centered ? rect.Top + ((rect.Height - size.Height) / 2) : rect.Top + 2);
        SafeAreaCanvas.Children.Add(label);
    }

    // ----- Editor de zonas propias -----

    /// <summary>Empieza a editar: un juego propio que ya existe, o uno nuevo a partir de lo que se ve.</summary>
    private void BeginSafeZoneEdit(SafeZoneChoice? own)
    {
        if (_services is null || Session is null)
        {
            return;
        }

        SafeZoneChoice? from = own ?? ShownSafeZones;
        (int w, int h) = SafeZoneEditing.Aspect(Session.Sequence.Width, Session.Sequence.Height);
        List<SafeZone> zones = from is null ? [] : [.. from.Preset.Zones];
        string name = own?.Name ?? (from is { Origin: not SafeZoneOrigin.Own } ? $"{from.Name} (mías)" : "Mis zonas");
        _zoneEditor = new SafeZoneEditor(own?.Reference, zones, from?.Preset.Center == true, w, h);
        SafeZoneSetName.Text = name;
        SafeZoneEditBar.Visibility = Visibility.Visible;
        Transport.Visibility = Visibility.Collapsed;
        SafeZoneEditCanvas.Visibility = Visibility.Visible;
        UpdateZoneEditor();
    }

    private void EndSafeZoneEdit()
    {
        _zoneEditor = null;
        SafeZoneEditBar.Visibility = Visibility.Collapsed;
        Transport.Visibility = IsFullScreen ? Visibility.Collapsed : Visibility.Visible;
        SafeZoneEditCanvas.Visibility = Visibility.Collapsed;
        UpdateSafeAreas();
    }

    private void UpdateZoneEditor()
    {
        if (_zoneEditor is not { } editor)
        {
            return;
        }

        bool selected = editor.Selected >= 0;
        SafeZoneLabel.IsEnabled = selected;
        SafeZoneRemove.IsEnabled = selected;
        editor.SyncingLabel = true;
        SafeZoneLabel.Text = selected ? editor.Zones[editor.Selected].Label : "";
        editor.SyncingLabel = false;
        UpdateSafeAreas();
    }

    private void OnSafeZoneEditPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_zoneEditor is not { } editor || Placement() is not { } placement)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(SafeZoneEditCanvas);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        (double x, double y) = ToFraction(placement, point.Position);
        PanelRect image = placement.Image;
        (int index, SafeZoneHandle handle) = SafeZoneEditing.Hit(editor.Zones, x, y, 6 / image.Width, 6 / image.Height);
        editor.Selected = index;
        editor.Drag = index >= 0 ? (handle, editor.Zones[index], x, y) : null;
        if (index >= 0)
        {
            SafeZoneEditCanvas.CapturePointer(e.Pointer);
        }

        e.Handled = true;
        UpdateZoneEditor();
    }

    private void OnSafeZoneEditMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_zoneEditor is not { Drag: { } drag } editor || Placement() is not { } placement)
        {
            return;
        }

        (double x, double y) = ToFraction(placement, e.GetCurrentPoint(SafeZoneEditCanvas).Position);
        editor.Zones[editor.Selected] = SafeZoneEditing.Drag(drag.Start, drag.Handle, x - drag.X, y - drag.Y);
        e.Handled = true;
        UpdateSafeAreas();
    }

    private void OnSafeZoneEditReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_zoneEditor is { } editor)
        {
            editor.Drag = null;
            SafeZoneEditCanvas.ReleasePointerCaptures();
        }
    }

    private void OnSafeZoneEditCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_zoneEditor is { } editor)
        {
            editor.Drag = null;
        }
    }

    private static (double X, double Y) ToFraction(ViewerPlacement placement, Point point)
    {
        PanelRect image = placement.Image;
        return ((point.X - image.Left) / image.Width, (point.Y - image.Top) / image.Height);
    }

    private void OnSafeZoneLabelChanged(object sender, TextChangedEventArgs e)
    {
        if (_zoneEditor is not { SyncingLabel: false, Selected: >= 0 } editor)
        {
            return;
        }

        string text = SafeZoneLabel.Text.Trim();
        editor.Zones[editor.Selected] = editor.Zones[editor.Selected] with { Label = text.Length == 0 ? "Zona" : text[..Math.Min(text.Length, 60)] };
        UpdateSafeAreas();
    }

    private void OnSafeZoneAdd(object sender, RoutedEventArgs e)
    {
        if (_zoneEditor is not { } editor || editor.Zones.Count >= 32)
        {
            return;
        }

        editor.Zones.Add(SafeZoneEditing.NewZone(editor.Zones.Count));
        editor.Selected = editor.Zones.Count - 1;
        UpdateZoneEditor();
    }

    private void OnSafeZoneRemove(object sender, RoutedEventArgs e)
    {
        if (_zoneEditor is not { Selected: >= 0 } editor)
        {
            return;
        }

        editor.Zones.RemoveAt(editor.Selected);
        editor.Selected = -1;
        UpdateZoneEditor();
    }

    private void OnSafeZoneCancel(object sender, RoutedEventArgs e) => EndSafeZoneEdit();

    private async void OnSafeZoneSave(object sender, RoutedEventArgs e)
    {
        if (_zoneEditor is not { } editor || _services is null || Session is null)
        {
            return;
        }

        string name = SafeZoneSetName.Text.Trim();
        if (name.Length == 0 || editor.Zones.Count == 0)
        {
            Session.Notify(NotificationSeverity.Informational, "Zonas seguras", name.Length == 0 ? "Ponle un nombre." : "Añade al menos una zona.");
            return;
        }

        try
        {
            SafeZonePreset preset = editor.Preset with { Name = name[..Math.Min(name.Length, 80)] };
            string? file = editor.Reference is { } existing ? SafeZoneCatalog.OwnFile(_services.Packages.Installed, existing) : null;
            string reference = SafeZoneCatalog.SaveOwn(AnimationPreset.UserFolder, preset, file);
            await _services.Packages.RefreshAsync();
            RefreshSafeZoneChoices();
            EndSafeZoneEdit();
            SelectSafeZones(reference);
            Session.Notify(NotificationSeverity.Success, "Zonas seguras", $"«{preset.Name}» quedó en tus complementos propios y se ve en el visor.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Session.Notify(NotificationSeverity.Error, "No se pudieron guardar las zonas", ex.Message);
        }
    }

    /// <summary>Lo que se está editando: las zonas, cuál está elegida y el arrastre en curso.</summary>
    private sealed class SafeZoneEditor(string? reference, List<SafeZone> zones, bool center, int aspectWidth, int aspectHeight)
    {
        public string? Reference { get; } = reference;

        public List<SafeZone> Zones { get; } = zones;

        public int Selected { get; set; } = -1;

        public (SafeZoneHandle Handle, SafeZone Start, double X, double Y)? Drag { get; set; }

        public bool SyncingLabel { get; set; }

        public SafeZonePreset Preset => new("", "Zonas", aspectWidth, aspectHeight, [.. Zones], center);
    }
}
