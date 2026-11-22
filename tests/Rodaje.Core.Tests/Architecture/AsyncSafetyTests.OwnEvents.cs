using System.Text.RegularExpressions;

namespace Rodaje.Core.Tests.Architecture;

/// <summary>
/// Que no vuelva el cierre sin rastro de la tarea 91: un desplegable o una lista de WinUI que rehace sus elementos (o se quita
/// del árbol) dentro de su propio <c>SelectionChanged</c>, <c>ItemClick</c> o <c>DropDownClosed</c> cierra la app en código
/// nativo, sin pasar por la red .NET. Cada manejador de esos eventos en la interfaz difiere su trabajo (RunAfterEvent, Choose,
/// QueueRefresh; él o un método al que llama) o está en la lista con el motivo por el que no rehace su propio control.
/// </summary>
public sealed partial class AsyncSafetyTests
{
    // Manejadores de esos eventos que actúan dentro del evento (archivo donde está su cuerpo → cuántos y por qué no rehacen
    // su propio control). Uno nuevo se difiere o se añade aquí tras comprobar la cadena entera hasta el refresco.
    private static readonly Dictionary<string, (int Count, string Reason)> OwnEventAllowed = new(StringComparer.Ordinal)
    {
        ["Rodaje.App/Views/Analysis/DuckingDialog.cs"] = (1, "solo cambia un texto del diálogo."),
        ["Rodaje.App/Views/Export/ExportView.xaml.cs"] = (17, "cambian el modelo; la vista se refresca con QueueRefresh, después del evento, y las listas no dependen de lo elegido en ellas (#687)."),
        ["Rodaje.App/Views/Inspector/Dynamics/DynamicsPanel.cs"] = (2, "Detector tiene elementos fijos y rehace otro desplegable; el de preajustes pasa por OnPresetChosen y LaterForChosen, que difiere."),
        ["Rodaje.App/Views/Inspector/Filters/FiltersPanel.Image.cs"] = (1, "la proporción tiene elementos fijos y su refresco solo elige."),
        ["Rodaje.App/Views/Inspector/Filters/FiltersPanel.Options.cs"] = (1, "una opción no cambia el tipo ni el contexto del filtro, que es lo único que rehace las filas."),
        ["Rodaje.App/Views/Inspector/Filters/FiltersPanel.Presets.cs"] = (1, "la cuadrícula de preajustes solo se rehace con RunLater tras un await."),
        ["Rodaje.App/Views/Inspector/Filters/FiltersPanel.xaml.cs"] = (1, "elegir un filtro solo muestra sus detalles; FilterItem no depende de los valores y la lista no se rehace."),
        ["Rodaje.App/Views/Inspector/Masks/MasksPanel.xaml.cs"] = (2, "elegir una máscara no cambia MaskItem; Combinar tiene elementos fijos y rehace la lista, otro control."),
        ["Rodaje.App/Views/Inspector/Properties/GraphPanel.cs"] = (1, "elegir el parámetro solo redibuja la gráfica."),
        ["Rodaje.App/Views/Inspector/Properties/PropertiesPanel.xaml.cs"] = (1, "la fusión tiene elementos fijos; su refresco solo elige."),
        ["Rodaje.App/Views/Inspector/Template/TemplatePanel.Rows.cs"] = (2, "cambian Values; las filas solo se rehacen si cambia el documento de la plantilla."),
        ["Rodaje.App/Views/Inspector/Text/TextPanel.Pro.cs"] = (3, "las galerías solo eligen y redibujan miniaturas (la de estilos se rehace tras un await si cambian los paquetes); el desplegable de opción no se rehace."),
        ["Rodaje.App/Views/Inspector/Text/TextPanel.Rows.cs"] = (2, "fuente y peso no se rehacen: su refresco solo elige."),
        ["Rodaje.App/Views/Inspector/Text/TextPanel.xaml.cs"] = (1, "OnStyleChosenAsync cede con Task.Yield antes de editar."),
        ["Rodaje.App/Views/Inspector/Tracking/TrackingPanel.xaml.cs"] = (2, "elegir un seguimiento no cambia TrackItem; el uso rehace el destino (otro control, con ComboBoxChoices)."),
        ["Rodaje.App/Views/Inspector/Transition/TransitionPanel.xaml.cs"] = (1, "el degradado tiene elementos fijos y está fuera de las filas que se rehacen."),
        ["Rodaje.App/Views/Mixer/StripView.cs"] = (1, "el clic en una inserción solo abre su editor."),
        ["Rodaje.App/Views/Player/PlayerView.Format.cs"] = (4, "el refresco del diálogo de formato solo cambia textos y estados, no los elementos."),
        ["Rodaje.App/Views/Preferences/PreferencesView.xaml.cs"] = (2, "el tema se aplica sin rehacer los desplegables de tema (se llenan desde botones de fuera)."),
        ["Rodaje.App/Views/Subtitles/AutoSubtitlesDialog.cs"] = (2, "el idioma solo activa un botón; el clic en la lista solo mueve el cursor."),
        ["Rodaje.App/Views/Timeline/TimelineView.xaml.cs"] = (2, "los modos de onda y de huecos solo cambian el estado de la sesión."),
        ["Rodaje.App/Views/Translate/TranslateDialog.cs"] = (3, "su Refresh solo cambia textos y botones, no los elementos."),
        ["Rodaje.App/Views/Vision/VisionDialogs.cs"] = (1, "la calidad solo recalcula la estimación."),
        ["Rodaje.App/Views/Voice/VoiceDialog.cs"] = (2, "el idioma rehace el desplegable de voces, otro control; la voz solo refresca botones."),
    };

