using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Revisión de #129: <see cref="CrashRouting.OnXamlUnhandled"/> solo avisa si el error no es una repetición, pero ninguna
/// prueba lo cubría (quitar la condición dejaba todo en verde). Una ráfaga de errores de XAML avisa una sola vez y cada
/// llamada queda atendida, con excepción o solo con el mensaje.
/// </summary>
public class CrashXamlBurstTests
{
    private const string Version = "1.0.0+prueba";

    [Fact]
    public void Xaml_DosErroresSeguidos_AvisaUnaVezYAmbosQuedanAtendidos()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, Version, new SessionLog());
        var shown = new List<string>();

        bool first = CrashRouting.OnXamlUnhandled(new InvalidOperationException("falla al medir"), "falla al medir", shown.Add, reporter.Write, reporter.Write);
        bool second = CrashRouting.OnXamlUnhandled(new InvalidOperationException("falla al medir"), "falla al medir", shown.Add, reporter.Write, reporter.Write);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(["falla al medir"], shown);
        string report = Assert.Single(Directory.GetFiles(dir.Path, "*.txt"));
        Assert.EndsWith("-recuperado.txt", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Xaml_DosErroresSeguidosSinExcepcion_AvisaUnaVezYAmbosQuedanAtendidos()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, Version, new SessionLog());
        var shown = new List<string>();

        bool first = CrashRouting.OnXamlUnhandled(null, "error de XAML sin excepción", shown.Add, reporter.Write, reporter.Write);
        bool second = CrashRouting.OnXamlUnhandled(null, "error de XAML sin excepción", shown.Add, reporter.Write, reporter.Write);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(["error de XAML sin excepción"], shown);
        Assert.Single(Directory.GetFiles(dir.Path, "*.txt"));
    }

    [Fact]
    public void Xaml_LaRepeticionLaDecideElInforme_NoElMensaje()
    {
        // Con informes falsos: la vía de la excepción y la del texto dicen «repetido» en la segunda llamada.
        var shown = new List<string>();
        int calls = 0;
        string? Report(CrashKind kind, Exception exception, bool fatal, out bool repeated)
        {
            repeated = calls++ > 0;
            return null;
        }

        string? ReportText(CrashKind kind, string? details, bool fatal, out bool repeated)
        {
            repeated = calls++ > 0;
            return null;
        }

        Assert.True(CrashRouting.OnXamlUnhandled(new InvalidOperationException("a"), null, shown.Add, Report, ReportText));
        Assert.True(CrashRouting.OnXamlUnhandled(null, "b", shown.Add, Report, ReportText));
        Assert.True(CrashRouting.OnXamlUnhandled(new InvalidOperationException("c"), null, shown.Add, Report, ReportText));

        Assert.Equal(["a"], shown);
        Assert.Equal(3, calls);
    }
}
