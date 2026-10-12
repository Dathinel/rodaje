using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Titles;
using Rodaje.App.Shell;
using Rodaje.Core.Model;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Views.Inspector.Text;

/// <summary>
/// Pestaña «Texto» del título seleccionado: contenido, estilo (copia en el clip, D-029), borde, sombra, fondo y las
/// animaciones de entrada y salida. Lo que se edita pasa por <see cref="TitleEditor"/>.
/// </summary>
internal sealed partial class TextPanel : UserControl, IEditorView
{
    private readonly List<Action<TextStyle>> _refreshers = [];
    private readonly List<LibraryElement> _styles = [];
    private EditorServices? _services;
    private TitleEditor? _editor;
    private string? _clipId;
    private bool _updating;
    private ComboBox? _styleBox;
    private TextBlock? _sourceText;
    private Button? _reapplyButton;
    private PackageScan? _stylesScan;

    public TextPanel()
    {
        InitializeComponent();
        ContentBox.MaxLength = ModelValidator.MaxTextLength;
    }

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _editor = new TitleEditor(services.Session);
        BuildRows();
        foreach (InspectorSection section in (InspectorSection[])[ContentSection, StyleSection, StrokeSection, ShadowSection, BackgroundSection, AnimationSection])
        {
            section.Remember(services.Settings);
        }

        services.Session.SelectionChanged += (_, _) => Refresh();
        services.Session.ProjectEdited += (_, _) => Refresh();
        services.Session.ProjectOpened += (_, _) => Refresh();
        services.Packages.Changed += (_, _) => Refresh();
        services.Player.MissingFontsChanged += () => DispatcherQueue.TryEnqueue(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        Clip? clip = editor.Target();
        if (clip?.Id != _clipId)
        {
            editor.EndTyping();
            editor.EndChange();
        }

        bool changedClip = clip?.Id != _clipId;
        _clipId = clip?.Id;
        EmptyText.Visibility = clip is null ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = clip is null ? Visibility.Collapsed : Visibility.Visible;
        if (clip?.Generator is not { } generator)
        {
            return;
        }

        _updating = true;
        try
        {
            ClipTitle.Text = ClipLabels.TitleName(generator);
            ToolTipService.SetToolTip(ClipTitle, ClipTitle.Text);

            // Mientras se escribe no se pisa lo que hay en el campo (el cursor saltaría al principio).
            if (changedClip || ContentBox.FocusState == FocusState.Unfocused)
            {
                if (ContentBox.Text != generator.Text)
                {
                    ContentBox.Text = generator.Text;
                }
            }

            TextStyle style = generator.Style ?? TitleStyles.Default();
            foreach (Action<TextStyle> refresh in _refreshers)
            {
                refresh(style);
            }

            ShowStyleChoice(style);
            ShowFontWarning(clip.Id, style);
            ShowLegibility(clip.Id, style);
            ShowAnimation(generator, style);
            StyleSection.Summary = string.Create(SpanishNumbers.Format, $"{style.Font.Family} · {style.Size:0}");
            StrokeSection.Summary = style.Stroke is null ? "Sin borde" : style.Stroke.Color;
            ShadowSection.Summary = style.Shadow is null ? "Sin sombra" : style.Shadow.Color;
            BackgroundSection.Summary = style.Background is null ? "Sin fondo" : style.Background.Color;
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnContentChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating && _editor is { } editor)
        {
            editor.SetText(TitleEditor.FromTextBox(ContentBox.Text));
        }
    }

    private void OnContentLostFocus(object sender, RoutedEventArgs e) => _editor?.EndTyping();

    private void OnEdgesClick(object sender, RoutedEventArgs e)
    {
        if (!_updating && _editor is { } editor)
        {
            editor.SetEdges(InBox.IsChecked == true, OutBox.IsChecked == true);
        }
    }

    private void OnConvertClick(object sender, RoutedEventArgs e) => _editor?.ConvertToKeyframes();

    private void ShowAnimation(ClipGenerator generator, TextStyle style)
    {
        InBox.IsEnabled = style.In is not null;
        OutBox.IsEnabled = style.Out is not null;
        InBox.IsChecked = style.In is not null && generator.Edges.In;
        OutBox.IsChecked = style.Out is not null && generator.Edges.Out;
        InText.Text = TitleEditor.DescribeAnimation(style.In, entry: true) ?? "Este estilo no trae animación de entrada.";
        OutText.Text = TitleEditor.DescribeAnimation(style.Out, entry: false) ?? "Este estilo no trae animación de salida.";
        bool any = (style.In is not null && generator.Edges.In) || (style.Out is not null && generator.Edges.Out);
        ConvertButton.IsEnabled = any;
        AnimationHint.Text = any
            ? "Siguen a los bordes del clip: recortar o alargar el título las conserva."
            : "Elige un estilo con animación para que el título entre o salga solo.";
        AnimationSection.Summary = any ? "Animado" : "Sin animación";
    }

