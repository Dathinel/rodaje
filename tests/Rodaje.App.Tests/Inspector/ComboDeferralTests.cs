using System.Text.RegularExpressions;
using Rodaje.App.Services.Inspector;

namespace Rodaje.App.Tests.Inspector;

/// <summary>
/// Tarea 121 (revisión de la 99): el diferido de los desplegables que editan y refrescan el panel. Cada vista anota la
/// elección en su <c>SelectionChanged</c> y deja aplicar y refrescar para después del evento con
/// <see cref="ComboChoices.Choose{T}"/>; refrescar dentro del evento puede rehacer el desplegable y cerrar la app desde WinUI
/// (tarea 91, «Escala»). Aquí con una cola falsa y, para las vistas, leyendo su código.
/// </summary>
public class ComboDeferralTests
{
    /// <summary>Los <c>SelectionChanged</c> de las vistas que editan y refrescan el panel (archivo y método).</summary>
    public static TheoryData<string, string> Handlers => new()
    {
        { "src/Rodaje.App/Views/Inspector/Properties/PropertiesPanel.xaml.cs", "OnGraphParameterSelected" },
        { "src/Rodaje.App/Views/Inspector/Filters/FiltersPanel.xaml.cs", "OnLutSelected" },
        { "src/Rodaje.App/Views/Inspector/Transition/TransitionPanel.xaml.cs", "OnTypeSelected" },
        { "src/Rodaje.App/Views/Inspector/Masks/MasksPanel.xaml.cs", "OnTargetSelected" },
        { "src/Rodaje.App/Views/Inspector/Dynamics/DynamicsPanel.cs", "OnTargetChosen" },
        { "src/Rodaje.App/Views/Mixer/StripView.cs", "OnOutputSelected" },
    };

    /// <summary>
    /// Los desplegables de las vistas que se refrescan con la lista abierta (archivo y campo): cada uno repite el refresco al
    /// cerrarla (<c>RetryWhenClosed</c>) con el mismo <see cref="ClosedListRetry"/> que anota lo pendiente (<c>retry:</c> en
    /// su <c>ComboBoxChoices.Show</c> o <c>Defer(caja.IsDropDownOpen)</c> antes de él). Tareas 126 y 141.
    /// </summary>
    public static TheoryData<string, string> RetriedBoxes => new()
    {
        { "src/Rodaje.App/Views/Inspector/Properties/PropertiesPanel.xaml.cs", "GraphParameterBox" },
        { "src/Rodaje.App/Views/Inspector/Filters/FiltersPanel.xaml.cs", "LutBox" },
        { "src/Rodaje.App/Views/Inspector/Transition/TransitionPanel.xaml.cs", "TypeBox" },
        { "src/Rodaje.App/Views/Inspector/Masks/MasksPanel.xaml.cs", "TargetBox" },
        { "src/Rodaje.App/Views/Inspector/Dynamics/DynamicsPanel.cs", "_targetBox" },
        { "src/Rodaje.App/Views/Inspector/Dynamics/DynamicsPanel.cs", "_presetBox" },
        { "src/Rodaje.App/Views/Inspector/Dynamics/DynamicsPanel.cs", "_sidechain" },
        { "src/Rodaje.App/Views/Mixer/StripView.cs", "_output" },
        { "src/Rodaje.App/Views/Inspector/Text/TextPanel.xaml.cs", "_styleBox" },
        { "src/Rodaje.App/Views/Inspector/Tracking/TrackingPanel.xaml.cs", "UseBox" },
        { "src/Rodaje.App/Views/Inspector/Tracking/TrackingPanel.xaml.cs", "TargetBox" },
    };

    /// <summary>
    /// Las vistas cuyos <c>ComboBoxChoices.Show</c> no necesitan <c>retry:</c>, y por qué. Exportar refresca toda la vista en
    /// su propio <c>DropDownClosed</c>.
    /// </summary>
    private static readonly Dictionary<string, string> WithoutRetry = new(StringComparer.Ordinal)
    {
        ["src/Rodaje.App/Views/Export/ExportView.xaml.cs"] = "DropDownClosed",
    };

