using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Rodaje.Module.Vision;
using Rodaje.Modules.Protocol;

ModuleInfo info = new("modulo-vision", "1.1.0", "Visión con IA",
[
    new FunctionInfo("quitar-fondo", "Quitar el fondo (MODNet; «alta»: BiRefNet_lite)", Inputs: ["video"], Outputs: ["video"]),
    new FunctionInfo("detectar", "Detectar rostros (YuNet) o personas (RT-DETRv2)", Inputs: ["video"]),
    new FunctionInfo("reencuadre", "Reencuadre automático que sigue al sujeto", ReportsProgress: false),
], Device: "directml, cpu");

using var models = new VisionModels(new ModelFiles());
var matting = new Matting(models);
var temporal = new TemporalFilter();
var faces = new FaceDetector(models);
var people = new PersonDetector(models);
ModuleHost host = new(info);

// quitar-fondo: [entrada0, salida0, entrada1, salida1, …] (hasta 8 pares). Entrada bgra8; salida gray8 del mismo tamaño
// (stride = ancho), 255 = sujeto. «calidad» normal|alta, «sesion» (estado temporal compartido), «reiniciar», «dispositivo».
// Desde 1.1, lo que decide el perfil de IA del editor: «lado» (de MODNet, 256–1024), «hilos» (de la CPU) y «paralelo»
// (cuadros a la vez en la CPU, 1–4).
host.Register("quitar-fondo", async (call, ct) =>
{
    int count = call.Buffers.Count;
    if (count is < 2 or > 16 || count % 2 != 0)
    {
        throw Invalid("«quitar-fondo» necesita de 1 a 8 pares [entrada bgra8, salida gray8].");
    }

    var outputs = new SharedBufferRef[count / 2];
    for (int i = 0; i < count; i += 2)
    {
        VideoLayout input = Bgra(call.Buffers[i], "quitar-fondo");
        SharedBufferRef target = call.Buffers[i + 1];
        if (target.Length < (long)input.Width * input.Height
            || (target.Video is { } v && (v.PixelFormat != PixelFormats.Gray8 || v.Width != input.Width || v.Height != input.Height || v.Stride != v.Width)))
        {
            throw Invalid("La salida de «quitar-fondo» es gray8 del mismo ancho y alto que la entrada, con stride = ancho.");
        }

        outputs[i / 2] = target with { Length = (long)input.Width * input.Height, Video = new VideoLayout(input.Width, input.Height, input.Width, PixelFormats.Gray8), Audio = null };
    }

    bool high = Text(call.Parameters, "calidad") switch
    {
        null or "normal" => false,
        "alta" => true,
        _ => throw Invalid("«calidad» es «normal» o «alta»."),
    };
    ComputeDevice device = Device(call.Parameters);
    string? session = Text(call.Parameters, "sesion");
    bool reset = Flag(call.Parameters, "reiniciar");
    int side = Int(call.Parameters, "lado") is { } l
        ? (l is >= Matting.MinSide and <= Matting.MaxSide ? l : throw Invalid($"«lado» va de {Matting.MinSide} a {Matting.MaxSide}."))
        : Matting.DefaultSide;
    models.CpuThreads = Math.Clamp(Int(call.Parameters, "hilos") ?? 0, 0, 64);
    // DirectML serializa la sesión: varios cuadros a la vez solo en la CPU.
    int parallel = device == ComputeDevice.Cpu ? Math.Clamp(Int(call.Parameters, "paralelo") ?? 1, 1, 4) : 1;
    // En cadena: la CPU prepara los cuadros por adelantado y estabiliza/escribe el anterior mientras la GPU corre el actual.
    // Todo el trabajo que bloquea (preparar, inferir con ONNX Runtime, escribir) va en hilos propios y no en el pool: eran
    // hasta 8 preparaciones y 4 inferencias por llamada, y 4 llamadas a la vez; con la CPU llena el pool no sumaba hilos y
    // el bucle de ModuleHost no contestaba el «ping» ni mandaba los avances: el editor mataba un módulo sano (tarea 195).
    var watch = Stopwatch.StartNew();
    Task<MatteInput>[] prepared = [.. Enumerable.Range(0, outputs.Length).Select(i =>
        ModuleHost.RunBlockingAsync(() => Guard(() => Matting.Prepare(Copy(call, call.Buffers[i * 2]), high, side)), ct))];
    Task written = Task.CompletedTask;
    MatteResult? last = null;
    for (int i = 0; i < outputs.Length; i += parallel)
    {
        ct.ThrowIfCancellationRequested();
        int first = i, wave = Math.Min(parallel, outputs.Length - i);
        MatteResult[] mattes = await Task.WhenAll(Enumerable.Range(first, wave).Select(k => ModuleHost.RunBlockingAsync(() =>
        {
            // En un hilo propio se puede esperar la preparación (también en un hilo propio) sin quitarle uno al pool.
            MatteInput input = prepared[k].GetAwaiter().GetResult();
            return Guard(() => matting.Infer(input, device, ct));
        }, ct))).ConfigureAwait(false);
        for (int k = 0; k < wave; k++)
        {
            written = Write(written, first + k, mattes[k]);
            last = mattes[k];
        }

        await call.ReportProgressAsync((double)(first + wave) / outputs.Length, "Quitando el fondo", ct).ConfigureAwait(false);
    }

    await written.ConfigureAwait(false);

    async Task Write(Task previous, int pair, MatteResult matte)
    {
        await previous.ConfigureAwait(false);
        await ModuleHost.RunBlockingAsync(() =>
        {
            float[] alpha = temporal.Apply(session, reset && pair == 0, matte);
            VideoLayout size = outputs[pair].Video!;
            Imaging.AlphaToGray8(alpha, matte.Width, matte.Height, call.Map(outputs[pair]), size.Width, size.Height);
        }, ct).ConfigureAwait(false);
    }

    return new ModuleOutput(Json($$"""{"modelo":"{{last!.Model}}","dispositivo":"{{last.Device}}","cuadros":{{outputs.Length}},"segundos":{{Number(watch.Elapsed.TotalSeconds)}}}"""), outputs);
});

