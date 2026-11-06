using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Views.Preferences;

internal sealed partial class PreferencesView : UserControl
{
    private static readonly (string Name, string Spec) ProfileFilter = ("Perfil de Rodaje", "*" + PersonalProfile.Extension);
    private static readonly (string Name, string Spec) ThemeFilter = ("Tema de Rodaje", "*" + Themes.Extension);

    private PreferencesViewModel? _model;
    private ShortcutRegistry? _registry;
    private PersonalizationDraft? _draft;
    private WorkspaceProfile? _currentWorkspace;
    private uint? _accent;
    private string? _pendingWorkspaceName;
    private bool _loading;

    public PreferencesView()
    {
        InitializeComponent();
        ProxyUsageBox.Items.Add("Automático");
        ProxyUsageBox.Items.Add("Siempre que haya");
        ProxyUsageBox.Items.Add("Nunca (originales)");
        PreviewQualityBox.Items.Add("Automática");
        PreviewQualityBox.Items.Add("Completa");
        PreviewQualityBox.Items.Add("Mitad");
        PreviewQualityBox.Items.Add("Cuarto");
        TenBitViewerBox.Items.Add("Automático (si el monitor es de 10 bits)");
        TenBitViewerBox.Items.Add("Siempre");
        TenBitViewerBox.Items.Add("Nunca (8 bits)");
        HdrViewerBox.Items.Add("Automático (si el monitor de la ventana está en HDR)");
        HdrViewerBox.Items.Add("Siempre (salvo en un monitor SDR)");
        HdrViewerBox.Items.Add("Nunca (SDR)");
        ShortcutPresetBox.Items.Add("Estilo Rodaje");
        ShortcutPresetBox.Items.Add("Estilo Ágil");
        ShortcutPresetBox.Items.Add("Estilo Clásico");
        foreach (int scale in Personalization.Scales)
        {
            ScaleBox.Items.Add($"{scale} %");
        }

        TimelineFontBox.Items.Add("Pequeño");
        TimelineFontBox.Items.Add("Normal");
        TimelineFontBox.Items.Add("Grande");
        foreach (AspectChoice aspect in ProjectFormats.Aspects.Where(a => !a.Custom))
        {
            AspectBox.Items.Add($"{aspect.Label} · {aspect.Use}");
        }

        foreach ((_, string label) in ProjectFormats.Resolutions)
        {
            ResolutionBox.Items.Add(label);
        }

        foreach ((_, string label) in ProjectFormats.FrameRates)
        {
            FrameRateBox.Items.Add(label);
        }
    }

    /// <summary>El espacio de trabajo elegido en la pestaña, para que la ventana lo aplique al guardar.</summary>
    public string? WorkspaceToApply { get; private set; }

    /// <summary>Se confirmó un perfil importado o «Restablecer todo»: la ventana debe guardar los atajos y releer los espacios.</summary>
    public bool ProfileImported { get; private set; }

    public void Attach(AppSettings settings, ShortcutRegistry registry, WorkspaceProfile currentWorkspace)
    {
        _model = new PreferencesViewModel(settings);
        _registry = registry;
        _draft = new PersonalizationDraft(settings, registry);
        _currentWorkspace = currentWorkspace;
        _loading = true;
        VideoTracksBox.Value = _model.VideoTracks;
        AudioTracksBox.Value = _model.AudioTracks;
        LinkedAudioTracksPanel.Children.Clear();
        foreach (int number in Enumerable.Range(1, NewProjectTracks.Max))
        {
            LinkedAudioTracksPanel.Children.Add(new CheckBox
            {
                Content = $"Video {number}",
                Tag = number,
                IsChecked = _model.LinkedAudioVideoTracks.Contains(number),
            });
        }
        AutoProxiesBox.IsChecked = _model.GenerateProxies;
        GeneratedFolderBox.Text = _model.GeneratedMediaFolder;
        StillImageDurationBox.Value = _model.StillImageDurationSeconds;
        StillCaptureFolderBox.Text = _model.StillCaptureFolder;
        AutosaveMinutesBox.Value = _model.AutosaveMinutes;
        CheckUpdatesBox.IsChecked = _model.CheckForUpdates;
        ProxyUsageBox.SelectedIndex = (int)_model.ProxyUsage;
        PreviewQualityBox.SelectedIndex = (int)_model.PreviewQuality;
        TenBitViewerBox.SelectedIndex = (int)_model.TenBitViewer;
        HdrViewerBox.SelectedIndex = (int)_model.HdrViewer;
        ShortcutPresetBox.SelectedIndex = (int)_model.ShortcutPreset;
        LoadPersonalization(settings);
        _loading = false;
    }