    private static ChoiceList<string> List(params string[] values)
    {
        var list = new ChoiceList<string>();
        foreach (string value in values)
        {
            list.Add(value.ToUpperInvariant(), value);
        }

        return list;
    }

    [Fact]
    public void Elegir_SeAplicaDespuesDelEvento_YUnaSolaVez()
    {
        var box = new FakeChoiceBox();
        bool updating = false;
        ChoiceList<string> shown = ComboScenario.Show(box, new ChoiceList<string>(), List("a", "b", "c"), 0, ref updating);
        var later = new Queue<Action>();
        var applied = new List<(string Value, bool InEvent)>();
        box.SelectionChanged = () => ComboChoices.Choose(shown, box.SelectedIndex, updating, value => () => applied.Add((value, box.InSelectionChanged)),
            a => { later.Enqueue(a); return true; }, _ => false);

        box.Pick(2);
        Assert.Empty(applied);
        Assert.Single(later);
        ComboScenario.Drain(later);
        Assert.Equal(("c", false), Assert.Single(applied));
        ComboScenario.Drain(later);
        Assert.Single(applied);
    }

    [Fact]
    public void Elegir_SinNadaQueHacer_FueraDeLaLista_OAlActualizar_NoPoneNadaEnLaCola()
    {
        ChoiceList<string> list = List("a", "b");
        int notes = 0;
        Func<string, Action?> note = value =>
        {
            notes++;
            return value == "a" ? null : () => { };
        };
        var later = new Queue<Action>();
        bool Enqueue(Action a)
        {
            later.Enqueue(a);
            return true;
        }

        Assert.False(ComboChoices.Choose(list, 0, false, note, Enqueue, _ => false));
        Assert.False(ComboChoices.Choose(list, -1, false, note, Enqueue, _ => false));
        Assert.False(ComboChoices.Choose(list, 2, false, note, Enqueue, _ => false));
        Assert.False(ComboChoices.Choose(list, 1, true, note, Enqueue, _ => false));
        Assert.Equal(1, notes);
        Assert.Empty(later);
        Assert.True(ComboChoices.Choose(list, 1, false, note, Enqueue, _ => false));
        Assert.Single(later);
    }

    [Fact]
    public void Elegir_ConLaColaCerrada_SeDescarta()
    {
        // Al cerrar la ventana la cola rechaza la acción: antes se hacía en el acto, dentro del evento.
        int runs = 0;
        Assert.False(ComboChoices.Choose(List("a", "b"), 1, false, _ => () => runs++, _ => false, _ => false));
        Assert.Equal(0, runs);
    }

    [Fact]
    public void UnPanelQueRefrescaDentroDelEvento_FallaEnElEscenario()
    {
        // Lo que hacía el panel antes de la tarea 91 (refrescar en el SelectionChanged): si la lista cambia, el desplegable
        // falso falla como WinUI. Aunque no cambie, el escenario de las vistas (ComboScenario.ChooseEach) falla porque su
        // refresco comprueba que no corre dentro del evento.
        var box = new FakeChoiceBox();
        bool updating = false;
        ChoiceList<string> shown = ComboScenario.Show(box, new ChoiceList<string>(), List("viejo", "a", "b"), 0, ref updating);
        box.SelectionChanged = () =>
        {
            if (!updating)
            {
                shown = ComboScenario.Show(box, shown, List("a", "b"), 0, ref updating);
            }
        };
        Assert.Throws<InvalidOperationException>(() => box.Pick(1));
        Assert.False(box.InSelectionChanged);
    }