// detectar: hasta 16 cuadros bgra8. «objetivo» rostros|personas. Cajas normalizadas al cuadro (x, y: esquina superior izquierda).
host.Register("detectar", async (call, ct) =>
{
    if (call.Buffers.Count is < 1 or > 16)
    {
        throw Invalid("«detectar» necesita de 1 a 16 cuadros bgra8.");
    }

    foreach (SharedBufferRef buffer in call.Buffers)
    {
        Bgra(buffer, "detectar");
    }

    string target = Text(call.Parameters, "objetivo") switch
    {
        "rostros" => "rostros",
        "personas" => "personas",
        _ => throw Invalid("«objetivo» es «rostros» o «personas»."),
    };
    ComputeDevice device = Device(call.Parameters);
    models.CpuThreads = Math.Clamp(Int(call.Parameters, "hilos") ?? 0, 0, 64);
    var watch = Stopwatch.StartNew();
    var found = new List<Box>[call.Buffers.Count];
    string used = "";
    const int Batch = PersonDetector.Batch;
    for (int start = 0; start < found.Length; start += target == "personas" ? Batch : 1)
    {
        ct.ThrowIfCancellationRequested();
        int first = start, size = target == "personas" ? Math.Min(Batch, found.Length - start) : 1;
        used = await ModuleHost.RunBlockingAsync(() => Guard(() =>
        {
            var frames = Enumerable.Range(first, size).Select(i => Copy(call, call.Buffers[i])).ToList();
            if (target == "rostros")
            {
                (found[first], string d) = faces.Detect(frames[0], device, ct);
                return d;
            }

            (List<Box>[] boxes, string device2) = people.Detect(frames, device, ct);
            boxes.CopyTo(found, first);
            return device2;
        }), ct).ConfigureAwait(false);
        await call.ReportProgressAsync((double)(start + size) / found.Length, "Detectando", ct).ConfigureAwait(false);
    }

    var json = new StringBuilder("{\"cuadros\":[");
    for (int f = 0; f < found.Length; f++)
    {
        json.Append(f == 0 ? "" : ",").Append("{\"cajas\":[");
        for (int b = 0; b < found[f].Count; b++)
        {
            Box box = found[f][b];
            json.Append(b == 0 ? "" : ",").Append(CultureInfo.InvariantCulture,
                $$"""{"x":{{Number(box.X)}},"y":{{Number(box.Y)}},"w":{{Number(box.W)}},"h":{{Number(box.H)}},"p":{{Number(box.P)}}}""");
        }

        json.Append("]}");
    }

    json.Append(CultureInfo.InvariantCulture, $$"""],"dispositivo":"{{used}}","segundos":{{Number(watch.Elapsed.TotalSeconds)}}}""");
    return new ModuleOutput(Json(json.ToString()));
});

