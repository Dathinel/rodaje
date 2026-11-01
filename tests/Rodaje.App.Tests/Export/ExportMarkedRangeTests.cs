using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;

namespace Rodaje.App.Tests.Export;

/// <summary>«Entre la entrada y la salida»: el rango que marcan I y O en la secuencia, como opción de Exportar.</summary>
public sealed class ExportMarkedRangeTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    [Fact]
    public void ConLasDosMarcas_EsLaSegundaOpcionYSeProponeSola()
    {
        Project project = ExportTestData.Project();

        IReadOnlyList<RangeChoice> ranges = ExportRanges.Choices(project.Sequences[0], ["b"], new SequenceMarks(S(3), S(7)));

        Assert.Equal(4, ranges.Count);
        Assert.Equal("Entre la entrada y la salida (0:03 – 0:07)", ranges[1].Label);
        Assert.Equal((3.0, 7.0), (ranges[1].Range!.Value.Start.ToSeconds(), ranges[1].Range!.Value.End.ToSeconds()));
        Assert.StartsWith("Lo seleccionado", ranges[2].Label, StringComparison.Ordinal);
        Assert.StartsWith("Región", ranges[3].Label, StringComparison.Ordinal);
        Assert.Equal(1, ExportRanges.DefaultIndex(ranges));
    }

    [Fact]
    public void SinMarcas_NoApareceYSeProponeTodaLaSecuencia()
    {
        Project project = ExportTestData.Project();

        IReadOnlyList<RangeChoice> ranges = ExportRanges.Choices(project.Sequences[0], []);

        Assert.DoesNotContain(ranges, r => r.Label.StartsWith(ExportRanges.MarksLabel, StringComparison.Ordinal));
        Assert.Equal(0, ExportRanges.DefaultIndex(ranges));
    }

    [Fact]
    public void UnaSolaMarca_CompletaConElPrincipioOElFinal()
    {
        Sequence sequence = ExportTestData.Project().Sequences[0];

        ExportRange? onlyIn = ExportRanges.MarkedRange(sequence, new SequenceMarks(S(4), null));
        ExportRange? onlyOut = ExportRanges.MarkedRange(sequence, new SequenceMarks(null, S(4)));

        Assert.Equal((4.0, 10.0), (onlyIn!.Value.Start.ToSeconds(), onlyIn.Value.End.ToSeconds()));
        Assert.Equal((0.0, 4.0), (onlyOut!.Value.Start.ToSeconds(), onlyOut.Value.End.ToSeconds()));
    }

    [Fact]
    public void UnTramoVacioOFueraDeLaSecuencia_NoSeOfrece()
    {
        Sequence sequence = ExportTestData.Project().Sequences[0];

        Assert.Null(ExportRanges.MarkedRange(sequence, new SequenceMarks(S(7), S(7))));
        Assert.Null(ExportRanges.MarkedRange(sequence, new SequenceMarks(S(12), S(15))));
        Assert.Null(ExportRanges.MarkedRange(sequence, SequenceMarks.None));

        // Una salida más allá del final se acota a la secuencia.
        ExportRange? clamped = ExportRanges.MarkedRange(sequence, new SequenceMarks(S(8), S(15)));
        Assert.Equal((8.0, 10.0), (clamped!.Value.Start.ToSeconds(), clamped.Value.End.ToSeconds()));
    }

    [Fact]
    public void ElModelo_ArrancaEnElRangoMarcadoYExportaSoloEseTramo()
    {
        using var dir = new TempDirectory();
        Project project = ExportTestData.Project();
        var model = new ExportDialogModel(new ExportPreferences { Folder = dir.Path }, project.Sequences[0], [], SimulatedEncoders.All, project.Name, null,
            new SequenceMarks(S(2), S(6)));

        Assert.Equal(1, model.RangeIndex);
        Assert.Equal(4.0, model.Duration.ToSeconds(), 6);
        ExportRequest request = model.CreateRequest(project.Clone(), "sec", dir.Path);
        Assert.Equal((2.0, 6.0), (request.Range!.Value.Start.ToSeconds(), request.Range.Value.End.ToSeconds()));
    }
}
