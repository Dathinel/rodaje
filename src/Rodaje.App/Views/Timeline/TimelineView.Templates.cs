using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.MotionTemplates;
using Rodaje.App.Shell;
using Rodaje.Core.Model;
using Rodaje.Packages.Content;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// Plantillas de movimiento (D-064) en el menú del clip: «Guardar como plantilla…» con uno o varios títulos seleccionados
/// (cada uno será una capa) y «Convertir en títulos» sobre una plantilla.
/// </summary>
internal sealed partial class TimelineView
{
    private void AddTemplateItems(MenuFlyout menu)
    {
        EditorSession session = Session!;
        IReadOnlyList<Clip> titles = TemplateAuthoring.Titles(session.Sequence, session.SelectedClipIds);
        Clip? template = session.SelectedTemplate;
        if (titles.Count == 0 && template is null)
        {
            return;
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        if (titles.Count > 0)
        {
            var save = new MenuFlyoutItem { Text = "Guardar como plantilla…" };
            ToolTipService.SetToolTip(save, "Guarda los títulos seleccionados como una plantilla de movimiento en tus complementos propios; tú eliges qué campos se podrán cambiar");
            save.Click += (_, _) => UiTask.Run(SaveTemplateAsync(titles));
            menu.Items.Add(save);
        }

        if (template is not null)
        {
            string id = template.Id;
            var convert = new MenuFlyoutItem { Text = "Convertir en títulos" };
            convert.Click += (_, _) => session.ConvertTemplateToTitles(id);
            menu.Items.Add(convert);
        }
    }

    /// <summary>
    /// El diálogo lista los campos de cada capa (texto, color de relleno, de fondo, de borde, fuente y tamaño) con su casilla
    /// «Expuesto» y el nombre que verá quien edite; al aceptar escribe <c>propios\&lt;nombre&gt;.json</c>.
    /// </summary>
    private async Task SaveTemplateAsync(IReadOnlyList<Clip> titles)
    {
        if (DialogGate.IsOpen || _services is not { } services || XamlRoot is null)
        {
            return;
        }

        EditorSession session = services.Session;
        Sequence sequence = session.Sequence;
        IReadOnlyList<TemplateFieldChoice> fields = TemplateAuthoring.Fields(titles);
        string suggested = titles.Count == 1 ? "Mi título" : "Mi rótulo";
        var nameBox = new TextBox { Header = "Nombre de la plantilla", Text = suggested, MaxLength = 80, SelectionStart = 0, SelectionLength = suggested.Length };
        var rows = new StackPanel { Spacing = 4 };
        var controls = new List<(CheckBox Exposed, TextBox Label)>();
        int layer = -1;
        foreach (TemplateFieldChoice field in fields)
        {
            if (field.Layer != layer)
            {
                layer = field.Layer;
                string text = titles[layer].Generator!.Text.ReplaceLineEndings(" ");
                rows.Children.Add(new TextBlock
                {
                    Text = $"Capa {layer + 1}: «{(text.Length > 40 ? text[..40] + "…" : text)}»",
                    Style = (Style)Application.Current.Resources["BodyTextStyle"],
                    Margin = new Thickness(0, 8, 0, 0),
                });
            }

            var exposed = new CheckBox { Content = FieldName(field.Field), IsChecked = field.Exposed, MinWidth = 150 };
            ToolTipService.SetToolTip(exposed, "Expuesto: quien use la plantilla podrá cambiar este campo desde el inspector");
            var label = new TextBox { Text = field.Label, MaxLength = 80, PlaceholderText = "Nombre visible", IsEnabled = field.Exposed, MinWidth = 200 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(label, $"Nombre visible de {FieldName(field.Field)}, capa {layer + 1}");
            exposed.Click += (_, _) => label.IsEnabled = exposed.IsChecked == true;
            controls.Add((exposed, label));
            rows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { exposed, label } });
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "Cada título seleccionado será una capa, con su posición y su desfase respecto del primero. Marca «Expuesto» en lo que se podrá cambiar al usarla y ponle el nombre que se verá. La plantilla queda en tus complementos propios, en Plantillas de la biblioteca.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(nameBox);
        content.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Guardar como plantilla",
            Content = content,
            PrimaryButtonText = "Guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        string name = string.IsNullOrWhiteSpace(nameBox.Text) ? suggested : nameBox.Text.Trim();
        var chosen = new List<TemplateFieldChoice>(fields.Count);
        for (int i = 0; i < fields.Count; i++)
        {
            string label = string.IsNullOrWhiteSpace(controls[i].Label.Text) ? fields[i].Label : controls[i].Label.Text.Trim();
            chosen.Add(fields[i] with { Exposed = controls[i].Exposed.IsChecked == true, Label = label });
        }

        try
        {
            string json = TemplateAuthoring.Build(name, sequence, titles, chosen);
            // El lector es la frontera: lo que no acepte no se guarda.
            _ = ContentReaders.ReadTemplate(json);
            (string path, _) = TemplateAuthoring.Save(services.Packages.Locations.Own, name, json);
            session.Notify(NotificationSeverity.Success, "Plantilla guardada", $"«{name}» quedó en tus complementos propios: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PackageContentException)
        {
            session.Notify(NotificationSeverity.Error, "No se pudo guardar la plantilla", ex.Message);
        }
    }

    private static string FieldName(TemplateField field) => field switch
    {
        TemplateField.Text => "Texto",
        TemplateField.FillColor => "Color de relleno",
        TemplateField.BackgroundColor => "Color de fondo",
        TemplateField.StrokeColor => "Color de borde",
        TemplateField.Font => "Fuente",
        _ => "Tamaño",
    };
}
