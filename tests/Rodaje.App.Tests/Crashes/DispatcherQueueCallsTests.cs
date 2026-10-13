using System.Text.RegularExpressions;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 106: un delegado puesto con <c>DispatcherQueue.TryEnqueue</c> no pasa por <c>GuardedSynchronizationContext</c>; si
/// lanza, WinUI cierra la app con 0xC000027B y sin pila. Toda la app pone su trabajo en la cola con
/// <c>DispatcherQueueLater.RunAfterEvent</c> o <c>RunLater</c>, que lo envuelven con el registro de fallos
/// (<c>UiDeferral</c>). Se vigila en el código fuente.
/// </summary>
public partial class DispatcherQueueCallsTests
{
    // El único que llama a TryEnqueue: el envoltorio de la cola de WinUI, que le pasa la acción ya envuelta por UiDeferral
    // (UiDeferral no conoce la cola de WinUI; recibe la función que encola).
    private static readonly string[] Allowed =
    [
        Path.Combine("Views", "Inspector", "DispatcherQueueLater.cs"),
    ];

    // Los desplegables que editaban dentro de su propio SelectionChanged: el manejador deja la edición para después.
    private static readonly (string File, string Handler)[] DeferredHandlers =
    [
        (Path.Combine("Views", "Inspector", "Filters", "FiltersPanel.xaml.cs"), "void OnAddSelected("),
        (Path.Combine("Views", "Inspector", "Properties", "PropertiesPanel.xaml.cs"), "void OnPresetSelected("),
        (Path.Combine("Views", "Inspector", "Properties", "ParameterRow.xaml.cs"), "void OnEaseChanged("),
        (Path.Combine("Views", "Mixer", "InsertEditor.cs"), "_presets.SelectionChanged +="),
        (Path.Combine("Views", "Mixer", "InsertEditor.cs"), "_division.SelectionChanged +="),
    ];

    [GeneratedRegex(@"\bTryEnqueue\s*\(")]
    private static partial Regex TryEnqueueCall();

