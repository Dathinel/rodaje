using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Services.Templates;
using Rodaje.App.Views.Inspector;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Shell;

/// <summary>Barra de título: los menús lanzan acciones por su identificador (Tag), así muestran la tecla vigente.</summary>
internal sealed partial class TitleBar : UserControl
{
    private EditorServices? _services;
    private ProjectTemplateStore? _templates;

    public TitleBar()
    {
        InitializeComponent();
        UpstreamCredit.Content = UpstreamAttribution.ShortCredit;
        ToolTipService.SetToolTip(UpstreamCredit, UpstreamAttribution.Notice);
        AutomationProperties.SetName(UpstreamCredit, UpstreamAttribution.Notice);
    }

    /// <summary>Se pidió una pestaña del panel derecho desde el menú Ver.</summary>
    public event EventHandler<InspectorView.Tab>? InspectorTabRequested;

    /// <summary>Se eligió Salir.</summary>
    public event EventHandler? ExitRequested;

    public EditorSession? Session { get; private set; }

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        Session = services.Session;
        Session.TransportChanged += (_, _) => UpdateStats();
        Session.HistoryChanged += (_, _) => UpdateUndoText();
        services.ShortcutRegistry.Changed += (_, _) => UpdateAccelerators();
        BuildTrackCountMenu(NewVideoTracksMenu, "Tracks", isVideo: true);
        BuildTrackCountMenu(NewAudioTracksMenu, "AudioTracks", isVideo: false);
        BuildLinkedAudioMenu();
        LoadTemplates();
        UpdateAccelerators();
        UpdateUndoText();
        UpdateChecks();
        UpdateStats();
        Bindings.Update();
    }

    private void UpdateStats()
    {
        if (_services is null)
        {
            return;
        }

        string gpu = _services.Player.DeviceDescription;
        long dropped = _services.Session.Transport.FramesDropped;
        string frames = dropped == 0 ? "sin cuadros perdidos" : string.Create(CultureInfo.InvariantCulture, $"{dropped} cuadros perdidos");
        StatsText.Text = gpu.Length == 0 ? "" : $"{gpu} · {frames}";
    }

    private void UpdateUndoText()
    {
        if (Session is null)
        {
            return;
        }

        UndoItem.Text = Session.UndoDescription is { } undo ? $"Deshacer «{undo}»" : "Deshacer";
        RedoItem.Text = Session.RedoDescription is { } redo ? $"Rehacer «{redo}»" : "Rehacer";
        UndoItem.IsEnabled = Session.CanUndo;
        RedoItem.IsEnabled = Session.CanRedo;
    }

    private void UpdateAccelerators()
    {
        if (_services is null)
        {
            return;
        }

        foreach (MenuBarItem top in Menu.Items)
        {
            foreach (MenuFlyoutItemBase item in top.Items)
            {
                if (item is MenuFlyoutItem { Tag: string id } entry && !id.Contains(':', StringComparison.Ordinal) && _services.ShortcutRegistry.Find(id) is not null)
                {
                    entry.KeyboardAcceleratorTextOverride = _services.ShortcutRegistry.GetKeyText(id);
                }
            }
        }

        UpdateChecks();
    }

    private void UpdateChecks()
    {
        if (_services is null)
        {
            return;
        }

        ShortcutPreset preset = _services.ShortcutRegistry.Preset;
        PresetRodaje.IsChecked = preset == ShortcutPreset.Rodaje;
        PresetAgile.IsChecked = preset == ShortcutPreset.Agile;
        PresetClassic.IsChecked = preset == ShortcutPreset.Classic;
        ProxyUsage proxies = _services.Settings.Current.ProxyUsage;
        ProxyAuto.IsChecked = proxies == ProxyUsage.Auto;
        ProxyAlways.IsChecked = proxies == ProxyUsage.Always;
        ProxyNever.IsChecked = proxies == ProxyUsage.Never;
        UpdateTrackCountChecks(NewVideoTracksMenu, _services.Settings.Current.NewProjectTracks.Video);
        UpdateTrackCountChecks(NewAudioTracksMenu, _services.Settings.Current.NewProjectTracks.Audio);
        UpdateLinkedAudioChecks();
        PreviewQuality quality = _services.Settings.Current.PreviewQuality;
        QualityAuto.IsChecked = quality == PreviewQuality.Auto;
        QualityFull.IsChecked = quality == PreviewQuality.Full;
        QualityHalf.IsChecked = quality == PreviewQuality.Half;
        QualityQuarter.IsChecked = quality == PreviewQuality.Quarter;
        UpdateWorkflowChecks();
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && _services is not null && !_services.Shortcuts.Invoke(id))
        {
            _services.Session.Notify(NotificationSeverity.Informational, "No disponible", "Esta acción no está disponible en este momento.");
        }
    }

    private void OnInspectorClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag.AsSpan("inspector:".Length), out InspectorView.Tab tab))
        {
            InspectorTabRequested?.Invoke(this, tab);
        }
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && _services is not null && Enum.TryParse(tag, out ShortcutPreset preset))
        {
            _services.ShortcutRegistry.Preset = preset;
            _services.Settings.Current.ShortcutPreset = preset;
        }
    }

    private void OnProxyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && _services is not null && Enum.TryParse(tag, out ProxyUsage usage))
        {
            _services.Settings.Current.ProxyUsage = usage;
            _services.Player.SetProxyUsage(usage);
        }
    }

    private void BuildTrackCountMenu(MenuFlyoutSubItem menu, string group, bool isVideo)
    {
        menu.Items.Clear();
        for (int count = NewProjectTracks.Min; count <= NewProjectTracks.Max; count++)
        {
            var item = new RadioMenuFlyoutItem { Text = count.ToString(Rodaje.App.Services.Format.SpanishNumbers.Format), GroupName = group, Tag = count };
            item.Click += isVideo ? OnVideoTracksClick : OnAudioTracksClick;
            menu.Items.Add(item);
        }
    }

    private static void UpdateTrackCountChecks(MenuFlyoutSubItem menu, int selected)
    {
        foreach (MenuFlyoutItemBase item in menu.Items)
        {
            if (item is RadioMenuFlyoutItem { Tag: int count } radio)
            {
                radio.IsChecked = count == selected;
            }
        }
    }

    private void OnVideoTracksClick(object sender, RoutedEventArgs e) => SetNewProjectTracks(sender, video: true);

    private void OnAudioTracksClick(object sender, RoutedEventArgs e) => SetNewProjectTracks(sender, video: false);

    private void SetNewProjectTracks(object sender, bool video)
    {
        if (sender is not FrameworkElement { Tag: int count } || _services is null)
        {
            return;
        }

        AppSettings settings = _services.Settings.Current;
        if (video)
        {
            settings.DefaultVideoTracks = count;
        }
        else
        {
            settings.DefaultAudioTracks = count;
        }

        settings.Normalize();
        _services.Session.NewProjectTracks = settings.NewProjectTracks;
        _services.Settings.SaveIfChanged();
    }

    /// <summary>«Todas» y una casilla por pista de video (1 a 8): de cuáles sale el audio al importar o soltar un medio.</summary>
    private void BuildLinkedAudioMenu()
    {
        LinkedAudioMenu.Items.Clear();
        var all = new ToggleMenuFlyoutItem { Text = "Todas", Tag = "all" };
        all.Click += OnLinkedAudioClick;
        LinkedAudioMenu.Items.Add(all);
        LinkedAudioMenu.Items.Add(new MenuFlyoutSeparator());
        for (int number = 1; number <= NewProjectTracks.Max; number++)
        {
            var item = new ToggleMenuFlyoutItem { Text = string.Create(Rodaje.App.Services.Format.SpanishNumbers.Format, $"Video {number}"), Tag = number };
            item.Click += OnLinkedAudioClick;
            LinkedAudioMenu.Items.Add(item);
        }
    }

    private void UpdateLinkedAudioChecks()
    {
        if (_services is null)
        {
            return;
        }

        LinkedAudioTracks rule = _services.Settings.Current.LinkedAudioTracks;
        foreach (MenuFlyoutItemBase item in LinkedAudioMenu.Items)
        {
            if (item is ToggleMenuFlyoutItem toggle)
            {
                toggle.IsChecked = toggle.Tag is int number ? rule.AllowsNumber(number) : rule.IsAll;
            }
        }
    }

    private void OnLinkedAudioClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || _services is null)
        {
            return;
        }

        AppSettings settings = _services.Settings.Current;
        int knownTracks = Math.Max(NewProjectTracks.Max, _services.Session.Sequence.VideoTracks.Count);
        LinkedAudioTracks next = element.Tag is int number ? settings.LinkedAudioTracks.Toggle(number, knownTracks) : LinkedAudioTracks.All;
        settings.LinkedAudioVideoTracks = next.ToSetting();
        settings.Normalize();
        _services.Session.LinkedAudioTracks = settings.LinkedAudioTracks;
        _services.Settings.SaveIfChanged();
        UpdateLinkedAudioChecks();
    }

    private void LoadTemplates()
    {
        _templates = new ProjectTemplateStore(ProjectTemplateStore.DefaultPath);
        if (_templates.Load() is { } warning)
        {
            _services?.Session.Notify(NotificationSeverity.Warning, "Plantillas", warning);
        }

        BuildTemplatesMenu();
    }

    /// <summary>Las de fábrica, las propias y, si hay propias, «Borrar plantilla propia».</summary>
    private void BuildTemplatesMenu()
    {
        TemplatesMenu.Items.Clear();
        if (_templates is null)
        {
            return;
        }

        foreach (ProjectTemplate template in BuiltInTemplates.All)
        {
            TemplatesMenu.Items.Add(TemplateItem(template, OnTemplateClick));
        }

        if (_templates.UserTemplates.Count == 0)
        {
            return;
        }

        TemplatesMenu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutSubItem { Text = "Borrar plantilla propia" };
        foreach (ProjectTemplate template in _templates.UserTemplates)
        {
            TemplatesMenu.Items.Add(TemplateItem(template, OnTemplateClick));
            delete.Items.Add(TemplateItem(template, OnDeleteTemplateClick));
        }

        TemplatesMenu.Items.Add(new MenuFlyoutSeparator());
        TemplatesMenu.Items.Add(delete);
    }

    private static MenuFlyoutItem TemplateItem(ProjectTemplate template, RoutedEventHandler click)
    {
        var item = new MenuFlyoutItem { Text = template.Name, Tag = template.Id };
        ToolTipService.SetToolTip(item, template.Summary);
        item.Click += click;
        return item;
    }

    private async void OnTemplateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || _services is null || _templates?.Find(id) is not { } template)
        {
            return;
        }

        try
        {
            await _services.Session.NewProjectFromTemplateAsync(template);
        }