    public void Apply()
    {
        if (_model is not { } model || _draft is not { } draft || _registry is not { } registry) return;
        bool profileChanges = draft.HasProfileChanges;
        draft.Commit(model.Settings, registry);
        ProfileImported = profileChanges;
        model.StillImageDurationSeconds = double.IsFinite(StillImageDurationBox.Value) ? StillImageDurationBox.Value : Rodaje.Core.Model.MediaRef.DefaultStillImageDurationSeconds;
        model.StillCaptureFolder = StillCaptureFolderBox.Text;
        model.AutosaveMinutes = double.IsFinite(AutosaveMinutesBox.Value) ? (int)Math.Round(AutosaveMinutesBox.Value) : Rodaje.Core.Serialization.AutosaveSchedule.DefaultIntervalMinutes;
        model.CheckForUpdates = CheckUpdatesBox.IsChecked == true;
        model.GenerateProxies = AutoProxiesBox.IsChecked == true;
        model.GeneratedMediaFolder = GeneratedFolderBox.Text;
        model.ProxyUsage = (ProxyUsage)Math.Max(0, ProxyUsageBox.SelectedIndex);
        model.PreviewQuality = (PreviewQuality)Math.Max(0, PreviewQualityBox.SelectedIndex);
        model.TenBitViewer = (TenBitViewer)Math.Max(0, TenBitViewerBox.SelectedIndex);
        model.HdrViewer = (HdrViewer)Math.Max(0, HdrViewerBox.SelectedIndex);
        model.SetLinkedAudioVideoTracks(LinkedAudioTracksPanel.Children.OfType<CheckBox>()
            .Where(checkBox => checkBox.IsChecked == true)
            .Select(checkBox => (int)checkBox.Tag));

        AppSettings s = model.Settings;
        WritePersonalization(s);
        if (_pendingWorkspaceName is { } name && _currentWorkspace is { } current)
        {
            WorkspaceToApply = WorkspaceProfiles.SaveCurrent(s, name, current).Id;
        }
        else if (WorkspaceBox.SelectedItem is WorkspaceChoice chosen && (chosen.Profile.Id != s.Workspace || profileChanges))
        {
            WorkspaceToApply = chosen.Profile.Id;
        }

        draft.CommitThemes(ThemeResources.Store);
        s.Normalize();
        ThemeResources.TimelineFontScale = Personalization.FontFactor(s.TimelineFontSize);
        ThemeResources.ApplyCurrent(s);
    }

    /// <summary>Lo de la pestaña Personalización (y el estilo de teclado, que viaja en el perfil) a <paramref name="s"/>.</summary>
    private void WritePersonalization(AppSettings s)
    {
        s.DefaultVideoTracks = double.IsFinite(VideoTracksBox.Value) ? (int)VideoTracksBox.Value : NewProjectTracks.Default.Video;
        s.DefaultAudioTracks = double.IsFinite(AudioTracksBox.Value) ? (int)AudioTracksBox.Value : NewProjectTracks.Default.Audio;
        s.ShortcutPreset = (ShortcutPreset)Math.Max(0, ShortcutPresetBox.SelectedIndex);
        s.Theme = ThemeBox.SelectedItem is ThemeChoice choice ? choice.Setting : Themes.GraphiteName;
        s.AccentColor = _accent is { } accent ? Personalization.ToHex(accent) : null;
        s.UiScale = Personalization.Scales[Math.Max(0, ScaleBox.SelectedIndex)];
        s.TimelineFontSize = (TimelineFontSize)Math.Max(0, TimelineFontBox.SelectedIndex);
        s.DefaultProjectAspect = ProjectFormats.Aspects[Math.Max(0, AspectBox.SelectedIndex)].Label;
        s.DefaultProjectShortSide = ProjectFormats.Resolutions[Math.Max(0, ResolutionBox.SelectedIndex)].ShortSide;
        s.DefaultProjectFrameRate = ProjectFormats.FrameRates[Math.Max(0, FrameRateBox.SelectedIndex)].Rate;
        s.DefaultProjectFolder = ProjectFolderBox.Text;
    }

