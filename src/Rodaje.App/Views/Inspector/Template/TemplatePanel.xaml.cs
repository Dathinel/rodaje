using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.MotionTemplates;
using Rodaje.App.Shell;
using Rodaje.Core.Model;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Views.Inspector.Template;

/// <summary>
/// Los parámetros expuestos de la plantilla de movimiento seleccionada (D-064). Cambiar un valor vuelve a instanciar las
/// capas y es un paso del historial (<c>TemplateEdits.SetValues</c>); teclear o arrastrar un deslizador se funden en uno.
/// </summary>
internal sealed partial class TemplatePanel : UserControl, IEditorView
{
    private readonly List<Action<JsonNode?>> _refreshers = [];
    private EditorServices? _services;
    private string? _clipId;
    private string? _documentText;
    private TemplateDocument? _document;
    private MotionTemplateEntry? _newer;
    private string? _newerFor;
    private bool _updating;
    private bool _active;

    public TemplatePanel() => InitializeComponent();

    /// <summary>Hay una plantilla seleccionada: la pestaña Texto muestra este panel en lugar del de títulos.</summary>
    public bool IsActive => _active;

    public event EventHandler? ActiveChanged;

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        services.Session.SelectionChanged += (_, _) => Refresh();
        services.Session.ProjectEdited += (_, _) => Refresh();
        services.Session.ProjectOpened += (_, _) => Refresh();
        services.Packages.Changed += (_, _) =>
        {
            _newerFor = null;
            Refresh();
        };
        Refresh();
    }

    private void Refresh()
    {
        if (_services is not { } services)
        {
            return;
        }

        Clip? clip = services.Session.SelectedTemplate;
        bool active = clip is not null;
        if (active != _active)
        {
            _active = active;
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }

        if (clip?.Generator?.Template is not { } template)
        {
            _clipId = null;
            return;
        }

        if (clip.Id != _clipId || template.Document != _documentText)
        {
            services.Session.BreakMerge();
            _clipId = clip.Id;
            _documentText = template.Document;
            try
            {
                _document = ContentReaders.ReadTemplate(template.Document);
            }
            catch (PackageContentException)
            {
                // El proyecto trae un documento que este programa no lee: las capas se siguen viendo, pero no hay campos.
                _document = null;
            }

            BuildRows();
        }

        ClipTitle.Text = template.Name;
        SourceText.Text = template.Source is { } source && PackageReference.TryParse(source, out PackageReference? reference)
            ? $"Plantilla de {reference.PackageId}, versión {reference.Version}"
            : "Plantilla propia";
        _updating = true;
        try
        {
            if (_document is { } document)
            {
                for (int i = 0; i < _refreshers.Count; i++)
                {
                    _refreshers[i](TemplateInstances.Current(document, template.Values, document.Parameters[i]));
                }
            }
        }
        finally
        {
            _updating = false;
        }

        ShowUpdate(template.Source);
    }

    private void ShowUpdate(string? source)
    {
        if (_services is not { } services)
        {
            return;
        }

        if (source != _newerFor)
        {
            _newerFor = source;
            _newer = null;
            if (source is not null)
            {
                PackageScan installed = services.Packages.Installed;
                UiTask.Run(FindNewerAsync(installed, source));
            }
        }

        UpdateBar.Visibility = _newer is null ? Visibility.Collapsed : Visibility.Visible;
        if (_newer is { } newer && PackageReference.TryParse(newer.Reference, out PackageReference? reference))
        {
            UpdateButton.Content = $"Actualizar a {reference.Version}";
        }
    }

    private async Task FindNewerAsync(PackageScan installed, string source)
    {
        MotionTemplateEntry? newer = await Task.Run(() => MotionTemplateCatalog.Newer(installed, source));
        if (_newerFor == source && newer is { Kind: TemplateKind.Title })
        {
            _newer = newer;
            Refresh();
        }
    }

    private void Commit(TemplateParameter parameter, JsonNode value, bool single)
    {
        if (_updating || _services is not { } services || _clipId is not { } clipId)
        {
            return;
        }

        if (single)
        {
            services.Session.BreakMerge();
        }

        services.Session.SetTemplateValue(clipId, parameter.Id, value);
        if (single)
        {
            services.Session.BreakMerge();
        }
    }

    private void EndChange() => _services?.Session.BreakMerge();

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _clipId is not { } clipId || _newer is not { } newer)
        {
            return;
        }

        services.Session.BreakMerge();
        if (services.Session.UpdateTemplate(clipId, newer) is { } reset)
        {
            string version = PackageReference.TryParse(newer.Reference, out PackageReference? reference) ? reference.Version.ToString() : "";
            services.Session.Notify(NotificationSeverity.Success, $"Plantilla actualizada a {version}", reset.Count == 0
                ? "Se conservaron todos tus valores."
                : $"Volvieron a su valor por defecto: {string.Join(", ", reset)}. Lo demás se conservó.");
        }
    }

    private void OnConvertClick(object sender, RoutedEventArgs e)
    {
        if (_services is { } services && _clipId is { } clipId)
        {
            services.Session.BreakMerge();
            services.Session.ConvertTemplateToTitles(clipId);
        }
    }
}
