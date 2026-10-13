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

    [Fact]
    public void LosDesplegablesQueEditan_LoHacenDespuesDeSuEvento()
    {
        string source = AppSource();
        foreach ((string file, string handler) in DeferredHandlers)
        {
            string body = Body(File.ReadAllText(Path.Combine(source, file)), handler);
            Assert.True(body.Contains(".RunAfterEvent(", StringComparison.Ordinal), $"{file}: «{handler}» debe dejar la edición para después del evento.");
        }
    }

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