    /// <summary>
    /// Una fuente de un paquete que no está instalado, o que DirectWrite no encuentra (lo dice el reproductor tras cada
    /// cambio), se verá con la de respaldo.
    /// </summary>
    private void ShowFontWarning(string clipId, TextStyle style)
    {
        string? warning = null;
        if (style.Font.Source is { } source && PackageReference.TryParse(source, out PackageReference? reference)
            && _services?.Packages.Installed.Find(reference.PackageId) is null)
        {
            string fallback = style.Font.Fallback.Count > 0 ? style.Font.Fallback[0] : "la fuente del sistema";
            warning = $"La fuente «{style.Font.Family}» viene del paquete «{reference.PackageId}», que no está instalado: se verá con «{fallback}».";
        }
        else if (_services?.Player.MissingFonts.FirstOrDefault(m => m.ClipId == clipId && m.Family == style.Font.Family) is { } missing)
        {
            warning = $"La fuente «{missing.Family}» no está en este equipo: se ve con «{missing.UsedFamily}» y al exportar se pedirá confirmación.";
        }

        FontWarning.Text = warning ?? "";
        FontWarning.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>En una pista de subtítulos, avisa si el estilo baja del tamaño o del contraste mínimos.</summary>
    private void ShowLegibility(string clipId, TextStyle style)
    {
        Sequence? sequence = _services?.Session.Sequence;
        string? warning = sequence?.FindClip(clipId) is { Track.Role: TrackRoles.Subtitles }
            ? SubtitleStyles.Legibility(style, sequence.Width, sequence.Height)
            : null;
        LegibilityWarning.Text = warning ?? "";
        LegibilityWarning.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnSaveStyleClick(object sender, RoutedEventArgs e)
    {
        if (DialogGate.IsOpen || _services is not { } services || _editor is not { } editor || editor.Target() is not { Generator.Style: { } style })
        {
            return;
        }

        string suggested = style.Name is { Length: > 0 } name ? name : "Mi estilo";
        var nameBox = new TextBox { Text = suggested, SelectionStart = 0, SelectionLength = suggested.Length };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "El estilo quedará en tus complementos propios, en la biblioteca de Texto, listo para otros títulos o para publicarlo como paquete.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(nameBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Guardar como estilo",
            Content = content,
            PrimaryButtonText = "Guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };

        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        string chosen = string.IsNullOrWhiteSpace(nameBox.Text) ? suggested : nameBox.Text.Trim();
        try
        {
            if (editor.SaveStyle(AnimationPreset.UserFolder, chosen) is { } saved)
            {
                services.Session.Notify(NotificationSeverity.Success, "Estilo guardado", $"«{chosen}» quedó en tus complementos propios: {saved.Path}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            services.Session.Notify(NotificationSeverity.Error, "No se pudo guardar el estilo", ex.Message);
        }
    }

    private void OnReapplyStyleClick(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _editor is not { } editor)
        {
            return;
        }

        try
        {
            // Un estilo es un JSON de hasta 1 MB: se lee en el momento, como al elegirlo de la lista.
            ReapplyResult result = editor.ReapplyStyle(new InstalledContentLoader(() => services.Packages.Installed));
            switch (result)
            {
                case ReapplyResult.Missing:
                    services.Session.Notify(NotificationSeverity.Warning, "No se pudo reaplicar el estilo", "El paquete del que salió ya no está instalado.");
                    break;
                case ReapplyResult.Unchanged:
                    services.Session.Notify(NotificationSeverity.Informational, "El estilo ya está al día", "El título tiene el estilo del paquete tal cual.");
                    break;
            }
        }
        catch (Exception ex) when (ex is PackageContentException or IOException or UnauthorizedAccessException)
        {
            services.Session.Notify(NotificationSeverity.Warning, "No se pudo reaplicar el estilo", ex.Message);
        }
    }

    private void ShowStyleChoice(TextStyle style)
    {
        if (_styleBox is null || _services is null)
        {
            return;
        }

        PackageScan scan = _services.Packages.Installed;
        if (!ReferenceEquals(scan, _stylesScan))
        {
            _stylesScan = scan;
            _styles.Clear();
            _styles.AddRange(LibraryElements.For(LibraryCategory.Text, scan));
            _styleBox.Items.Clear();
            foreach (LibraryElement element in _styles)
            {
                _styleBox.Items.Add(element.IsBuiltIn ? element.Name : $"{element.Name} · {element.Source}");
            }
        }

        int index = style.Source is { } source
            ? _styles.FindIndex(e => e.Reference == source)
            : _styles.FindIndex(e => e.IsBuiltIn && style.ContentEquals(TitleStyles.Default()));
        _styleBox.SelectedIndex = index;
        _styleBox.PlaceholderText = style.Name is { Length: > 0 } name ? $"{name} (cambiado)" : "Personalizado";
        if (_sourceText is not null)
        {
            _sourceText.Text = style.Source is null ? "" : $"Copiado de {style.Source}: actualizar el paquete no lo cambia.";
            _sourceText.Visibility = style.Source is null ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_reapplyButton is not null)
        {
            _reapplyButton.IsEnabled = style.Source is { } from && from != TitleStyles.BuiltInReference;
        }
    }

    private async void OnStyleChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _services is not { } services || _editor is not { } editor || _styleBox is not { SelectedIndex: >= 0 } box
            || box.SelectedIndex >= _styles.Count)
        {
            return;
        }

        LibraryElement element = _styles[box.SelectedIndex];
        var applier = new ElementApplier(services.Session, new InstalledContentLoader(() => services.Packages.Installed));
        string? clipId = _clipId;
        if (await applier.LoadTextStyleAsync(element) is { } style && clipId == _clipId)
        {
            editor.ApplyStyle(style);
        }
    }
}