    /// <summary>Tras importar un perfil o «Restablecer todo»: los controles muestran la copia pendiente y la app la previsualiza.</summary>
    private void ShowDraft(AppSettings s)
    {
        _loading = true;
        VideoTracksBox.Value = s.DefaultVideoTracks;
        AudioTracksBox.Value = s.DefaultAudioTracks;
        ShortcutPresetBox.SelectedIndex = (int)s.ShortcutPreset;
        _pendingWorkspaceName = null;
        SaveWorkspaceButton.Content = "Guardar la actual";
        LoadPersonalization(s);
        _loading = false;
        Preview(PreviewTheme());
    }

    /// <summary>Para la autoprueba: abre una pestaña y previsualiza un tema por su nombre.</summary>
    internal void ShowForSelfTest(int tab, string? themeName)
    {
        Tabs.SelectedIndex = tab;
        if (themeName is not null)
        {
            ThemeBox.SelectedItem = ThemeBox.Items.Cast<ThemeChoice>().First(c => c.Setting == themeName);
        }
    }

    /// <summary>Cancelar: fuera la vista previa, los temas editados, borrados o renombrados y lo importado o restablecido.</summary>
    public void Discard()
    {
        if (_model is not { } model) return;
        ThemeResources.Store.Load();
        ThemeResources.ApplyCurrent(model.Settings);
    }

    private sealed record ThemeChoice(string Setting, string Label)
    {
        public override string ToString() => Label;
    }

    // El texto del desplegable sale de ToString: con Native AOT el enlace por reflexión no ve las propiedades.
    private sealed record WorkspaceChoice(WorkspaceProfile Profile)
    {
        public override string ToString() => Profile.Name;
    }

    private void LoadPersonalization(AppSettings s)
    {
        _accent = s.Accent;
        FillThemeBoxes(s.Theme);
        AccentPanel.Children.Clear();
        foreach ((string name, uint argb) in Personalization.AccentPalette)
        {
            uint color = argb;
            var swatch = new Button
            {
                Width = 28, Height = 28, Padding = new Thickness(0), Background = new SolidColorBrush(ThemeResources.ToColor(color)),
                BorderBrush = ThemeResources.Brush("ControlBorderBrush"), BorderThickness = new Thickness(1),
            };
            ToolTipService.SetToolTip(swatch, name);
            swatch.Click += (_, _) => SetAccent(color);
            AccentPanel.Children.Add(swatch);
        }

        var custom = new Button { Content = "Personalizado…" };
        var picker = new ColorPicker { IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorPreviewVisible = false, Color = ThemeResources.ToColor(_accent ?? ThemeResources.Current.Color("AccentBrush")) };
        picker.ColorChanged += (_, e) => SetAccent(ThemeResources.ToArgb(e.NewColor));
        custom.Flyout = new Flyout { Content = picker };
        AccentPanel.Children.Add(custom);
        var fromTheme = new Button { Content = "Del tema" };
        fromTheme.Click += (_, _) => SetAccent(null);
        AccentPanel.Children.Add(fromTheme);
        UpdateAccentNote();

        ScaleBox.SelectedIndex = Math.Max(0, Personalization.Scales.ToList().IndexOf(s.UiScale));
        TimelineFontBox.SelectedIndex = (int)s.TimelineFontSize;
        FillWorkspaces(s);
        AspectBox.SelectedIndex = Math.Max(0, ProjectFormats.Aspects.ToList().FindIndex(a => a.Label == s.DefaultProjectAspect));
        ResolutionBox.SelectedIndex = Math.Max(0, ProjectFormats.Resolutions.ToList().FindIndex(r => r.ShortSide == s.DefaultProjectShortSide));
        FrameRateBox.SelectedIndex = Math.Max(0, ProjectFormats.FrameRates.ToList().FindIndex(r => r.Rate == s.DefaultProjectFrameRate));
        ProjectFolderBox.Text = s.DefaultProjectFolder ?? "";
    }

    private void FillThemeBoxes(string? selected)
    {
        bool loading = _loading;
        _loading = true;
        ThemeBox.Items.Clear();
        EditThemeBox.Items.Clear();
        foreach (ThemeDefinition theme in Themes.BuiltIn.Concat(ThemeResources.Store.Own))
        {
            ThemeBox.Items.Add(new ThemeChoice(theme.Name, theme.Name + (Themes.IsBuiltIn(theme.Name) ? "" : " (propio)")));
            EditThemeBox.Items.Add(theme.Name);
        }

        ThemeBox.Items.Add(new ThemeChoice(Personalization.FollowWindowsTheme, "Seguir a Windows"));
        ThemeBox.SelectedIndex = Math.Max(0, ThemeBox.Items.Cast<ThemeChoice>().ToList().FindIndex(c => string.Equals(c.Setting, selected, StringComparison.OrdinalIgnoreCase)));
        string edit = selected is null || selected == Personalization.FollowWindowsTheme ? Themes.GraphiteName : selected;
        EditThemeBox.SelectedIndex = Math.Max(0, EditThemeBox.Items.Cast<string>().ToList().FindIndex(n => string.Equals(n, edit, StringComparison.OrdinalIgnoreCase)));
        _loading = loading;
        ShowThemeEditor();
    }