    private static readonly string[] OwnEventDeferrals = ["RunAfterEvent(", ".Choose(", "QueueRefresh("];

    [Fact]
    public void ItemsControlEventsDeferOrAreReviewed()
    {
        List<(string File, string Code)> ui = [.. Sources("Rodaje.App/").Where(s => IsUiFile(s.File))];
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        var problems = new List<string>();
        void Check(string file, string where, string? body)
        {
            if (body is null)
            {
                problems.Add($"{where}: no se encontró el manejador.");
            }
            else if (!Defers(ui, Folder(file), body))
            {
                found[file] = found.GetValueOrDefault(file) + 1;
            }
        }

        foreach ((string file, string code) in ui)
        {
            if (file == "Rodaje.App/Views/Inspector/DispatcherQueueLater.cs")
            {
                continue; // Es la red: RetryWhenClosed ya difiere.
            }

            foreach (Match m in OwnEventSubscriptionRegex().Matches(code))
            {
                // EditorSession.SelectionChanged es la selección del proyecto, no la de un control.
                if (m.Groups["receiver"].Value.Contains("ession", StringComparison.Ordinal))
                {
                    continue;
                }

                if (m.Groups["method"].Success)
                {
                    (string? bodyFile, string? body) = MethodIn(ui, Folder(file), m.Groups["method"].Value);
                    Check(bodyFile ?? file, $"{file}:{Line(code, m.Index)}", body);
                }
                else
                {
                    Check(file, $"{file}:{Line(code, m.Index)}", BodyAfter(code, m.Index + m.Length));
                }
            }
        }

        string app = Path.Combine(RepoRoot(), "src", "Rodaje.App");
        foreach (string path in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            string file = "Rodaje.App/" + Path.GetRelativePath(app, path).Replace('\\', '/');
            if (!IsUiFile(file))
            {
                continue;
            }

            foreach (Match m in XamlOwnEventRegex().Matches(File.ReadAllText(path)))
            {
                (string? bodyFile, string? body) = MethodIn(ui, Folder(file), m.Groups[1].Value);
                Check(bodyFile ?? file, $"{file}#{m.Groups[1].Value}", body);
            }
        }

        foreach ((string file, int count) in found)
        {
            int allowed = OwnEventAllowed.TryGetValue(file, out var entry) ? entry.Count : 0;
            if (count != allowed)
            {
                problems.Add($"{file}: {count} manejadores de SelectionChanged/ItemClick/DropDownClosed actúan dentro del evento (la lista cubre {allowed}). "
                    + "Si su cadena puede rehacer o quitar ese mismo control, difiérelo con DispatcherQueue.RunAfterEvent o Choose; si no, ajusta OwnEventAllowed con el motivo.");
            }
        }

        problems.AddRange(OwnEventAllowed.Keys.Where(file => !found.ContainsKey(file)).Select(file => $"{file}: entrada de OwnEventAllowed que ya no aplica."));
        Assert.True(OwnEventAllowed.Values.All(e => e.Count > 0 && !string.IsNullOrWhiteSpace(e.Reason)));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void OwnEventCheckSeesLambdasMethodsAndHelpers()
    {
        List<(string File, string Code)> ui =
        [
            ("Rodaje.App/Views/X/A.cs", "void Later() { DispatcherQueue.RunAfterEvent(Apply); } void OnPick(object s, object e) { Later(); } void Bare() { Refresh(); }"),
        ];
        Assert.True(Defers(ui, "Rodaje.App/Views/X", "{ if (x) Later(); }"));
        Assert.True(Defers(ui, "Rodaje.App/Views/X", "DispatcherQueue.Choose(_list, box.SelectedIndex, _updating, v => null)"));
        Assert.False(Defers(ui, "Rodaje.App/Views/X", "{ Bare(); }"));
        Assert.False(Defers(ui, "Rodaje.App/Views/Y", "{ Later(); }"));
        Assert.Equal("{ Later(); }", MethodIn(ui, "Rodaje.App/Views/X", "OnPick").Body);
        Match m = OwnEventSubscriptionRegex().Match("box.SelectionChanged += OnPick;");
        Assert.Equal("OnPick", m.Groups["method"].Value);
        m = OwnEventSubscriptionRegex().Match("list.ItemClick += (_, e) => Seek(e);");
        Assert.False(m.Groups["method"].Success);
        Assert.Equal(" Seek(e)", BodyAfter("list.ItemClick += (_, e) => Seek(e);", m.Index + m.Length));
    }

    private static string Folder(string file) => file[..file.LastIndexOf('/')];

    // Si el cuerpo difiere su trabajo, él o un método de la misma carpeta al que llama.
    private static bool Defers(List<(string File, string Code)> ui, string folder, string body) =>
        DefersHere(body)
        || CalledNameRegex().Matches(body).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)
            .Any(name => MethodIn(ui, folder, name).Body is { } called && DefersHere(called));

    private static bool DefersHere(string body) => OwnEventDeferrals.Any(d => body.Contains(d, StringComparison.Ordinal));

    private static (string? File, string? Body) MethodIn(List<(string File, string Code)> ui, string folder, string name)
    {
        var declaration = new Regex(@"\bvoid\s+" + Regex.Escape(name) + @"\s*\(");
        foreach ((string file, string code) in ui.Where(s => Folder(s.File) == folder))
        {
            if (declaration.Match(code) is { Success: true } m)
            {
                return (file, BodyAfter(code, MatchingClose(code, m.Index + m.Length - 1) + 1));
            }
        }

        return (null, null);
    }

    [GeneratedRegex(@"(?<receiver>\w+)\.(?:SelectionChanged|ItemClick|DropDownClosed)\s*\+=\s*(?:(?<method>\w+)\s*;)?")]
    private static partial Regex OwnEventSubscriptionRegex();

    [GeneratedRegex(@"\b(?:SelectionChanged|ItemClick|DropDownClosed)=""(\w+)""")]
    private static partial Regex XamlOwnEventRegex();

    [GeneratedRegex(@"(?<![\w.])(\w+)\s*\(")]
    private static partial Regex CalledNameRegex();
}
