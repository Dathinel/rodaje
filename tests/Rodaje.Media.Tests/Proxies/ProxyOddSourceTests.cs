using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Media.Tests.Support;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests.Proxies;

/// <summary>
/// Proxies de originales de la vida real: un cuadro perdido a mitad (celular), un cuadro repetido (captura de pantalla),
/// el audio más largo que el video, MPEG-TS de cámara y la lista de edición de un recorte sin recodificar. Ningún
/// codificador daba proxy de los dos primeros: el promedio de cuadros por segundo del proxy no era el del original.
/// </summary>
public sealed class ProxyOddSourceTests(RealWorldMediaFixture media, ITestOutputHelper output) : IClassFixture<RealWorldMediaFixture>
{
    [ProxyTheory]
    [InlineData("cuadro perdido.mp4", "-vf select=not(eq(n\\,45)) -fps_mode passthrough -c:v mpeg4 -c:a aac")]
    [InlineData("cuadro perdido.mkv", "-vf select=not(eq(n\\,45)) -fps_mode passthrough -c:v mpeg4 -c:a aac")]
    [InlineData("cuadro repetido.mkv", "-vf settb=1/1000,setpts=(floor(N/2)*2/30+mod(N\\,2)*0.002)/TB -fps_mode passthrough -enc_time_base:v filter -c:v mpeg4 -c:a aac -shortest")]
    [InlineData("camara.ts", "-c:v mpeg2video -bf 2 -g 300 -c:a mp2 -shortest")]
    [InlineData("recortado.mp4", "-c:v mpeg4 -c:a aac -shortest -output_ts_offset -0.7 -avoid_negative_ts disabled")]
    public async Task Proxy_is_made_with_the_frames_of_the_source(string name, string codecs)
    {
        string source = media.Make(name, RealWorldMediaTests.Picture + " " + RealWorldMediaTests.Tone + " " + codecs);
        using var temp = new TempDirectory();
        using var generator = new ProxyGenerator(new ProxyStore(temp.Combine("proxies")), ProxyTestEnvironment.Executable!);
        MediaInfo original = MediaProbe.Probe(source);

        ProxyEntry entry = await generator.GenerateAsync(source).WaitAsync(TimeSpan.FromMinutes(2));

        // Matroska no declara cuántos cuadros hay: se cuentan decodificando.
        int frames = FfmpegTool.Default!.DecodeGrayFrames(source, 16, 16, static (_, _) => { });
        output.WriteLine($"{name}: {entry.Metadata.FrameCount} cuadros de {frames} ({original.Video!.FrameCount} según la sonda) con {entry.Metadata.Encoder}");
        Assert.Equal(frames, entry.Metadata.FrameCount);
    }
}
