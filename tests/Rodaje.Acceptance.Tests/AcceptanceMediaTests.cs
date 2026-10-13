using System.Globalization;
using Rodaje.Acceptance.Tests.Support;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Acceptance.Tests;

/// <summary>
/// Genera (o toma de la caché) los medios largos de la aceptación, como casos aparte. Sin esto, la primera prueba de
/// deriva que pide un medio paga su codificación además de su medida: en el runner de la nocturna, el medio de 16 minutos
/// más la medida de «Montaje» pasaban los 15 minutos de --blame-hang-timeout (tarea 290). La nocturna corre estos casos
/// en un paso propio, antes de la solución, y guarda la caché entre corridas; las pruebas de deriva solo leen el archivo.
/// </summary>
public sealed class AcceptanceMediaTests(ITestOutputHelper output)
{
    /// <summary>El medio de <see cref="SpeedDriftTests"/> y <see cref="SpeedSeekTests"/>, con sus mismas condiciones.</summary>
    [FfmpegLibraryFact(VideoCodec.H264, Audio = AudioCodec.Aac, LongRunning = true)]
    public void Medio_de_velocidad_de_16_minutos() => Prepare(SpeedMediaPresets.Speed720p16Min);

    /// <summary>El medio de <see cref="AvDriftTests"/>, con sus mismas condiciones.</summary>
    [FfmpegLibraryFact(VideoCodec.H264, VideoCodec.Hevc, Audio = AudioCodec.Aac, LongRunning = true)]
    public void Medio_de_deriva_de_10_minutos() => Prepare(TestMediaPresets.Drift720p10Min);

    private void Prepare(MediaSpec spec)
    {
        GeneratedMedia media = AcceptanceEnvironment.Generator.GetOrCreate(spec);

        var file = new FileInfo(media.Path);
        Assert.True(file.Exists, $"No quedó el archivo {media.Path}.");
        Assert.True(file.Length > 0, $"{media.Path} está vacío.");
        output.Report($"medios.{spec.Name}.segundos_de_generacion", $"{media.GenerationTime.TotalSeconds:0.0}");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{spec.Name}: {(media.FromCache ? "de la caché" : $"generado en {media.GenerationTime.TotalSeconds:0.0} s")} con {media.VideoEncoder}, " +
            $"{spec.FrameCount} cuadros, {file.Length / 1e6:0.0} MB, en {media.Path}."));
    }
}
