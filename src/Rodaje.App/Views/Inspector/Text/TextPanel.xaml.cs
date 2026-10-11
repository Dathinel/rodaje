using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Titles;
using Rodaje.App.Shell;
using Rodaje.Core.Model;
using Rodaje.Paquetes;

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
    private PackageScan? _stylesScan;

    public TextPanel()
    {
        InitializeComponent();
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
            ShowFontWarning(style);
            ShowAnimation(generator, style);
            StyleSection.Summary = string.Create(CultureInfo.CurrentCulture, $"{style.Font.Family} · {style.Size:0}");
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
            // Windows escribe «\r» al pulsar Intro en un TextBox; el formato guarda «\n».
            editor.SetText(ContentBox.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
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

    /// <summary>Una fuente de un paquete que no está instalado se verá con la de respaldo.</summary>
    private void ShowFontWarning(TextStyle style)
    {
        string? warning = null;
        if (style.Font.Source is { } source && PackageReference.TryParse(source, out PackageReference? reference)
            && _services?.Packages.Installed.Find(reference.PackageId) is null)
        {
            string fallback = style.Font.Fallback.Count > 0 ? style.Font.Fallback[0] : "la fuente del sistema";
            warning = $"La fuente «{style.Font.Family}» viene del paquete «{reference.PackageId}», que no está instalado: se verá con «{fallback}».";
        }

        FontWarning.Text = warning ?? "";
        FontWarning.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
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