    private void FillWorkspaces(AppSettings s)
    {
        WorkspaceBox.Items.Clear();
        foreach (WorkspaceProfile profile in WorkspaceProfiles.All(s))
        {
            WorkspaceBox.Items.Add(new WorkspaceChoice(profile));
        }

        WorkspaceBox.SelectedIndex = Math.Max(0, WorkspaceBox.Items.Cast<WorkspaceChoice>().ToList().FindIndex(c => c.Profile.Id == s.Workspace));
    }

    private ThemeDefinition PreviewTheme() => ThemeBox.SelectedItem is ThemeChoice choice
        ? Themes.Resolve(choice.Setting, ThemeResources.Store.Own, choice.Setting == Personalization.FollowWindowsTheme && ThemeResources.WindowsIsLight(), out _)
        : Themes.Graphite;

    private void Preview(ThemeDefinition theme) => ThemeResources.ApplyTheme(theme, _accent);

    private void SetAccent(uint? accent)
    {
        _accent = accent;
        UpdateAccentNote();
        Preview(PreviewTheme());
    }

    private void UpdateAccentNote()
    {
        if (_accent is { } accent && Themes.FitAccent(PreviewTheme(), accent) is (uint fitted, true))
        {
            AccentNote.Text = $"El acento no llega a 3:1 sobre los paneles; se usa {Personalization.ToHex(fitted)}.";
            AccentNote.Visibility = Visibility.Visible;
        }
        else
        {
            AccentNote.Visibility = Visibility.Collapsed;
        }
    }