    [Fact]
    public void LaApp_NoPoneNadaEnLaColaSinRegistro()
    {
        string source = AppSource();
        var found = new List<string>();
        foreach (string file in SourceFiles(source))
        {
            string relative = Path.GetRelativePath(source, file);
            if (Allowed.Contains(relative, StringComparer.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsOffending(lines[i]))
                {
                    found.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.Empty(found);
    }

    [Fact]
    public void LaVigilanciaDetectaLasLlamadasDirectas()
    {
        Assert.True(IsOffending("        DispatcherQueue.TryEnqueue(Refresh);"));
        Assert.True(IsOffending("_ = dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () => Scan());"));
        Assert.True(IsOffending("if (!queue.TryEnqueue (() => x()))"));
        Assert.False(IsOffending("        DispatcherQueue.RunLater(Refresh);"));
        Assert.False(IsOffending("        DispatcherQueue.RunAfterEvent(Refresh);"));
        Assert.False(IsOffending("    // TryEnqueue(…) cerraba la app sin pila"));
        Assert.False(IsOffending("/// <c>DispatcherQueue.TryEnqueue(…)</c> no pasa por el registro"));
    }

    [Fact]
    public void LaVigilanciaLeeLosFuentesDeLaApp()
    {
        // Si la ruta cambiara y no leyera nada, la vigilancia pasaría siempre.
        string source = AppSource();
        Assert.Contains(SourceFiles(source), f => f.EndsWith("ExportView.xaml.cs", StringComparison.Ordinal));
        foreach (string allowed in Allowed)
        {
            Assert.Matches(TryEnqueueCall(), File.ReadAllText(Path.Combine(source, allowed)));
        }
    }

    // Lo que edita o refresca, o toca el desplegable: fuera de la lambda diferida no puede estar.
    [GeneratedRegex(@"\.Execute\s*\(|\bRefresh\w*\s*\(|\bSetValue\s*\(|\?\.Invoke\s*\(|\.Selected(Index|Item|Value)\s*=(?!=)|\.Items\.(Add|Clear|Insert|Remove)")]
    private static partial Regex EditOrRefresh();

    // Las llamadas a métodos de un trozo de código (el nombre antes de «(»), sin las palabras clave.
    [GeneratedRegex(@"\b(?!(?:if|while|for|foreach|switch|return|using|lock|catch|nameof|typeof|when)\b)([A-Za-z_]\w*)\s*\(")]
    private static partial Regex CalledName();

    [Fact]
    public void LosDesplegablesQueEditan_LoHacenDespuesDeSuEvento()
    {
        string source = AppSource();
        var found = new List<string>();
        foreach ((string file, string handler) in DeferredHandlers)
        {
            string body = Body(File.ReadAllText(Path.Combine(source, file)), handler);
            if (DeferralProblem(body) is { } problem)
            {
                found.Add($"{file}: «{handler}» {problem}");
            }
        }

        Assert.Empty(found);
    }

    [Fact]
    public void LaVigilanciaDeLosDesplegables_DetectaEdicionesFueraDeLaLambda()
    {
        Assert.Null(DeferralProblem("""
            void OnAddSelected(object sender, SelectionChangedEventArgs e)
            {
                if (_updating || AddBox.SelectedIndex < 0 || AddBox.SelectedIndex == AddBox.Items.Count - 1) { return; }
                // Refresh() y la edición van después del evento.
                string type = _addable[AddBox.SelectedIndex].Type;
                DispatcherQueue.RunAfterEvent(() => AddEffect(type));
            }
            """));

        // Sin diferir nada.
        Assert.NotNull(DeferralProblem("void On(object s, object e) { services.Session.Execute(command); }"));
        // Difiere la edición pero refresca en línea, antes o después.
        Assert.NotNull(DeferralProblem("void On(object s, object e) { DispatcherQueue.RunAfterEvent(() => AddEffect(type)); Refresh(); }"));
        Assert.NotNull(DeferralProblem("void On(object s, object e) { RefreshList(); DispatcherQueue.RunAfterEvent(() => AddEffect(type)); }"));
        // Edita en línea y además difiere otra cosa.
        Assert.NotNull(DeferralProblem("void On(object s, object e) { session.Execute(edit); DispatcherQueue.RunAfterEvent(() => Refresh()); }"));
        Assert.NotNull(DeferralProblem("void On(object s, object e) { SetValue(\"x\", 1, \"y\"); DispatcherQueue.RunAfterEvent(() => SetValue(\"x\", 1, \"y\")); }"));
        // Llama en línea a lo mismo que difiere, aunque no esté en la lista.
        Assert.NotNull(DeferralProblem("void On(object s, object e) { AddEffect(type); DispatcherQueue.RunAfterEvent(() => AddEffect(type)); }"));
        // Toca el desplegable o avisa a quien escucha en línea.
        Assert.NotNull(DeferralProblem("void On(object s, object e) { AddBox.SelectedIndex = -1; DispatcherQueue.RunAfterEvent(() => AddEffect(type)); }"));
        Assert.NotNull(DeferralProblem("void On(object s, object e) { Chosen?.Invoke(this, ease); DispatcherQueue.RunAfterEvent(() => Chosen?.Invoke(this, ease)); }"));
        // La lambda diferida no hace nada.
        Assert.NotNull(DeferralProblem("void On(object s, object e) { AddEffect(type); DispatcherQueue.RunAfterEvent(() => { }); }"));
    }

    /// <summary>
    /// Qué falla en el manejador <paramref name="body"/>, o nulo: deja su edición en una lambda de <c>RunAfterEvent</c> y,
    /// fuera de ella, no edita, no refresca, no toca el desplegable ni llama a nada de lo que la lambda hace.
    /// </summary>
    private static string? DeferralProblem(string body)
    {
        string code = WithoutComments(body);
        const string Deferral = ".RunAfterEvent(";
        int call = code.IndexOf(Deferral, StringComparison.Ordinal);
        if (call < 0)
        {
            return "debe dejar la edición para después del evento.";
        }

        int open = call + Deferral.Length - 1;
        int close = Closing(code, open);
        string deferred = code[(open + 1)..close];
        string outside = code[..call] + code[(close + 1)..];
        string[] deferredCalls = [.. CalledName().Matches(deferred).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];
        if (deferredCalls.Length == 0)
        {
            return "difiere una lambda que no llama a nada.";
        }

        if (EditOrRefresh().Match(outside) is { Success: true } inline)
        {
            return $"edita o refresca fuera de la lambda diferida («{inline.Value}»).";
        }

        string? repeated = CalledName().Matches(outside).Select(m => m.Groups[1].Value).FirstOrDefault(n => deferredCalls.Contains(n, StringComparer.Ordinal));
        return repeated is null ? null : $"llama a «{repeated}» también fuera de la lambda diferida.";
    }

    /// <summary>El índice del paréntesis que cierra el que abre en <paramref name="open"/>.</summary>
    private static int Closing(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            depth += text[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Paréntesis sin cerrar.");
    }

    private static string WithoutComments(string code) =>
        string.Join('\n', code.Split('\n').Select(line => line.IndexOf("//", StringComparison.Ordinal) is int at and >= 0 ? line[..at] : line));

    [Fact]
    public void ExportarPoneAlDiaCadaDesplegableAlCerrarse()
    {
        string view = Path.Combine(AppSource(), "Views", "Export");
        string xaml = File.ReadAllText(Path.Combine(view, "ExportView.xaml"));
        int boxes = Regex.Count(xaml, "<ComboBox x:Name=");
        Assert.True(boxes > 0);
        Assert.Equal(boxes, Regex.Count(xaml, "<ComboBox x:Name=\"\\w+\"[^>]*DropDownClosed=\"OnDropDownClosed\""));
        Assert.Contains("void OnDropDownClosed(object sender, object e) => QueueRefresh();", File.ReadAllText(Path.Combine(view, "ExportView.xaml.cs")),
            StringComparison.Ordinal);
    }

    private static bool IsOffending(string rawLine)
    {
        string line = rawLine.Trim();
        return !line.StartsWith("//", StringComparison.Ordinal) && TryEnqueueCall().IsMatch(line);
    }

    /// <summary>Desde <paramref name="start"/> hasta la llave que cierra el primer bloque que abre.</summary>
    private static string Body(string text, string start)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"No encuentro «{start}».");
        int open = text.IndexOf('{', from);
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            depth += text[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return text[from..(i + 1)];
            }
        }

        return text[from..];
    }

    private static IEnumerable<string> SourceFiles(string source)
    {
        string obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        string bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        return Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(obj, StringComparison.Ordinal) && !f.Contains(bin, StringComparison.Ordinal));
    }

    private static string AppSource()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return Path.Combine(dir.FullName, "src", "Rodaje.App");
            }
        }

        throw new InvalidOperationException("No encuentro Rodaje.slnx subiendo desde las pruebas.");
    }
}