    [Fact]
    public void ConLaListaAbiertaEnElEvento_ElRefrescoSeRepiteAlCerrarla()
    {
        // Si WinUI manda el SelectionChanged con la lista abierta y lo diferido llega antes de que se cierre, el refresco
        // devuelve Keep: al cerrarla se repite y la lista queda al día (aquí, sin «viejo», que ya no se ofrece).
        foreach (bool withRetry in new[] { false, true })
        {
            var box = new FakeChoiceBox { SelectsWithListOpen = true };
            bool updating = false;
            string state = "viejo";
            ChoiceList<string> Build() => state == "viejo" ? List("viejo", "a", "b") : List("a", "b");
            ChoiceList<string> shown = ComboScenario.Show(box, new ChoiceList<string>(), Build(), 0, ref updating);
            var later = new Queue<Action>();
            ClosedListRetry? retry = withRetry ? new ClosedListRetry() : null;
            bool Enqueue(Action a)
            {
                later.Enqueue(a);
                return true;
            }

            void Refresh()
            {
                ChoiceList<string> next = Build();
                shown = ComboScenario.Show(box, shown, next, next.IndexOf(state), ref updating, retry);
            }

            box.SelectionChanged = () => ComboChoices.Choose(shown, box.SelectedIndex, updating, value => () =>
            {
                state = value;
                Refresh();
            }, Enqueue, _ => false);
            box.DropDownClosed = () => retry?.Closed(Enqueue, Refresh, _ => false);

            box.Pick(1);
            ComboScenario.Drain(later);
            Assert.Equal("a", state);
            Assert.Equal(3, box.Items.Count);
            Assert.Equal(withRetry, retry?.Pending ?? false);

            box.Close();
            ComboScenario.Drain(later);
            Assert.Equal(withRetry ? 2 : 3, box.Items.Count);
            Assert.Equal(withRetry ? 0 : 1, box.SelectedIndex);
            Assert.Equal("a", shown.Values[box.SelectedIndex]);
            Assert.False(retry?.Pending ?? false);
        }
    }