    // Los dos desplegables de tema van juntos: el que se ve (y se edita) en Temas es el que queda al pulsar Guardar.
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ThemeBox.SelectedItem is ThemeChoice choice && choice.Setting != Personalization.FollowWindowsTheme
            && EditThemeBox.Items.Cast<string>().FirstOrDefault(n => string.Equals(n, choice.Setting, StringComparison.OrdinalIgnoreCase)) is { } name
            && !Equals(EditThemeBox.SelectedItem, name))
        {
            _loading = true;
            EditThemeBox.SelectedItem = name;
            _loading = false;
            ShowThemeEditor();
        }

        UpdateAccentNote();
        Preview(PreviewTheme());
    }

    private void OnSaveWorkspaceClick(object sender, RoutedEventArgs e)
    {
        string name = WorkspaceNameBox.Text.Trim();
        if (name.Length == 0) return;
        _pendingWorkspaceName = name;
        SaveWorkspaceButton.Content = $"Se guardará «{name}»";
    }

    private void OnResetWorkspaceClick(object sender, RoutedEventArgs e)
    {
        _pendingWorkspaceName = null;
        SaveWorkspaceButton.Content = "Guardar la actual";
        WorkspaceBox.SelectedItem = WorkspaceBox.Items.OfType<WorkspaceChoice>().First(c => c.Profile.Id == WorkspaceProfiles.EditingId);
    }

    // Temas

    private ThemeDefinition? EditedTheme => EditThemeBox.SelectedItem is string name
        ? Themes.BuiltIn.FirstOrDefault(t => t.Name == name) ?? ThemeResources.Store.Find(name)
        : null;

    private void OnEditThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ShowThemeEditor();
        if (EditThemeBox.SelectedItem is string name
            && ThemeBox.Items.Cast<ThemeChoice>().FirstOrDefault(c => string.Equals(c.Setting, name, StringComparison.OrdinalIgnoreCase)) is { } choice)
        {
            _loading = true;
            ThemeBox.SelectedItem = choice;
            _loading = false;
        }

        UpdateAccentNote();
        Preview(PreviewTheme());
    }

    private void ShowThemeEditor()
    {
        ThemeColorsPanel.Children.Clear();
        if (EditedTheme is not { } theme)
        {
            return;
        }

        bool builtIn = Themes.IsBuiltIn(theme.Name);
        RenameThemeButton.IsEnabled = DeleteThemeButton.IsEnabled = ResetThemeButton.IsEnabled = !builtIn;
        ThemeHint.Text = builtIn
            ? "Es el tema de la app. Los de serie no se editan: pulsa Duplicar para hacer uno propio a partir de este."
            : "Es el tema de la app; cada cambio se ve al momento y se guarda al pulsar Guardar.";
        DeleteThemeText.Text = $"¿Eliminar el tema «{theme.Name}»? El archivo se borra al pulsar Guardar.";
        RenameBox.Text = theme.Name;
        string? group = null;
        foreach ((string key, string groupName, string label) in Themes.Slots)
        {
            if (group != groupName)
            {
                group = groupName;
                ThemeColorsPanel.Children.Add(new TextBlock { Text = groupName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            }

            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            uint color = theme.Color(key);
            // El color va en un Border dentro del botón: desactivado (tema de serie) el fondo del botón se vuelve transparente.
            var chip = new Border { Width = 36, Height = 16, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(ThemeResources.ToColor(color)) };
            var swatch = new Button { Padding = new Thickness(3), IsEnabled = !builtIn, Content = chip };
            var hex = new TextBlock { Text = Personalization.ToHex(color), VerticalAlignment = VerticalAlignment.Center, Style = ThemeResources.Style("CaptionTextStyle") };
            var picker = new ColorPicker { IsAlphaEnabled = Personalization.Alpha(color) != 0xFF, IsMoreButtonVisible = false, IsColorPreviewVisible = false, Color = ThemeResources.ToColor(color) };
            string slot = key;
            picker.ColorChanged += (_, e) =>
            {
                uint argb = ThemeResources.ToArgb(e.NewColor);
                theme.Set(slot, argb);
                chip.Background = new SolidColorBrush(e.NewColor);
                hex.Text = Personalization.ToHex(argb);
                Preview(PreviewTheme());
                UpdateContrastNote(theme);
            };
            swatch.Flyout = new Flyout { Content = picker };
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(swatch, 1);
            Grid.SetColumn(hex, 2);
            row.Children.Add(text);
            row.Children.Add(swatch);
            row.Children.Add(hex);
            ThemeColorsPanel.Children.Add(row);
        }

        UpdateContrastNote(theme);
    }

    private void UpdateContrastNote(ThemeDefinition theme)
    {
        IReadOnlyList<string> warnings = Themes.ContrastWarnings(theme);
        ContrastNote.Text = warnings.Count == 0 ? "Contraste AA: todas las parejas de texto y fondo cumplen." : "Contraste insuficiente (AA): " + string.Join("; ", warnings) + ".";
    }

    private void OnDuplicateThemeClick(object sender, RoutedEventArgs e)
    {
        if (EditedTheme is not { } source) return;
        ThemeDefinition copy = source.Copy(ThemeResources.Store.FreeName(source.Name));
        ThemeResources.Store.Own.Add(copy);
        FillThemeBoxes(copy.Name);
        Preview(PreviewTheme());
    }

    private void OnRenameThemeClick(object sender, RoutedEventArgs e)
    {
        if (EditedTheme is not { } theme || Themes.IsBuiltIn(theme.Name) || Themes.CleanName(RenameBox.Text) is not { } name || name == theme.Name) return;
        if (Themes.IsBuiltIn(name) || ThemeResources.Store.Find(name) is not null)
        {
            RenameBox.Header = "Ya hay un tema con ese nombre";
            return;
        }

        _draft?.RenameTheme(theme, name);
        RenameThemeButton.Flyout.Hide();
        FillThemeBoxes(name);
        Preview(PreviewTheme());
    }

    private void OnDeleteThemeClick(object sender, RoutedEventArgs e)
    {
        if (EditedTheme is not { } theme || Themes.IsBuiltIn(theme.Name)) return;
        _draft?.DeleteTheme(ThemeResources.Store, theme.Name);
        DeleteThemeButton.Flyout.Hide();
        string? selected = ThemeBox.SelectedItem is ThemeChoice c && !string.Equals(c.Setting, theme.Name, StringComparison.OrdinalIgnoreCase) ? c.Setting : Themes.GraphiteName;
        FillThemeBoxes(selected);
        Preview(PreviewTheme());
    }

    private void OnResetThemeClick(object sender, RoutedEventArgs e)
    {
        if (EditedTheme is not { } theme || Themes.IsBuiltIn(theme.Name)) return;
        theme.Colors = new Dictionary<string, string>(Themes.Graphite.Colors, StringComparer.Ordinal);
        ShowThemeEditor();
        Preview(PreviewTheme());
    }

    private void OnExportThemeClick(object sender, RoutedEventArgs e)
    {
        if (EditedTheme is not { } theme || _model is not { } model) return;
        try
        {
            if (FileDialogs.Save(Owner(), "Exportar tema", Themes.FileName(theme.Name), Themes.Extension.TrimStart('.'),
                FileDialogFolderPolicy.SaveFolder(model.Settings, SaveDialogKind.Shortcuts), ThemeFilter) is { } path)
            {
                AppDataPaths.WriteAllTextAtomic(path, Themes.Serialize(theme));
                ThemeHint.Text = $"Tema exportado a {path}.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ThemeHint.Text = $"No se pudo exportar: {ex.Message}";
        }
    }

    private void OnImportThemeClick(object sender, RoutedEventArgs e)
    {
        if (_model is not { } model) return;
        try
        {
            IReadOnlyList<string> picked = FileDialogs.Open(Owner(), "Importar tema", multiple: false, FileDialogFolderPolicy.OpenFolder(model.Settings), ThemeFilter, FileDialogs.AllFilter);
            if (picked.Count == 0) return;
            if (Themes.Parse(Rodaje.Packages.SafeText.ReadAllTextStrict(picked[0])) is not { } theme)
            {
                ThemeHint.Text = $"{Path.GetFileName(picked[0])} no es un tema de Rodaje.";
                return;
            }

            theme.Name = ThemeResources.Store.FreeName(theme.Name);
            ThemeResources.Store.Own.Add(theme);
            FillThemeBoxes(theme.Name);
            Preview(PreviewTheme());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ThemeHint.Text = $"No se pudo importar: {ex.Message}";
        }
    }

    // Perfil

    private void OnExportProfileClick(object sender, RoutedEventArgs e)
    {
        if (_model is not { } model || _draft is not { } draft) return;
        // Se exporta lo que muestran los controles sobre una copia: los ajustes vivos no cambian hasta Guardar.
        AppSettings profile = draft.Settings.Clone();
        WritePersonalization(profile);
        if (_pendingWorkspaceName is { } name && _currentWorkspace is { } current)
        {
            WorkspaceProfiles.SaveCurrent(profile, name, current);
        }
        else if (WorkspaceBox.SelectedItem is WorkspaceChoice chosen)
        {
            profile.Workspace = chosen.Profile.Id;
        }

        profile.Normalize();
        try
        {
            if (FileDialogs.Save(Owner(), "Exportar perfil", "rodaje" + PersonalProfile.Extension, PersonalProfile.Extension.TrimStart('.'),
                FileDialogFolderPolicy.SaveFolder(model.Settings, SaveDialogKind.Shortcuts), ProfileFilter) is { } path)
            {
                PersonalProfile.Export(profile, draft.Registry, ThemeResources.Store.Find(profile.Theme), path);
                Note(ProfileNote, $"Perfil exportado a {path}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Note(ProfileNote, $"No se pudo exportar: {ex.Message}");
        }
    }

    private void OnImportProfileClick(object sender, RoutedEventArgs e)
    {
        if (_model is not { } model || _draft is not { } draft) return;
        try
        {
            IReadOnlyList<string> picked = FileDialogs.Open(Owner(), "Importar perfil", multiple: false, FileDialogFolderPolicy.OpenFolder(model.Settings), ProfileFilter, FileDialogs.AllFilter);
            if (picked.Count == 0) return;
            if (draft.Import(picked[0], ThemeResources.Store) is { } error)
            {
                Note(ProfileNote, error);
                return;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Note(ProfileNote, $"No se pudo abrir el diálogo: {ex.Message}");
            return;
        }

        ShowDraft(draft.Settings);
        Note(ProfileNote, "Perfil importado; se aplica al pulsar Guardar (Cancelar lo descarta).");
    }

    private void OnResetAllClick(object sender, RoutedEventArgs e)
    {
        if (_draft is not { } draft) return;
        draft.ResetAll();
        ShowDraft(draft.Settings);
        Note(ProfileNote, "Todo vuelve a fábrica al pulsar Guardar (Cancelar lo descarta).");
    }

    private static void Note(TextBlock block, string text)
    {
        block.Text = text;
        block.Visibility = Visibility.Visible;
    }

    private nint Owner() =>
        XamlRoot?.ContentIslandEnvironment is { } environment ? Microsoft.UI.Win32Interop.GetWindowFromWindowId(environment.AppWindowId) : 0;
}
