using System.Text.RegularExpressions;

namespace Rodaje.Core.Tests.Architecture;

/// <summary>
/// Prueba de arquitectura sobre el texto de <c>src/Rodaje.App/Shell/MainWindow.xaml.cs</c> (tarea 216): al cerrar, la
/// ventana espera a los módulos con <c>ModuleManager.CloseOnExitAsync</c> (tarea 206, #207), que vuelve a
/// <c>CloseTimeout</c> o, si sigue matando el árbol de un módulo colgado, cuando termina de matarlo. Si vuelve el cableado
/// de antes (<c>_modules.DisposeAsync().AsTask().Wait(_modules.CloseTimeout)</c>), la ventana deja de esperar justo a
/// los <c>CloseTimeout</c> aunque el árbol siga vivo, y ni <see cref="AsyncSafetyTests"/> (que solo cuenta un
/// <c>.Wait(</c>) ni las pruebas de <c>ModuleManager</c> (que llaman a <c>CloseOnExitAsync</c> directamente) lo notan.
/// Lee el fuente sin comentarios ni cadenas, como <see cref="AsyncSafetyTests"/>.
/// </summary>
public sealed partial class ModuleCloseWiringTests
{
    private const string MainWindow = "src/Rodaje.App/Shell/MainWindow.xaml.cs";

    [Fact]
    public void The_window_closes_the_modules_with_CloseOnExitAsync()
    {
        string code = MainWindowCode();

        Assert.True(CloseOnExitWaitRegex().Count(code) == 1,
            $"{MainWindow} debe cerrar los módulos una vez con «_modules.CloseOnExitAsync().Wait()» (sin plazo propio: la cota la pone CloseOnExitAsync).");
    }

    [Fact]
    public void The_window_does_not_dispose_the_modules_with_its_own_wait()
    {
        string code = MainWindowCode();
        var problems = new List<string>();

        foreach (Match m in ModulesDisposeRegex().Matches(code))
        {
            problems.Add($"línea {Line(code, m.Index)}: «{m.Value}»: el cierre de los módulos es CloseOnExitAsync, no DisposeAsync ni Dispose.");
        }

        foreach (Match m in WaitWithArgumentRegex().Matches(code))
        {
            problems.Add($"línea {Line(code, m.Index)}: «.Wait(» con plazo propio: la espera la acota CloseOnExitAsync (CloseTimeout y matar el árbol).");
        }

        foreach (Match m in CloseTimeoutRegex().Matches(code))
        {
            problems.Add($"línea {Line(code, m.Index)}: la ventana no usa CloseTimeout: lo aplica CloseOnExitAsync.");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_check_catches_the_wiring_before_task_206()
    {
        string old = AsyncSafetyTests.Strip("""
            try
            {
                _modules.DisposeAsync().AsTask().Wait(_modules.CloseTimeout);
            }
            catch (Exception ex) { }
            """);

        Assert.Empty(CloseOnExitWaitRegex().Matches(old));
        Assert.Single(ModulesDisposeRegex().Matches(old));
        Assert.Single(WaitWithArgumentRegex().Matches(old));
        Assert.Single(CloseTimeoutRegex().Matches(old));

        string current = AsyncSafetyTests.Strip("_modules.CloseOnExitAsync().Wait(); // no _modules.DisposeAsync()");
        Assert.Single(CloseOnExitWaitRegex().Matches(current));
        Assert.Empty(ModulesDisposeRegex().Matches(current));
        Assert.Empty(WaitWithArgumentRegex().Matches(current));
    }

    private static string MainWindowCode()
    {
        string path = Path.Combine(AsyncSafetyTests.RepoRoot(), MainWindow);
        Assert.True(File.Exists(path), $"no se encontró {MainWindow}");
        return AsyncSafetyTests.Strip(File.ReadAllText(path));
    }

    private static int Line(string code, int index) => 1 + code.AsSpan(0, index).Count('\n');

    [GeneratedRegex(@"\b_modules\s*\.\s*CloseOnExitAsync\s*\(\s*\)\s*\.\s*Wait\s*\(\s*\)")]
    private static partial Regex CloseOnExitWaitRegex();

    [GeneratedRegex(@"\b_modules\s*\.\s*Dispose(Async)?\s*\(")]
    private static partial Regex ModulesDisposeRegex();

    [GeneratedRegex(@"\.\s*Wait\s*\(\s*[^\s)]")]
    private static partial Regex WaitWithArgumentRegex();

    [GeneratedRegex(@"\bCloseTimeout\b")]
    private static partial Regex CloseTimeoutRegex();
}
