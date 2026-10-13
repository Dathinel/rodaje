using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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

    /// <summary>Exportar rellena sus desplegables con <c>Fill</c> y refresca toda la vista en su <c>OnDropDownClosed</c>.</summary>
    private static readonly ComboSource.ClosedRefresh ExportClosed = new("Fill", "OnDropDownClosed");

    /// <summary>
    /// Las vistas cuyos <c>ComboBoxChoices.Show</c> no necesitan <c>retry:</c>, y cómo se comprueba su relevo: cada desplegable
    /// que rellenan engancha en su .xaml el <c>DropDownClosed</c> que refresca la vista (no basta con que el nombre aparezca en
    /// el .cs: quitar los atributos del .xaml debe fallar).
    /// </summary>
    private static readonly Dictionary<string, ComboSource.ClosedRefresh> WithoutRetry = new(StringComparer.Ordinal)
    {
        ["src/Rodaje.App/Views/Export/ExportView.xaml.cs"] = ExportClosed,
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
        // «() => …» o es el propio Refresh sin llamar. Sobre el código sin comentarios ni textos (ComboSource.Code).
        string? body = ComboSource.Body(ComboSource.Code(File.ReadAllText(Path.Combine(RepositoryRoot(), file))), method);
        Assert.True(body is not null, $"No encuentro {method}.");
        Assert.True(Regex.Count(body, @"\bDispatcherQueue\.Choose\(") == 1, $"{method} debe llamar una vez a DispatcherQueue.Choose.");
        Assert.DoesNotContain("RunAfterEvent", body, StringComparison.Ordinal);
        string calls = Regex.Replace(body, @"\(\)=>\w+\(", "()=>DEFERRED(");
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
        string code = ComboSource.Code(File.ReadAllText(Path.Combine(RepositoryRoot(), file)));
        Assert.True(ComboSource.RetryCalls(code).Any(c => !c.Conditional), $"{file} debe llamar a RetryWhenClosed (sin «?.»).");
        Assert.True(Regex.IsMatch(code, @"\.Defer\(|\bretry:"), $"{file} debe anotar lo pendiente con Defer( o retry:.");
    }

    [Theory]
    [MemberData(nameof(RetriedBoxes))]
    public void CadaDesplegable_RepiteAlCerrar_ConElMismoReintentoQueAnota(string file, string box)
    {
        string code = ComboSource.Code(File.ReadAllText(Path.Combine(RepositoryRoot(), file)));
        string? retry = ComboSource.RetryOf(code, box);
        Assert.True(retry is not null, $"{box} de {file} debe llamar a RetryWhenClosed (sin «?.»).");
        List<ComboSource.ShowCall> shows = [.. ComboSource.ShowCalls(code).Where(c => c.Box == box)];
        Assert.True(shows.Count > 0, $"No encuentro el ComboBoxChoices.Show de {box} en {file}.");
        foreach (ComboSource.ShowCall show in shows)
        {
            Assert.True(ComboSource.Guarded(code, show, retry!), $"El ComboBoxChoices.Show de {box} en {file} debe pasar «retry: {retry}» o ir tras «{retry}.Defer({box}.IsDropDownOpen)» en el mismo método.");
        }
    }

    [Fact]
    public void TodoShowDeLasVistas_PasaRetry_SalvoLaListaBlanca()
    {
        // Un desplegable nuevo sin relevo al cerrar falla aquí: o pasa retry: (con su RetryWhenClosed), o va tras Defer, o
        // entra en WithoutRetry y su .xaml engancha el DropDownClosed que refresca a cada desplegable que rellena.
        string root = RepositoryRoot();
        var missing = new List<string>();
        int count = 0;
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "src", "Rodaje.App", "Views"), "*.cs", SearchOption.AllDirectories))
        {
            string file = Path.GetRelativePath(root, path).Replace('\\', '/');
            string code = ComboSource.Code(File.ReadAllText(path));
            count += ComboSource.ShowCalls(code).Count;
            if (WithoutRetry.TryGetValue(file, out ComboSource.ClosedRefresh? closed))
            {
                string xaml = File.ReadAllText(path[..^".cs".Length]);
                missing.AddRange(ComboSource.ClosedRefreshProblems(code, xaml, closed).Select(p => $"{file}: {p}"));
                continue;
            }

            missing.AddRange(ComboSource.Missing(code).Select(box => $"{file}: {box}"));
        }

        Assert.True(count >= 12, $"Solo encuentro {count} llamadas a ComboBoxChoices.Show: ¿cambió su forma?");
        Assert.True(missing.Count == 0, "Sin relevo al cerrar la lista: " + string.Join("; ", missing));
    }

    [Theory]
    [MemberData(nameof(BrokenSources))]
    public void ElAnalisis_RechazaLasFuentesSinRelevo(string name, string source)
    {
        Assert.True(ComboSource.Missing(ComboSource.Code(source)).Count > 0, $"«{name}» debería fallar.");
    }

    [Theory]
    [MemberData(nameof(ValidSources))]
    public void ElAnalisis_AceptaLasFuentesConRelevo(string name, string source)
    {
        string code = ComboSource.Code(source);
        Assert.True(ComboSource.ShowCalls(code).Count > 0, $"«{name}»: no encuentra el Show.");
        Assert.True(ComboSource.Missing(code).Count == 0, $"«{name}» debería pasar: " + string.Join("; ", ComboSource.Missing(code)));
    }

    [Theory]
    [MemberData(nameof(ClosedRefreshSources))]
    public void LaListaBlanca_CompruebaElXaml(string name, string source, string xaml, bool valid)
    {
        List<string> problems = ComboSource.ClosedRefreshProblems(ComboSource.Code(source), xaml, ExportClosed);
        Assert.True(valid == (problems.Count == 0), $"«{name}»: " + (valid ? string.Join("; ", problems) : "debería fallar."));
    }

    public static TheoryData<string, string> BrokenSources => Samples(ComboSourceSamples.Broken);

    public static TheoryData<string, string> ValidSources => Samples(ComboSourceSamples.Valid);

    public static TheoryData<string, string, string, bool> ClosedRefreshSources
    {
        get
        {
            var data = new TheoryData<string, string, string, bool>();
            foreach ((string name, string source, string xaml, bool valid) in ComboSourceSamples.ClosedRefresh)
            {
                data.Add(name, source, xaml, valid);
            }

            return data;
        }
    }

    private static TheoryData<string, string> Samples(IEnumerable<(string Name, string Source)> samples)
    {
        var data = new TheoryData<string, string>();
        foreach ((string name, string source) in samples)
        {
            data.Add(name, source);
        }

        return data;
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

/// <summary>
/// Lectura del código de las vistas para <see cref="ComboDeferralTests"/>: sin comentarios ni contenido de textos y con los
/// espacios normalizados, para que un comentario, un texto, un <c>this.</c>, un salto de línea o los argumentos con nombre no
/// engañen a la prueba. Se ejercita con fuentes sintéticas (<see cref="ComboSourceSamples"/>).
/// </summary>
internal static class ComboSource
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] ShowParameters = ["box", "shown", "next", "selected", "item", "retry"];
    private static readonly string[] RetryParameters = ["retry", "refresh"];
    private static readonly string[] StaticRetryParameters = ["box", "retry", "refresh"];

    /// <summary>Una llamada a <c>ComboBoxChoices.Show</c>: dónde empieza, el desplegable y el reintento (o nulo).</summary>
    internal sealed record ShowCall(int Index, string Box, string? Retry);

    /// <summary>Una llamada a <c>RetryWhenClosed</c>; <paramref name="Conditional"/> si es con <c>?.</c> (puede no hacerse).</summary>
    internal sealed record RetryCall(string Box, string Retry, bool Conditional);

    /// <summary>
    /// Una vista sin <c>retry:</c>: todos sus <c>ComboBoxChoices.Show</c> van dentro de <paramref name="Helper"/>, y cada
    /// desplegable que este rellena engancha en el .xaml <paramref name="Handler"/>, que refresca la vista.
    /// </summary>
    internal sealed record ClosedRefresh(string Helper, string Handler);

    /// <summary>
    /// El código sin comentarios, con cada texto o carácter reducido a <c>""</c>, los espacios en blanco reducidos a uno y
    /// sin espacios junto a la puntuación (<c>a . b ( c )</c> queda <c>a.b(c)</c>).
    /// </summary>
    public static string Code(string source)
    {
        var code = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                code.Append(' ');
            }
            else if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                code.Append(' ');
            }
            else if (LiteralEnd(source, i) is int end && end > i)
            {
                code.Append(" \"\" ");
                i = end;
            }
            else
            {
                code.Append(source[i]);
                i++;
            }
        }

        string spaced = Regex.Replace(code.ToString(), @"\s+", " ");
        return Regex.Replace(spaced, @" ?([^\w ]) ?", "$1").Trim();
    }

    /// <summary>Si en <paramref name="i"/> empieza un texto o un carácter, dónde acaba; si no, <paramref name="i"/>.</summary>
    private static int LiteralEnd(string s, int i)
    {
        if (s[i] == '\'' && (i == 0 || !(char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_')))
        {
            int k = i + 1;
            while (k < s.Length && s[k] != '\'')
            {
                k += s[k] == '\\' ? 2 : 1;
            }

            return Math.Min(k + 1, s.Length);
        }

        int j = i;
        bool verbatim = false;
        int dollars = 0;
        while (j < s.Length && (s[j] == '$' || s[j] == '@'))
        {
            verbatim |= s[j] == '@';
            dollars += s[j] == '$' ? 1 : 0;
            j++;
        }

        if (j >= s.Length || s[j] != '"' || (j > i && i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_')))
        {
            return i;
        }

        int quotes = 0;
        while (j + quotes < s.Length && s[j + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            // Texto en bruto («"""…"""»): acaba en la misma tira de comillas.
            int end = s.IndexOf(new string('"', quotes), j + quotes, StringComparison.Ordinal);
            return end < 0 ? s.Length : end + quotes;
        }

        int k2 = j + 1;
        while (k2 < s.Length)
        {
            char c = s[k2];
            if (verbatim && c == '"' && k2 + 1 < s.Length && s[k2 + 1] == '"')
            {
                k2 += 2;
            }
            else if (c == '"')
            {
                return k2 + 1;
            }
            else if (!verbatim && c == '\\')
            {
                k2 += 2;
            }
            else if (dollars > 0 && c == '{' && k2 + 1 < s.Length && s[k2 + 1] == '{')
            {
                k2 += 2;
            }
            else if (dollars > 0 && c == '{')
            {
                // Hueco de interpolación: puede llevar textos dentro.
                int depth = 1;
                k2++;
                while (k2 < s.Length && depth > 0)
                {
                    int inner = LiteralEnd(s, k2);
                    if (inner > k2)
                    {
                        k2 = inner;
                        continue;
                    }

                    depth += s[k2] switch
                    {
                        '{' => 1,
                        '}' => -1,
                        _ => 0,
                    };
                    k2++;
                }
            }
            else
            {
                k2++;
            }
        }

        return s.Length;
    }

    /// <summary>Cada <c>ComboBoxChoices.Show(</c> del código (ver <see cref="Code"/>), con sus argumentos por posición o nombre.</summary>
    public static List<ShowCall> ShowCalls(string code)
    {
        var calls = new List<ShowCall>();
        foreach (Match call in Regex.Matches(code, @"\bComboBoxChoices\.Show(?:<[^()]*>)?\("))
        {
            Dictionary<string, string> arguments = Arguments(code, call.Index + call.Length, ShowParameters);
            calls.Add(new ShowCall(call.Index, Name(arguments.GetValueOrDefault("box")), arguments.TryGetValue("retry", out string? retry) ? Name(retry) : null));
        }

        return calls;
    }

    /// <summary>Cada <c>RetryWhenClosed</c> del código: como extensión (<c>caja.RetryWhenClosed(…)</c>) o estático.</summary>
    public static List<RetryCall> RetryCalls(string code)
    {
        var calls = new List<RetryCall>();
        foreach (Match call in Regex.Matches(code, @"(?<![\w.])(?:this\.)?(\w+)(\?|!)?\.RetryWhenClosed\("))
        {
            int start = call.Index + call.Length;
            if (call.Groups[1].Value == "DispatcherQueueLater")
            {
                Dictionary<string, string> both = Arguments(code, start, StaticRetryParameters);
                calls.Add(new RetryCall(Name(both.GetValueOrDefault("box")), Name(both.GetValueOrDefault("retry")), false));
                continue;
            }

            Dictionary<string, string> arguments = Arguments(code, start, RetryParameters);
            calls.Add(new RetryCall(call.Groups[1].Value, Name(arguments.GetValueOrDefault("retry")), call.Groups[2].Value == "?"));
        }

        return calls;
    }

    /// <summary>El reintento con que <paramref name="box"/> llama siempre (sin <c>?.</c>) a <c>RetryWhenClosed</c>, o nulo.</summary>
    public static string? RetryOf(string code, string box) =>
        RetryCalls(code).FirstOrDefault(c => c.Box == box && !c.Conditional && c.Retry.Length > 0)?.Retry;

    /// <summary>
    /// Si <paramref name="call"/> pasa <paramref name="retry"/> como reintento, o va tras
    /// <c><paramref name="retry"/>.Defer(caja.IsDropDownOpen)</c> en el mismo bloque o uno que lo contiene (no en otro método).
    /// </summary>
    public static bool Guarded(string code, ShowCall call, string retry)
    {
        if (call.Retry == retry)
        {
            return true;
        }

        string defer = @"(?<![\w.])(?:this\.)?" + Regex.Escape(retry) + @"\.Defer\((?:isDropDownOpen:)?(?:this\.)?"
            + Regex.Escape(call.Box) + @"\.IsDropDownOpen\)";
        foreach (Match match in Regex.Matches(code[..call.Index], defer))
        {
            int depth = 0;
            bool left = false;
            for (int i = match.Index; i < call.Index && !left; i++)
            {
                depth += code[i] switch
                {
                    '{' => 1,
                    '}' => -1,
                    _ => 0,
                };
                left = depth < 0;
            }

            if (!left)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Los desplegables cuyo <c>ComboBoxChoices.Show</c> no tiene relevo al cerrar la lista.</summary>
    public static List<string> Missing(string code) =>
        [.. ShowCalls(code)
            .Where(c => RetryOf(code, c.Box) is not { } retry || !Guarded(code, c, retry))
            .Select(c => c.Box.Length > 0 ? c.Box : "(desplegable sin nombre)")];

    /// <summary>Lo que no cumple <paramref name="closed"/> en una vista de la lista blanca (vacío si todo está bien).</summary>
    public static List<string> ClosedRefreshProblems(string code, string xaml, ClosedRefresh closed)
    {
        var problems = new List<string>();
        if (Body(code, closed.Handler) is not { } handler || !Regex.IsMatch(handler, @"\b(?:QueueRefresh|Refresh)\("))
        {
            problems.Add($"{closed.Handler} debe existir y refrescar la vista");
        }

        if (BodySpan(code, closed.Helper) is not { } helper)
        {
            problems.Add($"no encuentro {closed.Helper}");
            return problems;
        }

        foreach (ShowCall call in ShowCalls(code).Where(c => c.Index < helper.Start || c.Index >= helper.End))
        {
            problems.Add($"el ComboBoxChoices.Show de {call.Box} no va dentro de {closed.Helper}");
        }

        XNamespace x = XamlNamespace;
        var closedBy = XDocument.Parse(xaml).Descendants()
            .Where(e => e.Name.LocalName == "ComboBox" && e.Attribute(x + "Name") is not null)
            .ToDictionary(e => e.Attribute(x + "Name")!.Value, e => e.Attribute("DropDownClosed")?.Value, StringComparer.Ordinal);
        var filled = Regex.Matches(code, @"(?<![\w.])" + Regex.Escape(closed.Helper) + @"\((?:this\.)?(\w+),").Select(m => m.Groups[1].Value).ToList();
        if (filled.Count == 0)
        {
            problems.Add($"nadie llama a {closed.Helper}");
        }

        foreach (string box in filled.Distinct(StringComparer.Ordinal))
        {
            if (!closedBy.TryGetValue(box, out string? handlerName) || handlerName != closed.Handler)
            {
                problems.Add($"{box} debe tener DropDownClosed=\"{closed.Handler}\" en el .xaml");
            }
        }

        return problems;
    }

    /// <summary>El cuerpo del método <c>void <paramref name="method"/>(…)</c>, con llaves o con <c>=&gt;</c>, o nulo.</summary>
    public static string? Body(string code, string method) => BodySpan(code, method) is { } span ? code[span.Start..span.End] : null;

    private static (int Start, int End)? BodySpan(string code, string method)
    {
        Match signature = Regex.Match(code, @"\bvoid " + Regex.Escape(method) + @"\(");
        if (!signature.Success)
        {
            return null;
        }

        int i = Close(code, signature.Index + signature.Length, '(', ')');
        if (code.AsSpan(i).StartsWith("=>", StringComparison.Ordinal))
        {
            int end = code.IndexOf(';', i);
            return end < 0 ? null : (i + 2, end);
        }

        return i < code.Length && code[i] == '{' ? (i + 1, Close(code, i + 1, '{', '}') - 1) : null;
    }

    /// <summary>Tras la apertura en <paramref name="start"/> − 1, la posición siguiente a su cierre.</summary>
    private static int Close(string code, int start, char open, char close)
    {
        int depth = 1;
        int i = start;
        for (; i < code.Length && depth > 0; i++)
        {
            depth += code[i] == open ? 1 : code[i] == close ? -1 : 0;
        }

        return i;
    }

    /// <summary>Los argumentos de la llamada que empieza en <paramref name="start"/>, por nombre de parámetro.</summary>
    private static Dictionary<string, string> Arguments(string code, int start, string[] parameters)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        int depth = 0;
        int position = 0;
        int from = start;
        for (int i = start; i < code.Length; i++)
        {
            char c = code[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (depth > 0 && c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (depth == 0 && c is ',' or ')')
            {
                string argument = code[from..i];
                Match named = Regex.Match(argument, @"^(\w+):(?!:)(.*)$");
                if (named.Success)
                {
                    arguments[named.Groups[1].Value] = named.Groups[2].Value;
                }
                else if (position < parameters.Length && argument.Length > 0)
                {
                    arguments[parameters[position]] = argument;
                }

                position++;
                from = i + 1;
                if (c == ')')
                {
                    break;
                }
            }
        }

        return arguments;
    }

    /// <summary>El campo de un argumento (<c>this.x</c> o <c>x!</c> quedan <c>x</c>); otra expresión queda tal cual.</summary>
    private static string Name(string? argument)
    {
        string name = argument ?? "";
        name = name.StartsWith("this.", StringComparison.Ordinal) ? name["this.".Length..] : name;
        return name.TrimEnd('!');
    }
}

/// <summary>Fuentes sintéticas para <see cref="ComboSource"/>: las rotas deben fallar y las válidas, pasar.</summary>
internal static class ComboSourceSamples
{
    public static readonly (string Name, string Source)[] Broken =
    [
        ("sin relevo", """
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0); }
            """),
        ("RetryWhenClosed solo en un comentario", """
            void Attach() { // Box.RetryWhenClosed(_r, Refresh);
            }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0, retry: _r); }
            """),
        ("RetryWhenClosed en un comentario de bloque", """
            void Attach() { /* Box.RetryWhenClosed(_r, Refresh); */ }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0, retry: _r); }
            """),
        ("RetryWhenClosed dentro de un texto", """
            void Attach() { Log("Box.RetryWhenClosed(_r, Refresh);"); }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0, retry: _r); }
            """),
        ("RetryWhenClosed con ?.", """
            void Attach() { Box?.RetryWhenClosed(_r, Refresh); }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0, retry: _r); }
            """),
        ("otro reintento", """
            void Attach() { Box.RetryWhenClosed(_other, Refresh); }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0, retry: _r); }
            """),
        ("RetryWhenClosed de otro desplegable", """
            void Attach() { OtherBox.RetryWhenClosed(_r, Refresh); }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0, retry: _r); }
            """),
        ("RetryWhenClosed del desplegable de otro objeto", """
            void Attach() { _other.Box.RetryWhenClosed(_r, Refresh); }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0, retry: _r); }
            """),
        ("retry: en un comentario de los argumentos", """
            void Attach() { Box.RetryWhenClosed(_r, Refresh); }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0 /* , retry: _r */); }
            """),
        ("retry: en un texto de los argumentos", """
            void Attach() { Box.RetryWhenClosed(_r, Refresh); }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0, i => "retry: _r"); }
            """),
        ("Defer en otro método", """
            void Attach() { Box.RetryWhenClosed(_r, Refresh); }
            void Other() { if (_r.Defer(Box.IsDropDownOpen)) { return; } }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0); }
            """),
        ("Defer después del Show", """
            void Attach() { Box.RetryWhenClosed(_r, Refresh); }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0); if (_r.Defer(Box.IsDropDownOpen)) { return; } }
            """),
        ("Defer de otro desplegable", """
            void Attach() { Box.RetryWhenClosed(_r, Refresh); }
            void Refresh() { if (_r.Defer(OtherBox.IsDropDownOpen)) { return; } _a = ComboBoxChoices.Show(Box, _a, next, 0); }
            """),
        ("Defer en un comentario", """
            void Attach() { Box.RetryWhenClosed(_r, Refresh); }
            void Refresh() { // if (_r.Defer(Box.IsDropDownOpen)) return;
                _a = ComboBoxChoices.Show(Box, _a, next, 0); }
            """),
        ("dos desplegables y uno sin relevo", """
            void Attach() { Box.RetryWhenClosed(_r, Refresh); }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0, retry: _r); _b = ComboBoxChoices.Show(OtherBox, _b, next, 0, retry: _r); }
            """),
    ];

    public static readonly (string Name, string Source)[] Valid =
    [
        ("retry: con su RetryWhenClosed", """
            void Attach() { Box.RetryWhenClosed(_r, Refresh); }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0, retry: _r); }
            """),
        ("this., saltos de línea y comentarios por medio", """
            void Attach()
            {
                this.Box // el de tipos
                    .RetryWhenClosed(
                        this._r, Refresh);
            }
            void Refresh()
            {
                _a = ComboBoxChoices
                    .Show(this.Box, _a, next, 0,
                        retry: this._r); // al cerrar
            }
            """),
        ("argumentos con nombre en otro orden", """
            void Attach() { Box.RetryWhenClosed(refresh: Refresh, retry: _r); }
            void Refresh() { _a = ComboBoxChoices.Show(selected: 0, next: next, retry: _r, shown: _a, box: Box); }
            """),
        ("RetryWhenClosed estático", """
            void Attach() { DispatcherQueueLater.RetryWhenClosed(Box, _r, Refresh); }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0, retry: _r); }
            """),
        ("reintento por posición", """
            void Attach() { Box.RetryWhenClosed(_r, Refresh); }
            void Refresh() { _a = ComboBoxChoices.Show(Box, _a, next, 0, null, _r); }
            """),
        ("con ! en vez de ?.", """
            void Attach() { _box!.RetryWhenClosed(_r, Refresh); }
            void Refresh() { _a = ComboBoxChoices.Show(_box, _a, next, 0, retry: _r); }
            """),
        ("tras Defer y return", """
            void Attach() { Box.RetryWhenClosed(_r, Refresh); }
            void Refresh()
            {
                if (_r.Defer(Box.IsDropDownOpen))
                {
                    return;
                }

                _a = ComboBoxChoices.Show(Box, _a, next, 0);
            }
            """),
        ("dentro del if de Defer", """
            void Attach() { Box.RetryWhenClosed(_r, Refresh); }
            void Refresh()
            {
                if (!this._r.Defer(isDropDownOpen: Box.IsDropDownOpen))
                {
                    try { _a = ComboBoxChoices.Show(Box, _a, next, 0, i => new Item { Tag = i }); } finally { }
                }
            }
            """),
        ("lambda con llaves y textos con paréntesis y //", """
            void Attach() { Box.RetryWhenClosed(_r, Refresh); }
            void Refresh()
            {
                _a = ComboBoxChoices.Show(Box, _a, next, 0, i =>
                {
                    string s = "x (y) // z, retry: _q";
                    string t = $"{Label(i, "(")} {{ }}";
                    char c = ')';
                    return new Item { Content = s + t + c };
                }, retry: _r);
            }
            """),
    ];

    private const string ExportCode = """
        private void OnDropDownClosed(object sender, object e) => QueueRefresh();
        private void Refresh() { Fill(RangeBox, a, 0); Fill(this.CodecBox, b, 1); }
        private static void Fill(ComboBox box, IEnumerable<string> labels, int selected)
        {
            _ = ComboBoxChoices.Show(box, shown, next, selected, i => new ComboBoxItem { Content = next.Labels[i] });
        }
        """;

    private const string ExportXaml = """
        <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <StackPanel>
                <ComboBox x:Name="RangeBox" SelectionChanged="OnRangeChanged" DropDownClosed="OnDropDownClosed" />
                <ComboBox x:Name="CodecBox" SelectionChanged="OnCodecChanged" DropDownClosed="OnDropDownClosed"
                          ToolTipService.ToolTip="Códec" />
            </StackPanel>
        </UserControl>
        """;

    public static readonly (string Name, string Source, string Xaml, bool Valid)[] ClosedRefresh =
    [
        ("como Exportar", ExportCode, ExportXaml, true),
        ("un desplegable sin DropDownClosed en el .xaml", ExportCode,
            ExportXaml.Replace("SelectionChanged=\"OnCodecChanged\" DropDownClosed=\"OnDropDownClosed\"", "SelectionChanged=\"OnCodecChanged\"", StringComparison.Ordinal), false),
        ("DropDownClosed solo en un comentario del .xaml", ExportCode,
            ExportXaml.Replace("""DropDownClosed="OnDropDownClosed" />""", """/> <!-- DropDownClosed="OnDropDownClosed" -->""", StringComparison.Ordinal), false),
        ("el manejador no refresca", ExportCode.Replace("=> QueueRefresh();", "{ } // QueueRefresh();", StringComparison.Ordinal), ExportXaml, false),
        ("un Show fuera del método que rellena", ExportCode + """
            private void Other() { _ = ComboBoxChoices.Show(OtherBox, s, n, 0); }
            """, ExportXaml, false),
        ("rellena un desplegable que no está en el .xaml", ExportCode.Replace("Fill(RangeBox", "Fill(_codeBox", StringComparison.Ordinal), ExportXaml, false),
    ];
}
