using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Services.Templates;
using Rodaje.App.Theme;
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
        SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width < 1100;
            StatsText.Visibility = e.NewSize.Width < 1500 ? Visibility.Collapsed : Visibility.Visible;
            CreditArea.Visibility = Brand.Visibility = ExportButton.Visibility = ProjectInfo.Visibility =
                narrow ? Visibility.Collapsed : Visibility.Visible;
            ProjectColumn.Width = narrow ? new GridLength(24) : new GridLength(1, GridUnitType.Star);
            MenuColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        };
    }

    /// <summary>Se pidió una pestaña del panel derecho desde el menú Ver.</summary>
    public event EventHandler<InspectorView.Tab>? InspectorTabRequested;

    public event EventHandler? MarkersPanelRequested;

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
        ToolTipService.SetToolTip(StatsText, StatsText.Text.Length == 0 ? null : StatsText.Text);
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

        // Las preferencias persistentes se editan desde Archivo > Preferencias.
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && _services is not null && !_services.Shortcuts.Invoke(id))
        {
            _services.Session.Notify(NotificationSeverity.Informational, "No disponible", "Esta acción no está disponible en este momento.");
        }
    }

    private void OnMarkersClick(object sender, RoutedEventArgs e) => MarkersPanelRequested?.Invoke(this, EventArgs.Empty);

    private void OnInspectorClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag.AsSpan("inspector:".Length), out InspectorView.Tab tab))
        {
            InspectorTabRequested?.Invoke(this, tab);
        }
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

    private void OnTemplateClick(object sender, RoutedEventArgs e) => UiTask.Run(OnTemplateClickAsync(sender));

    private async Task OnTemplateClickAsync(object sender)
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
            UiTask.Report(ex, notify: false);
            _services.Session.Notify(NotificationSeverity.Error, "No se pudo crear el proyecto", ex.Message);
        }
    }

    /// <summary>Borra una plantilla propia después de confirmarlo (no se puede deshacer).</summary>
    private void OnDeleteTemplateClick(object sender, RoutedEventArgs e) => UiTask.Run(OnDeleteTemplateClickAsync(sender));

    private async Task OnDeleteTemplateClickAsync(object sender)
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
    private void OnSaveTemplateClick(object sender, RoutedEventArgs e) => UiTask.Run(OnSaveTemplateClickAsync(sender, e));

    private async Task OnSaveTemplateClickAsync(object sender, RoutedEventArgs e)
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

    private void OnGeneratedFolderClick(object sender, RoutedEventArgs e) => UiTask.Run(OnGeneratedFolderClickAsync(sender, e));

    private async Task OnGeneratedFolderClickAsync(object sender, RoutedEventArgs e)
    {
        if (DialogGate.IsOpen || _services is not { } services || XamlRoot is null)
        {
            return;
        }

        var body = new StackPanel { Spacing = 8, MaxWidth = 460 };
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

    private void OnAboutClick(object sender, RoutedEventArgs e) => UiTask.Run(OnAboutClickAsync(sender, e));

    private async Task OnAboutClickAsync(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var link = new HyperlinkButton { Content = UpstreamAttribution.RepositoryUrl, Padding = new Thickness(0) };
        link.Click += (_, _) => OpenUpstream();
        string? version = typeof(TitleBar).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var name = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "Rodaje", Style = ThemeResources.Style("SubtitleTextStyle") });
        name.Children.Add(new TextBlock { Text = $"Editor de video para Windows · versión {version}", Style = ThemeResources.Style("LabelTextStyle"), IsTextSelectionEnabled = true });
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        header.Children.Add(new AppLogo { Detailed = true, Width = 64, Height = 64 });
        header.Children.Add(name);
        var body = new StackPanel { Spacing = 12, MaxWidth = 460 };
        body.Children.Add(header);
        body.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4), Background = ThemeResources.Brush("StrokeBrush") });
        body.Children.Add(new TextBlock { Text = UpstreamAttribution.Notice, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        body.Children.Add(link);
        body.Children.Add(new TextBlock { Text = UpstreamAttribution.Copyright, TextWrapping = TextWrapping.Wrap, Style = ThemeResources.Style("LabelTextStyle"), TextTrimming = TextTrimming.None });
        body.Children.Add(new TextBlock { Text = UpstreamAttribution.LicenseLine, TextWrapping = TextWrapping.Wrap, Style = ThemeResources.Style("CaptionTextStyle"), TextTrimming = TextTrimming.None });
        body.Children.Add(new TextBlock { Text = UpstreamAttribution.FfmpegCredit, TextWrapping = TextWrapping.Wrap, Style = ThemeResources.Style("CaptionTextStyle"), TextTrimming = TextTrimming.None });
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