    [Fact]
    public void Reintento_SoloSiQuedoPendiente_YUnaVez()
    {
        var retry = new ClosedListRetry();
        var later = new Queue<Action>();
        int refreshes = 0;
        bool Enqueue(Action a)
        {
            later.Enqueue(a);
            return true;
        }

        retry.Closed(Enqueue, () => refreshes++, _ => false);
        Assert.Empty(later);
        Assert.False(retry.Defer(isDropDownOpen: false));
        Assert.False(retry.Pending);
        Assert.True(retry.Defer(isDropDownOpen: true));
        Assert.True(retry.Defer(isDropDownOpen: true));
        retry.Closed(Enqueue, () => refreshes++, _ => false);
        retry.Closed(Enqueue, () => refreshes++, _ => false);
        ComboScenario.Drain(later);
        Assert.Equal(1, refreshes);
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    public void LasVistas_AnotanEnElEvento_YAplicanYRefrescanDespues(string file, string method)
    {
        // Si una vista dejara Choose y refrescara o aplicara dentro de su SelectionChanged (la causa del cierre de «Escala»),
        // esta prueba falla: el cuerpo del manejador solo llama a DispatcherQueue.Choose, y lo que aplica va en una lambda
        // «() => …» o es el propio Refresh sin llamar.
        string body = MethodBody(File.ReadAllText(Path.Combine(RepositoryRoot(), file)), method);
        Assert.True(Regex.Count(body, @"\bDispatcherQueue\.Choose\(") == 1, $"{method} debe llamar una vez a DispatcherQueue.Choose.");
        Assert.DoesNotContain("RunAfterEvent", body, StringComparison.Ordinal);
        string calls = Regex.Replace(body, @"\(\) => \w+\(", "() => DEFERRED(");
        calls = calls.Replace("DispatcherQueue.Choose(", "CHOOSE(", StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\b(Refresh|Update\w*|Choose\w*|Show\w*|Run)\(", calls);
        Assert.DoesNotMatch(@"\.(Execute|SetOutput|Set\w+)\(", calls);
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    public void LasVistasDelRelevo_RepitenAlCerrarLaLista(string file, string method)
    {
        // Las seis vistas de #126: sin RetryWhenClosed, o sin Defer/retry:, un refresco con la lista abierta deja el
        // desplegable desfasado hasta el próximo cambio.
        _ = method;
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), file));
        Assert.True(Regex.IsMatch(source, @"\.RetryWhenClosed\("), $"{file} debe llamar a RetryWhenClosed.");
        Assert.True(Regex.IsMatch(source, @"\.Defer\(|\bretry:"), $"{file} debe anotar lo pendiente con Defer( o retry:.");
    }

    [Theory]
    [MemberData(nameof(RetriedBoxes))]
    public void CadaDesplegable_RepiteAlCerrar_ConElMismoReintentoQueAnota(string file, string box)
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), file));
        string? retry = RetryOf(source, box);
        Assert.True(retry is not null, $"{box} de {file} debe llamar a RetryWhenClosed.");
        List<string> shows = [.. ShowCalls(source).Where(c => c.Box == box).Select(c => c.Arguments)];
        Assert.True(shows.Count > 0, $"No encuentro el ComboBoxChoices.Show de {box} en {file}.");
        foreach (string arguments in shows)
        {
            Assert.True(Guarded(source, box, retry!, arguments), $"El ComboBoxChoices.Show de {box} en {file} debe pasar «retry: {retry}» o ir tras «{retry}.Defer({box}.IsDropDownOpen)».");
        }
    }

    [Fact]
    public void TodoShowDeLasVistas_PasaRetry_SalvoLaListaBlanca()
    {
        // Un desplegable nuevo sin relevo al cerrar falla aquí: o pasa retry: (con su RetryWhenClosed), o va tras Defer, o
        // entra en WithoutRetry con su causa.
        string root = RepositoryRoot();
        var missing = new List<string>();
        int count = 0;
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "src", "Rodaje.App", "Views"), "*.cs", SearchOption.AllDirectories))
        {
            string file = Path.GetRelativePath(root, path).Replace('\\', '/');
            string source = File.ReadAllText(path);
            foreach ((string box, string arguments) in ShowCalls(source))
            {
                count++;
                if (WithoutRetry.TryGetValue(file, out string? cause))
                {
                    Assert.Contains(cause, source, StringComparison.Ordinal);
                    continue;
                }

                if (RetryOf(source, box) is not { } retry || !Guarded(source, box, retry, arguments))
                {
                    missing.Add($"{file}: {box}");
                }
            }
        }

        Assert.True(count >= 12, $"Solo encuentro {count} llamadas a ComboBoxChoices.Show: ¿cambió su forma?");
        Assert.True(missing.Count == 0, "Sin relevo al cerrar la lista: " + string.Join("; ", missing));
    }

    /// <summary>El <see cref="ClosedListRetry"/> con que <paramref name="box"/> llama a <c>RetryWhenClosed</c>, o nulo.</summary>
    private static string? RetryOf(string source, string box)
    {
        Match match = Regex.Match(source, @"\b" + Regex.Escape(box) + @"\??\.RetryWhenClosed\(\s*(\w+)\s*,");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Si la llamada pasa <c>retry: <paramref name="retry"/></c> o el archivo anota con <c>Defer</c> antes de ella.</summary>
    private static bool Guarded(string source, string box, string retry, string arguments) =>
        Regex.IsMatch(arguments, @"\bretry:\s*" + Regex.Escape(retry) + @"\b")
        || Regex.IsMatch(source, @"\b" + Regex.Escape(retry) + @"\.Defer\(\s*" + Regex.Escape(box) + @"\.IsDropDownOpen\s*\)");

    /// <summary>Cada <c>ComboBoxChoices.Show(</c> del archivo: el desplegable (primer argumento) y todos sus argumentos.</summary>
    private static IEnumerable<(string Box, string Arguments)> ShowCalls(string source)
    {
        foreach (Match call in Regex.Matches(source, @"\bComboBoxChoices\.Show\("))
        {
            int depth = 1;
            int i = call.Index + call.Length;
            int start = i;
            for (; depth > 0; i++)
            {
                depth += source[i] switch
                {
                    '(' => 1,
                    ')' => -1,
                    _ => 0,
                };
            }

            string arguments = source[start..(i - 1)];
            yield return (Regex.Match(arguments, @"^\s*(\w+)").Groups[1].Value, arguments);
        }
    }

    private static string MethodBody(string source, string method)
    {
        Match signature = Regex.Match(source, @"private void " + method + @"\([^)]*\)\s*\{");
        Assert.True(signature.Success, $"No encuentro {method}.");
        int depth = 1;
        int i = signature.Index + signature.Length;
        int start = i;
        for (; depth > 0; i++)
        {
            depth += source[i] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };
        }

        return source[start..(i - 1)];
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No encuentro Rodaje.slnx subiendo desde las pruebas.");
    }
}
