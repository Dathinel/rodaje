using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 239: el aviso de un error inesperado es un texto fijo en español con la ruta del informe; el mensaje de la
/// excepción (en inglés y técnico) solo va al informe.
/// </summary>
public class UnexpectedErrorNoticeTests
{
    private const string Version = "1.0.0+prueba";
    private const string Technical = "Object reference not set to an instance of an object.";

    [Fact]
    public void HiloDeLaInterfaz_ElAvisoNoTraeElMensajeTecnicoYSiLaRutaDelInforme()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, Version, new SessionLog());
        var shown = new List<string>();

        Assert.True(CrashRouting.OnDispatcherException(new InvalidOperationException(Technical), shown.Add, reporter.Write));

        string notice = Assert.Single(shown);
        string report = Assert.Single(Directory.GetFiles(dir.Path, "*.txt"));
        Assert.DoesNotContain(Technical, notice, StringComparison.Ordinal);
        Assert.Contains(report, notice, StringComparison.Ordinal);
        Assert.Contains(Technical, File.ReadAllText(report), StringComparison.Ordinal);
    }

    [Fact]
    public void Xaml_ElAvisoNoTraeElMensajeTecnico()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, Version, new SessionLog());
        var shown = new List<string>();

        Assert.True(CrashRouting.OnXamlUnhandled(new InvalidOperationException(Technical), Technical, shown.Add, reporter.Write, reporter.Write));

        string notice = Assert.Single(shown);
        Assert.DoesNotContain(Technical, notice, StringComparison.Ordinal);
        Assert.Contains(Assert.Single(Directory.GetFiles(dir.Path, "*.txt")), notice, StringComparison.Ordinal);
    }

    [Fact]
    public void SinInforme_SenalaLaCarpetaDeLosInformes()
    {
        Assert.Contains(UnexpectedErrorNotice.ReportsFolder, UnexpectedErrorNotice.Describe(null), StringComparison.Ordinal);
        Assert.Contains(UnexpectedErrorNotice.ReportsFolder, UnexpectedErrorNotice.Describe(" "), StringComparison.Ordinal);
    }
}
