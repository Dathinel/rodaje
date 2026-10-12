using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Rodaje.Modules.Protocol;
using Xunit.Abstractions;

namespace Rodaje.Module.Vision.Tests;

/// <summary>
/// El módulo como proceso aparte, por el protocolo (lo que hace la app): <c>RODAJE_MODULO_VISION</c> apunta a otro exe,
/// por ejemplo el publicado con Native AOT.
/// </summary>
public class ProcessTests(ITestOutputHelper output)
{
    private static string Exe =>
        Environment.GetEnvironmentVariable("RODAJE_MODULO_VISION") is { Length: > 0 } exe ? exe : Path.Combine(AppContext.BaseDirectory, "Rodaje.Module.Vision.exe");

    private static async Task<ModuleProcess> StartAsync()
    {
        if (TestImages.Environment is { } environment)
        {
            Environment.SetEnvironmentVariable("RODAJE_ENTORNO", environment);
        }

        return await ModuleProcess.StartAsync(Exe);
    }

    private static JsonElement Parameters(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Copia los cuadros a una región y devuelve [entrada0, salida0, …] (o solo las entradas).</summary>
    private static List<SharedBufferRef> Frames(ModuleProcess module, List<byte[]> frames, int width, int height, bool withOutputs, out SharedMemoryRegion region)
    {
        long input = (long)width * height * 4, mask = (long)width * height;
        region = module.Client.CreateRegion(frames.Count * (input + (withOutputs ? mask : 0)));
        var buffers = new List<SharedBufferRef>();
        long offset = 0;
        foreach (byte[] frame in frames)
        {
            frame.CopyTo(region.GetSpan(offset, input));
            buffers.Add(region.Describe(offset, input, new VideoLayout(width, height, width * 4, PixelFormats.Bgra8)));
            offset += input;
            if (withOutputs)
            {
                buffers.Add(region.Describe(offset, mask, new VideoLayout(width, height, width, PixelFormats.Gray8)));
                offset += mask;
            }
        }

        return buffers;
    }

    [Fact]
    public async Task Removes_background_detects_and_reframes_through_the_protocol()
    {
        if (!TestImages.Ready || !File.Exists(Exe))
        {
            output.WriteLine(TestImages.Missing);
            return;
        }

        var watch = Stopwatch.StartNew();
        await using ModuleProcess module = await StartAsync();
        output.WriteLine($"Arranque y saludo: {watch.Elapsed.TotalMilliseconds:0} ms; funciones: {string.Join(", ", module.Client.Module.Functions.Select(f => f.Id))}");

        var cases = MattingTests.Cases().Where(c => c.Name.StartsWith("retrato", StringComparison.Ordinal)).Take(3).ToList();
        (int w, int h) = (cases[0].Width, cases[0].Height);
        List<SharedBufferRef> buffers = Frames(module, [.. cases.Select(c => c.Frame)], w, h, true, out SharedMemoryRegion region);
        watch.Restart();
        ModuleResult matte = await module.Client.InvokeAsync("quitar-fondo", Parameters("""{"sesion":"clip-1","reiniciar":true}"""), buffers);
        output.WriteLine($"quitar-fondo: {watch.Elapsed.TotalMilliseconds:0} ms con la carga del modelo; {matte.Value}");
        Assert.Equal(cases.Count, matte.Value!.Value.GetProperty("cuadros").GetInt32());
        for (int i = 0; i < cases.Count; i++)
        {
            SharedBufferRef mask = matte.Buffers[i];
            Assert.Equal(PixelFormats.Gray8, mask.Video!.PixelFormat);
            double iou = Scenes.Iou(region.GetSpan(mask), cases[i].Truth);
            output.WriteLine($"  {cases[i].Name}: IoU {iou:0.000}");
            Assert.True(iou >= 0.9);
        }

        List<SharedBufferRef> inputs = [.. buffers.Where((_, i) => i % 2 == 0)];
        ModuleResult people = await module.Client.InvokeAsync("detectar", Parameters("""{"objetivo":"personas"}"""), inputs);
        ModuleResult faces = await module.Client.InvokeAsync("detectar", Parameters("""{"objetivo":"rostros","dispositivo":"cpu"}"""), inputs);
        output.WriteLine($"detectar personas: {people.Value}");
        output.WriteLine($"detectar rostros (cpu): {faces.Value}");
        JsonElement frames = people.Value!.Value.GetProperty("cuadros");
        Assert.Equal(cases.Count, frames.GetArrayLength());
        Assert.All(frames.EnumerateArray(), f => Assert.Equal(1, f.GetProperty("cajas").GetArrayLength()));
        Assert.All(faces.Value!.Value.GetProperty("cuadros").EnumerateArray(), f => Assert.Equal(1, f.GetProperty("cajas").GetArrayLength()));

        // Con las caras: la caja de la persona (medio cuerpo) es más ancha que un recorte vertical y no cabría.
        string cuadros = string.Join(",", faces.Value.Value.GetProperty("cuadros").EnumerateArray().Select((f, i) => $$"""{"t":{{(i * 0.5).ToString(CultureInfo.InvariantCulture)}},"cajas":{{f.GetProperty("cajas").GetRawText()}}}"""));
        ModuleResult reframe = await module.Client.InvokeAsync("reencuadre",
            Parameters($$"""{"aspecto":0.5625,"fuente":{"ancho":{{w}},"alto":{{h}}},"cuadros":[{{cuadros}}]}"""), []);
        output.WriteLine($"reencuadre: {reframe.Value}");
        Assert.Equal(1.0, reframe.Value!.Value.GetProperty("dentro").GetDouble());
        Assert.True(reframe.Value.Value.GetProperty("keyframes").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Bad_calls_fail_without_closing_the_module()
    {
        if (!File.Exists(Exe))
        {
            return;
        }

        await using ModuleProcess module = await StartAsync();
        List<SharedBufferRef> one = Frames(module, [new byte[64 * 64 * 4]], 64, 64, false, out _);
        ModuleCallException error = await Assert.ThrowsAsync<ModuleCallException>(() => module.Client.InvokeAsync("quitar-fondo", null, one));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);
        error = await Assert.ThrowsAsync<ModuleCallException>(() => module.Client.InvokeAsync("detectar", Parameters("""{"objetivo":"gatos"}"""), one));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);
        error = await Assert.ThrowsAsync<ModuleCallException>(() => module.Client.InvokeAsync("reencuadre", Parameters("""{"aspecto":0}"""), []));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);

        ModuleResult empty = await module.Client.InvokeAsync("reencuadre",
            Parameters("""{"aspecto":0.5625,"fuente":{"ancho":1920,"alto":1080},"cuadros":[{"t":0,"cajas":[]}]}"""), []);
        Assert.Equal("""{"keyframes":[{"t":0,"x":0.5,"y":0.5,"scale":1}],"dentro":1}""", empty.Value!.Value.GetRawText());

        await module.Client.PingAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(module.Exited.IsCompleted);
    }