#pragma warning disable CA1031 // Un clic en el menú no debe tumbar la aplicación: el error se muestra en la ventana.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _services.Session.Notify(NotificationSeverity.Error, "No se pudo crear el proyecto", ex.Message);
        }
    }

    /// <summary>Borra una plantilla propia después de confirmarlo (no se puede deshacer).</summary>
    private async void OnDeleteTemplateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || _services is null || _templates is null)
        {
            return;
        }

        try
        {
            if (!await _services.Session.DeleteUserTemplateAsync(_templates, id))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Session.Notify(NotificationSeverity.Error, "No se pudo borrar la plantilla", ex.Message);
        }

        BuildTemplatesMenu();
    }

    /// <summary>Pide un nombre y guarda el formato y las pistas de la secuencia abierta como plantilla propia.</summary>
    private async void OnSaveTemplateClick(object sender, RoutedEventArgs e)
    {
        if (DialogGate.IsOpen || _services is not { } services || _templates is not { } templates || XamlRoot is null)
        {
            return;
        }

        var nameBox = new TextBox { PlaceholderText = "Por ejemplo: Mi canal vertical", MaxLength = ProjectTemplate.MaxNameLength };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "Guarda el tamaño, la tasa de cuadros y las pistas con sus nombres (sin clips) para empezar otros proyectos igual. Si ya hay una con ese nombre, se reemplaza.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(nameBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Guardar como plantilla",
            Content = content,
            PrimaryButtonText = "Guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };

        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            return;
        }

        try
        {
            ProjectTemplate saved = templates.Save(services.Session.CreateTemplateFromSequence(nameBox.Text));
            services.Session.Notify(NotificationSeverity.Success, "Plantilla guardada", $"«{saved.Name}» aparece en Archivo › Proyecto nuevo desde plantilla ({saved.Summary}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            services.Session.Notify(NotificationSeverity.Error, "No se pudo guardar la plantilla", ex.Message);
        }

        BuildTemplatesMenu();
    }

    private void OnQualityClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && _services is not null && Enum.TryParse(tag, out PreviewQuality quality))
        {
            _services.SetPreviewQuality(quality);
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e) => _services?.Shortcuts.Invoke(ActionIds.Export);

    private void OnExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void OnUpstreamClick(object sender, RoutedEventArgs e) => OpenUpstream();

    private void OpenUpstream()
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(UpstreamAttribution.RepositoryUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _services?.Session.Notify(NotificationSeverity.Warning, "Repositorio oficial", "No se pudo abrir el navegador: " + UpstreamAttribution.RepositoryUrl);
        }
    }

    private async void OnGeneratedFolderClick(object sender, RoutedEventArgs e)
    {
        if (DialogGate.IsOpen || _services is not { } services || XamlRoot is null)
        {
            return;
        }

        var body = new StackPanel { Spacing = 10, MaxWidth = 460 };
        body.Children.Add(new TextBlock
        {
            Text = "Dónde guarda Rodaje lo que crea: audio sin ruido, voz, copias ProRes de CinemaDNG y máscaras. Lo ya creado no se mueve.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new Views.Shared.GeneratedFolderRow(services).Element);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Carpeta de medios generados",
            Content = body,
            CloseButtonText = "Listo",
            RequestedTheme = ElementTheme.Dark,
        };
        await DialogGate.ShowAsync(dialog);
    }

    private async void OnAboutClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var link = new HyperlinkButton { Content = UpstreamAttribution.RepositoryUrl, Padding = new Thickness(0) };
        link.Click += (_, _) => OpenUpstream();
        var body = new StackPanel { Spacing = 10, MaxWidth = 460 };
        string? version = typeof(TitleBar).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        body.Children.Add(new TextBlock { Text = $"Versión {version}", IsTextSelectionEnabled = true });
        body.Children.Add(new TextBlock { Text = UpstreamAttribution.Notice, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        body.Children.Add(link);
        body.Children.Add(new TextBlock { Text = UpstreamAttribution.Copyright, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = UpstreamAttribution.LicenseLine, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextStyle"] });
        body.Children.Add(new TextBlock { Text = UpstreamAttribution.FfmpegCredit, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextStyle"] });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Acerca de Rodaje",
            Content = body,
            CloseButtonText = "Cerrar",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        await DialogGate.ShowAsync(dialog);
    }
}
