using System.Diagnostics;
using System.Text.Json;
using Rodaje.Module.Raw;
using Rodaje.Modules.Protocol;

ModuleInfo info = new("rodaje-raw", "0.1.0", "RAW de cine",
[
    new FunctionInfo("info", "Tamaño, cámara, balance y cadencia de un cuadro RAW", ReportsProgress: false, Inputs: ["bytes"]),
    new FunctionInfo("revelar", "Revelar cuadros RAW (CinemaDNG) con LibRaw", Inputs: ["bytes"], Outputs: ["video"]),
], Device: "cpu");

// El editor espera el saludo hasta 30 s (ModuleManagerOptions.StartTimeout); con los 10 s del anfitrión, en un equipo
// cargado el módulo se rendía antes de recibir «hello» y el editor veía «Se cortó la conexión al saludar».
ModuleHost host = new(info) { HandshakeTimeout = TimeSpan.FromSeconds(30) };

// info: [archivo RAW entero]. El editor lee el archivo; el módulo no abre archivos del usuario.
host.Register("info", (call, _) =>
{
    if (call.Buffers.Count != 1)
    {
        throw new ModuleCallException(ErrorCodes.InvalidParameters, "«info» necesita un búfer con el archivo.");
    }

    RawInfo raw = Guard(() => RawDeveloper.Inspect(call.Map(call.Buffers[0])));
    (int Numerator, int Denominator)? rate = DngTags.FrameRate(call.Map(call.Buffers[0]));
    return ValueTask.FromResult(new ModuleOutput(Json(w =>
    {
        w.WriteNumber("ancho", raw.Width);
        w.WriteNumber("alto", raw.Height);
        w.WriteString("marca", raw.Make);
        w.WriteString("modelo", raw.Model);
        w.WriteNumber("iso", raw.Iso);
        w.WriteNumber("obturador", raw.Shutter);
        w.WriteNumber("diafragma", raw.Aperture);
        w.WriteNumber("focal", raw.FocalLength);
        w.WriteBoolean("dng", raw.IsDng);
        w.WriteStartArray("balanceCamara");
        foreach (float m in raw.CameraMultipliers)
        {
            w.WriteNumberValue(m);
        }

        w.WriteEndArray();
        if (rate is { } r)
        {
            w.WriteStartObject("cadencia");
            w.WriteNumber("num", r.Numerator);
            w.WriteNumber("den", r.Denominator);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("cadencia");
        }

        w.WriteString("libraw", RawDeveloper.Version);
    })));
});

// revelar: pares [archivo, salida] (hasta 8 cuadros por llamada), todas las salidas del mismo formato (rgba16f, bgra8 o
// rgba8) y del tamaño que dio «info». Parámetros: balance («camara», «luz-dia» o [r, g, b]), exposicion (EV), espacio
// (rec709, rec2020, p3, aces, prophoto), curva (rec709, srgb, lineal), calidad (alta = AHD, rapida = lineal) e hilos.
host.Register("revelar", async (call, ct) =>
{
    IReadOnlyList<SharedBufferRef> buffers = call.Buffers;
    if (buffers.Count is < 2 or > 16 || buffers.Count % 2 != 0
        || buffers.Where((_, i) => i % 2 == 1).Any(b => b.Video is not { } v || !ToneTable.Supports(v.PixelFormat) || v.PixelFormat != buffers[1].Video!.PixelFormat))
    {
        throw new ModuleCallException(ErrorCodes.InvalidParameters,
            "«revelar» necesita de 1 a 8 pares [archivo, salida de video], con todas las salidas en rgba16f, bgra8 o rgba8.");
    }

    DevelopSettings settings;
    int threads;
    try
    {
        settings = DevelopSettings.Parse(call.Parameters);
        threads = call.Parameters is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("hilos", out JsonElement h)
            ? (h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out int n) && n is >= 1 and <= 64 ? n : throw new ArgumentException("«hilos» va de 1 a 64."))
            : Environment.ProcessorCount;
    }
    catch (ArgumentException ex)
    {
        throw new ModuleCallException(ErrorCodes.InvalidParameters, ex.Message);
    }

    var table = new ToneTable(buffers[1].Video!.PixelFormat, settings.Exposure, settings.Curve);
    int frames = buffers.Count / 2;
    using var gate = new SemaphoreSlim(Math.Min(threads, frames));
    var milliseconds = new double[frames];
    List<Task> pending = [.. Enumerable.Range(0, frames).Select(i => Task.Run(async () =>
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long start = Stopwatch.GetTimestamp();
            Guard(() =>
            {
                RawDeveloper.Develop(call.Map(buffers[i * 2]), settings, table, call.Map(buffers[(i * 2) + 1]), buffers[(i * 2) + 1].Video!, ct);
                return 0;
            }, frames > 1 ? $"Cuadro {i + 1}: " : "");
            milliseconds[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        finally
        {
            gate.Release();
        }
    }, ct))];

    int done = 0;
    while (pending.Count > 0)
    {
        Task finished = await Task.WhenAny(pending).ConfigureAwait(false);
        pending.Remove(finished);
        await finished.ConfigureAwait(false);
        await call.ReportProgressAsync((double)++done / frames, "Revelando", ct).ConfigureAwait(false);
    }

    return new ModuleOutput(Json(w =>
    {
        w.WriteNumber("cuadros", frames);
        w.WriteStartArray("ms");
        foreach (double ms in milliseconds)
        {
            w.WriteNumberValue(Math.Round(ms, 1));
        }

        w.WriteEndArray();
    }), [.. buffers.Where((_, i) => i % 2 == 1)]);
});

return await host.RunAsync(args).ConfigureAwait(false);

static JsonElement Json(Action<Utf8JsonWriter> body)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
    {
        writer.WriteStartObject();
        body(writer);
        writer.WriteEndObject();
    }

    using JsonDocument doc = JsonDocument.Parse(stream.ToArray());
    return doc.RootElement.Clone();
}

// Un archivo que LibRaw no entiende es un error de la llamada con un texto para la persona; el módulo sigue.
static T Guard<T>(Func<T> work, string prefix = "")
{
    try
    {
        return work();
    }
    catch (RawException ex)
    {
        throw new ModuleCallException(ErrorCodes.InvalidParameters, prefix + ex.Message);
    }
}
