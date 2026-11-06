using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>Un archivo de proyecto o de subtítulos hostil (enorme) se rechaza antes de leerlo entero a memoria.</summary>
public sealed class UntrustedFileSizeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rodaje-tamano-" + Guid.NewGuid().ToString("N"));

    public UntrustedFileSizeTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Oversized(string name, long length)
    {
        string path = Path.Combine(_folder, name);
        using var stream = new FileStream(path, FileMode.Create);
        stream.SetLength(length);
        return path;
    }

    [Fact]
    public void ProyectoMasGrandeQueElLimite_SeRechazaConMensajeEnEspanol()
    {
        string path = Oversized("enorme.rodaje", ProjectSerializer.MaxProjectFileBytes + 1);
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(path));
        Assert.Contains("enorme.rodaje", ex.Message, StringComparison.Ordinal);
        Assert.Contains("demasiado para ser un proyecto", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SubtitulosMasGrandesQueElLimite_SeRechazanConMensajeEnEspanol()
    {
        string path = Oversized("enorme.srt", SubtitleFiles.MaxFileBytes + 1);
        var ex = Assert.Throws<InvalidDataException>(() => SubtitleFiles.Read(path));
        Assert.Contains("demasiado para ser un archivo de subtítulos", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SubtitulosJustoEnElLimite_SeLeen()
    {
        string path = Path.Combine(_folder, "normal.srt");
        File.WriteAllText(path, "1\r\n00:00:01,000 --> 00:00:02,000\r\nHola\r\n\r\n");
        Assert.Single(SubtitleFiles.Read(path).Cues);
    }
}