    /// <summary>
    /// Cifras de la RTX 3050 (DirectML), cada modelo en un proceso nuevo para medir su memoria de GPU: MODNet a 512 px
    /// (vista previa, objetivo ≥30 cuadros/s), BiRefNet_lite a 1024 px y el reencuadre de punta a punta (detección de
    /// personas a 10 cuadros/s de análisis + trayectoria, objetivo ≥4× tiempo real).
    /// </summary>
    [LongFact]
    [Trait("Categoria", "TiempoReal")]
    public async Task Speed_and_gpu_memory()
    {
        byte[] background = Scenes.MakeBackground(Scenes.Background.Blobs, 512, 512, 9);
        (byte[] portrait, _) = Scenes.Compose(background, 512, 512, TestImages.Portrait!, 30, 40, 470);

        await using (ModuleProcess module = await StartAsync())
        {
            List<SharedBufferRef> buffers = Frames(module, Enumerable.Repeat(portrait, 8).ToList(), 512, 512, true, out _);
            JsonElement parameters = Parameters("""{"sesion":"velocidad"}""");
            for (int i = 0; i < 3; i++)
            {
                await module.Client.InvokeAsync("quitar-fondo", parameters, buffers);
            }

            var watch = Stopwatch.StartNew();
            const int Calls = 15;
            ModuleResult last = null!;
            for (int i = 0; i < Calls; i++)
            {
                last = await module.Client.InvokeAsync("quitar-fondo", parameters, buffers);
            }

            double fps = Calls * 8 / watch.Elapsed.TotalSeconds;
            output.WriteLine($"MODNet 512×512 ({last.Value!.Value.GetProperty("dispositivo")}): {fps:0.0} cuadros/s por el protocolo, lotes de 8; GPU {Memory(module)}");
            Assert.True(fps >= 30);
        }

        await using (ModuleProcess module = await StartAsync())
        {
            byte[] big = Scenes.MakeBackground(Scenes.Background.Blobs, 1024, 1024, 9);
            (byte[] frame, _) = Scenes.Compose(big, 1024, 1024, TestImages.Portrait!, 60, 80, 940);
            List<SharedBufferRef> buffers = Frames(module, [frame], 1024, 1024, true, out _);
            JsonElement parameters = Parameters("""{"calidad":"alta"}""");
            await module.Client.InvokeAsync("quitar-fondo", parameters, buffers);
            var watch = Stopwatch.StartNew();
            ModuleResult last = null!;
            for (int i = 0; i < 3; i++)
            {
                last = await module.Client.InvokeAsync("quitar-fondo", parameters, buffers);
            }

            output.WriteLine($"BiRefNet_lite 1024×1024 ({last.Value!.Value.GetProperty("dispositivo")}): {watch.Elapsed.TotalSeconds / 3:0.00} s por cuadro; GPU {Memory(module)}");
        }

        await using (ModuleProcess module = await StartAsync())
        {
            const int Width = 960, Height = 540, Fps = 10, Seconds = 10;
            byte[] scene = Scenes.MakeBackground(Scenes.Background.Gradient, Width, Height, 4);
            var frames = new List<byte[]>();
            for (int i = 0; i < Fps * Seconds; i++)
            {
                frames.Add(Scenes.Compose(scene, Width, Height, TestImages.FullBody!, 100 + (i * 6), 30, 480).Frame);
            }

            var watch = Stopwatch.StartNew();
            List<SharedBufferRef> warm = Frames(module, [frames[0]], Width, Height, false, out SharedMemoryRegion warmRegion);
            await module.Client.InvokeAsync("detectar", Parameters("""{"objetivo":"personas"}"""), warm);
            warmRegion.Dispose();
            double load = watch.Elapsed.TotalSeconds;
            watch.Restart();
            var cuadros = new List<string>();
            for (int start = 0; start < frames.Count; start += 16)
            {
                List<SharedBufferRef> buffers = Frames(module, frames.Skip(start).Take(16).ToList(), Width, Height, false, out SharedMemoryRegion region);
                ModuleResult found = await module.Client.InvokeAsync("detectar", Parameters("""{"objetivo":"personas"}"""), buffers);
                cuadros.AddRange(found.Value!.Value.GetProperty("cuadros").EnumerateArray()
                    .Select((f, i) => $$"""{"t":{{((start + i) / (double)Fps).ToString(CultureInfo.InvariantCulture)}},"cajas":{{f.GetProperty("cajas").GetRawText()}}}"""));
                region.Dispose();
            }

            ModuleResult reframe = await module.Client.InvokeAsync("reencuadre",
                Parameters($$"""{"aspecto":0.5625,"fuente":{"ancho":{{Width}},"alto":{{Height}}},"cuadros":[{{string.Join(",", cuadros)}}]}"""), []);
            double elapsed = watch.Elapsed.TotalSeconds, factor = Seconds / elapsed;
            output.WriteLine($"Reencuadre de punta a punta, {Seconds} s a {Fps} cuadros/s de análisis: {elapsed:0.00} s, {factor:0.0}× tiempo real " +
                $"(más {load:0.00} s de la carga de RT-DETRv2 una vez por proceso: {Seconds / (elapsed + load):0.0}× con ella); " +
                $"{reframe.Value!.Value.GetProperty("keyframes").GetArrayLength()} keyframes, dentro {reframe.Value.Value.GetProperty("dentro")}; GPU {Memory(module)}");
            Assert.True(factor >= 4);
            Assert.True(reframe.Value.Value.GetProperty("dentro").GetDouble() >= 0.98);
        }
    }

    private static string Memory(ModuleProcess module) =>
        GpuMemory.Mb(module.ProcessId) is { } mb ? $"{mb.Dedicated:0} MB dedicados + {mb.Shared:0} MB compartidos" : "sin datos";
}

/// <summary>Prueba de hardware: solo con <c>RODAJE_PRUEBAS_LARGAS=1</c> y los modelos en <c>.entorno</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LongFactAttribute : FactAttribute
{
    public LongFactAttribute()
    {
        if (!TestImages.LongTests)
        {
            Skip = "Omitida: prueba de hardware (RODAJE_PRUEBAS_LARGAS=1).";
        }
        else if (!TestImages.Ready)
        {
            Skip = TestImages.Missing;
        }
    }
}