// reencuadre: sin búferes. {"aspecto", "fuente": {"ancho","alto"}, "cuadros": [{"t","cajas":[…]}], "suavizado"}.
host.Register("reencuadre", async (call, ct) =>
{
    if (call.Parameters is not { ValueKind: JsonValueKind.Object } p
        || !p.TryGetProperty("aspecto", out JsonElement aspectElement) || !aspectElement.TryGetDouble(out double aspect) || !(aspect is > 0.05 and < 20)
        || !p.TryGetProperty("fuente", out JsonElement source) || source.ValueKind != JsonValueKind.Object
        || !source.TryGetProperty("ancho", out JsonElement we) || !we.TryGetInt32(out int width) || width < 1
        || !source.TryGetProperty("alto", out JsonElement he) || !he.TryGetInt32(out int height) || height < 1
        || !p.TryGetProperty("cuadros", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
    {
        throw Invalid("«reencuadre» necesita «aspecto» (ancho/alto), «fuente» {ancho, alto} y «cuadros» [{t, cajas}].");
    }

    double smoothing = p.TryGetProperty("suavizado", out JsonElement s) && s.TryGetDouble(out double sv) ? sv : 0.5;
    var frames = new List<ReframeFrame>();
    foreach (JsonElement frame in list.EnumerateArray())
    {
        if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty("t", out JsonElement te) || !te.TryGetDouble(out double t) || !double.IsFinite(t))
        {
            throw Invalid("Cada cuadro de «reencuadre» lleva «t» en segundos.");
        }

        var boxes = new List<Box>();
        if (frame.TryGetProperty("cajas", out JsonElement cajas) && cajas.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement c in cajas.EnumerateArray())
            {
                if (Num(c, "x") is { } x && Num(c, "y") is { } y && Num(c, "w") is { } w && Num(c, "h") is { } h && w > 0 && h > 0)
                {
                    boxes.Add(new Box((float)x, (float)y, (float)w, (float)h, (float)(Num(c, "p") ?? 1)).Clamped());
                }
            }
        }

        frames.Add(new ReframeFrame(t, boxes));
    }

    ReframeResult result = await ModuleHost.RunBlockingAsync(() => Reframer.Run(aspect, width, height, frames, smoothing), ct).ConfigureAwait(false);
    var json = new StringBuilder("{\"keyframes\":[");
    for (int k = 0; k < result.Keyframes.Count; k++)
    {
        Keyframe key = result.Keyframes[k];
        json.Append(k == 0 ? "" : ",").Append(CultureInfo.InvariantCulture,
            $$"""{"t":{{Number(key.Time)}},"x":{{Number(key.X)}},"y":{{Number(key.Y)}},"scale":{{Number(key.Scale)}}}""");
    }

    json.Append(CultureInfo.InvariantCulture, $$"""],"dentro":{{Number(result.Inside)}}}""");
    return new ModuleOutput(Json(json.ToString()));
});

return await host.RunAsync(args).ConfigureAwait(false);

static ModuleCallException Invalid(string message) => new(ErrorCodes.InvalidParameters, message);

static VideoLayout Bgra(SharedBufferRef buffer, string function) =>
    buffer.Video is { PixelFormat: PixelFormats.Bgra8 } v ? v : throw Invalid($"«{function}» recibe cuadros bgra8.");

static BgraFrame Copy(ModuleCall call, SharedBufferRef buffer)
{
    VideoLayout v = buffer.Video!;
    int bytes = (v.Stride * (v.Height - 1)) + (v.Width * 4);
    return new BgraFrame(call.Map(buffer)[..bytes].ToArray(), v.Width, v.Height, v.Stride);
}

static ComputeDevice Device(JsonElement? parameters) => Text(parameters, "dispositivo") switch
{
    null or "gpu" => ComputeDevice.Gpu,
    "cpu" => ComputeDevice.Cpu,
    _ => throw Invalid("«dispositivo» es «gpu» o «cpu»."),
};

static string? Text(JsonElement? parameters, string name) =>
    parameters is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

static int? Int(JsonElement? parameters, string name) =>
    parameters is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int n) ? n : null;

static bool Flag(JsonElement? parameters, string name) =>
    parameters is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

static double? Num(JsonElement element, string name) =>
    element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement v) && v.TryGetDouble(out double d) && double.IsFinite(d) ? d : null;

static string Number(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

static JsonElement Json(string text)
{
    using JsonDocument doc = JsonDocument.Parse(text);
    return doc.RootElement.Clone();
}

// Un modelo que falta o está dañado, o un fallo de ONNX Runtime, se informa como error de la llamada.
static T Guard<T>(Func<T> work)
{
    try
    {
        return work();
    }
    catch (ModelMissingException ex)
    {
        throw new ModuleCallException(ErrorCodes.Internal, ex.Message);
    }
    catch (OnnxRuntimeException ex)
    {
        throw new ModuleCallException(ErrorCodes.Internal, "El modelo falló: " + ex.Message);
    }
}
