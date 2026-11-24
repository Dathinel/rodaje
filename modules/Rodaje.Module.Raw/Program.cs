using System.Diagnostics;
using System.Text.Json;
using Rodaje.Module.Raw;
using Rodaje.Modules.Protocol;

ModuleInfo info = new("modulo-raw", "1.1.0", "RAW de cine",
[
    new FunctionInfo("info", "Tamaño, cámara, balance y cadencia de un cuadro RAW", ReportsProgress: false, Inputs: ["bytes"]),
    new FunctionInfo("revelar", "Revelar cuadros RAW (CinemaDNG) con LibRaw", Inputs: ["bytes"], Outputs: ["video"]),
    new FunctionInfo("mosaico", "Leer el mosaico de Bayer sin interpolar, para revelarlo en la GPU del editor", Inputs: ["bytes"], Outputs: ["bytes"]),
], Device: "cpu");

ModuleHost host = new(info);

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
        // bayer: «mosaico» puede dar este cuadro (patrón de 2×2 RGB sin girar); si no, solo «revelar».
        w.WriteBoolean("bayer", raw.BayerPattern is not null);
        if (raw.BayerPattern is { } pattern)
        {
            w.WriteString("patron", pattern);
        }

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
        threads = Threads(call.Parameters);
    }
    catch (ArgumentException ex)
    {
        throw new ModuleCallException(ErrorCodes.InvalidParameters, ex.Message);
    }

    var table = new ToneTable(buffers[1].Video!.PixelFormat, settings.Exposure, settings.Curve);
    int frames = buffers.Count / 2;
    var milliseconds = new double[frames];
    await FrameBatch.RunAsync(frames, threads, (i, token) =>
    {
        long start = Stopwatch.GetTimestamp();
        Guard(() =>
        {
            RawDeveloper.Develop(call.Map(buffers[i * 2]), settings, table, call.Map(buffers[(i * 2) + 1]), buffers[(i * 2) + 1].Video!, token);
            return 0;
        }, frames > 1 ? $"Cuadro {i + 1}: " : "");
        milliseconds[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }, (done, token) => call.ReportProgressAsync((double)done / frames, "Revelando", token), ct).ConfigureAwait(false);

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

// mosaico: pares [archivo, salida] (hasta 8 cuadros), con «ancho» y «alto» (los de «info») en los parámetros y cada salida de
// ancho × alto × 2 bytes: el mosaico de Bayer con el negro restado, de 16 bits por filas, sin interpolar. Toma «balance» (y
// «espacio», que solo puede ser rec709) de los mismos parámetros que «revelar»; la exposición y la curva las aplica quien
// revela. Devuelve por cuadro el patrón, el blanco, los multiplicadores y la matriz (docs/hdr.md, fase D).
host.Register("mosaico", async (call, ct) =>
{
    IReadOnlyList<SharedBufferRef> buffers = call.Buffers;
    DevelopSettings settings;
    int width, height, threads;
    try
    {
        settings = DevelopSettings.Parse(call.Parameters);
        (width, height) = Size(call.Parameters);
        threads = Threads(call.Parameters);
    }
    catch (ArgumentException ex)
    {
        throw new ModuleCallException(ErrorCodes.InvalidParameters, ex.Message);
    }

    long mosaicBytes = (long)width * height * 2;
    if (buffers.Count is < 2 or > 16 || buffers.Count % 2 != 0
        || buffers.Where((_, i) => i % 2 == 1).Any(b => b.Video is not null || b.Audio is not null || b.Length < mosaicBytes))
    {
        throw new ModuleCallException(ErrorCodes.InvalidParameters,
            "«mosaico» necesita de 1 a 8 pares [archivo, salida de bytes] con cada salida de ancho × alto × 2 bytes.");
    }

    int frames = buffers.Count / 2;
    var infos = new MosaicInfo[frames];
    var milliseconds = new double[frames];
    await FrameBatch.RunAsync(frames, threads, (i, token) =>
    {
        long start = Stopwatch.GetTimestamp();
        infos[i] = Guard(() => RawDeveloper.Unpack(call.Map(buffers[i * 2]), settings, call.Map(buffers[(i * 2) + 1]), width, height, token),
            frames > 1 ? $"Cuadro {i + 1}: " : "");
        milliseconds[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }, (done, token) => call.ReportProgressAsync((double)done / frames, "Leyendo", token), ct).ConfigureAwait(false);

    return new ModuleOutput(Json(w =>
    {
        w.WriteStartArray("cuadros");
        foreach (MosaicInfo mosaic in infos)
        {
            w.WriteStartObject();
            w.WriteString("patron", mosaic.Pattern);
            w.WriteNumber("blanco", mosaic.White);
            w.WriteStartArray("balance");
            foreach (float m in mosaic.Multipliers)
            {
                w.WriteNumberValue(m);
            }

            w.WriteEndArray();
            w.WriteStartArray("matriz");
            foreach (float m in mosaic.Matrix)
            {
                w.WriteNumberValue(m);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("ms");
        foreach (double ms in milliseconds)
        {
            w.WriteNumberValue(Math.Round(ms, 1));
        }

        w.WriteEndArray();
    }), [.. buffers.Where((_, i) => i % 2 == 1)]);
});

return await host.RunAsync(args).ConfigureAwait(false);

static int Threads(JsonElement? parameters) =>
    parameters is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("hilos", out JsonElement h)
        ? (h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out int n) && n is >= 1 and <= 64 ? n : throw new ArgumentException("«hilos» va de 1 a 64."))
        : Environment.ProcessorCount;

static (int Width, int Height) Size(JsonElement? parameters)
{
    if (parameters is { ValueKind: JsonValueKind.Object } p
        && p.TryGetProperty("ancho", out JsonElement w) && w.ValueKind == JsonValueKind.Number && w.TryGetInt32(out int width) && width is >= 1 and <= 16_384
        && p.TryGetProperty("alto", out JsonElement h) && h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out int height) && height is >= 1 and <= 16_384)
    {
        return (width, height);
    }

    throw new ArgumentException("«mosaico» necesita «ancho» y «alto» (de 1 a 16 384), los que dio «info».");
}

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
