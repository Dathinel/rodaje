using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// El minivolcado de <see cref="CrashReporter.WriteNativeFatal"/> cuando se llama dos veces con el mismo informe fatal y
/// cuando el escritor falla sin código de error. Con un <see cref="CrashReporter.MinidumpWriter"/> falso.
/// </summary>
public class NativeFatalDumpTests
{
    [Fact]
    public unsafe void DosFallosNativos_ConElMismoInforme_SeQuedaElPrimerMinivolcado()
    {
        using var dir = new TempDirectory();
        int calls = 0;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog())
        {
            MinidumpWriter = (path, _) =>
            {
                calls++;
                File.WriteAllText(path, "MDMP" + calls);
                return 0;
            },
        };

        string? first = reporter.WriteNativeFatal(0xC0000005, null);
        string? second = reporter.WriteNativeFatal(0xC0000005, null);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, calls);
        string dump = Assert.Single(Directory.GetFiles(dir.Path, "*.dmp"));
        Assert.Equal(Path.ChangeExtension(first, ".dmp"), dump);
        Assert.Equal("MDMP1", File.ReadAllText(dump));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        string text = File.ReadAllText(first!);
        Assert.Contains("Minivolcado: " + ExpectedDumpPath.Of(dump) + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Contains("Minivolcado: ya había un minivolcado de este fallo", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no se pudo escribir", text, StringComparison.Ordinal);
    }

    [Fact]
    public unsafe void FalloNativo_DespuesDeUnFatalAdministrado_ConMinivolcadoYa_NoLoPisa()
    {
        using var dir = new TempDirectory();
        int calls = 0;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog())
        {
            MinidumpWriter = (path, _) =>
            {
                calls++;
                File.WriteAllText(path, "MDMP" + calls);
                return 0;
            },
        };

        string? report = reporter.Write(CrashKind.AppDomainUnhandled, "System.InvalidOperationException: fatal", fatal: true);
        Assert.NotNull(report);
        Assert.NotNull(reporter.WriteNativeFatal(0xC0000409, null));
        Assert.NotNull(reporter.WriteNativeFatal(0xC0000409, null));

        Assert.Equal(1, calls);
        Assert.Equal("MDMP1", File.ReadAllText(Path.ChangeExtension(report, ".dmp")));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public unsafe void FalloNativo_SinCodigoDeError_NoAnotaUnCodigoDeWin32Inventado()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog()) { MinidumpWriter = (_, _) => -1 };

        string? report = reporter.WriteNativeFatal(0xC0000005, null);

        string text = File.ReadAllText(report!);
        Assert.Contains("Minivolcado: no se pudo escribir (sin código de error).", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0xFFFFFFFF", text, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.dmp*"));
    }
}
