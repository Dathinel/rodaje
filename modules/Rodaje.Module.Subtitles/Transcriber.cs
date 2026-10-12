using System.Runtime.InteropServices;
using System.Text.Json;
using Rodaje.Modules.Protocol;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Rodaje.Module.Subtitles;

/// <summary>
/// La función <c>subtitulos</c>: Whisper con DTW por token sobre audio f32 de 16 kHz. Guarda el modelo cargado entre
/// llamadas (el editor cierra el proceso tras 2 min ocioso) y, si la GPU falla al cargar o al transcribir, sigue en la CPU.
/// </summary>
internal sealed class Transcriber : IDisposable
{
    public const string ModelFile = "ggml-large-v3-turbo-q5_0.bin";

    /// <summary>Sin GPU (o si falla), un modelo menor para no pasar de 8 min por cada 10 de audio (D-034).</summary>
    public const string CpuModelFile = "ggml-small-q5_1.bin";

    public const int SampleRate = 16000;

    /// <summary>Por debajo del máximo de 1 MiB de un mensaje, con margen para el sobre.</summary>
    private const int MaxInlineResult = 900 * 1024;

    /// <summary>Tramos a la vez como mucho («parallel»).</summary>
    public const int MaxParallel = 4;

    private WhisperFactory? _factory;
    private string? _factoryKey;
    private string _device = "cpu";

    static Transcriber() =>
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];

    public async ValueTask<ModuleOutput> RunAsync(ModuleCall call, CancellationToken cancellationToken)
    {
        Options options = Options.Parse(call.Parameters);
        if (call.Buffers.Count is < 1 or > 2 || call.Buffers[0].Audio is not { } audio)
        {
            throw new ModuleCallException(ErrorCodes.InvalidParameters, "«subtitulos» necesita un búfer de audio (y, opcional, uno de bytes para el resultado).");
        }

        if (audio.SampleRate != SampleRate)
        {
            throw new ModuleCallException(ErrorCodes.InvalidParameters, $"El audio tiene que venir a {SampleRate} Hz, no a {audio.SampleRate}.");
        }

        float[] samples = ReadMono(call.Map(call.Buffers[0]), audio);
        string model = FindModel(options.Model, ModelFile);

        int progress = 0;
        string stage = "cargando";
        using CancellationTokenSource pumpStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task pump = PumpProgressAsync(call, () => (progress / 100.0, stage), pumpStop.Token);
        Transcript transcript;
        try
        {
            transcript = await TranscribeWithFallbackAsync(samples, model, options, p => progress = p, () => stage = "transcribiendo", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await pumpStop.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false);
        }

        await call.ReportProgressAsync(1, "transcribiendo", cancellationToken).ConfigureAwait(false);
        return Output(call, transcript);
    }

    public void Dispose() => _factory?.Dispose();

    internal static string FindModel(string? requested, string file) =>
        TryFindModel(requested, file) ?? TryFindShared(file)
            ?? throw new ModuleCallException(ErrorCodes.InvalidParameters, $"Falta el modelo {file}: reinstala el módulo de subtítulos.");

    internal static string? TryFindModel(string? requested, string file)
    {
        List<string> candidates = [];
        if (!string.IsNullOrWhiteSpace(requested))
        {
            candidates.Add(requested);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "modelos", file));
        if (Environment.GetEnvironmentVariable("RODAJE_MODELOS") is { Length: > 0 } dir)
        {
            candidates.Add(Path.Combine(dir, file));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Como los demás módulos: el almacén compartido de complementos y, al desarrollar, el <c>.entorno</c>.</summary>
    private static string? TryFindShared(string file)
    {
        List<string> candidates = [];
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rodaje", "complementos", "modelos", file));
        if (Environment.GetEnvironmentVariable("RODAJE_ENTORNO") is { Length: > 0 } entorno)
        {
            candidates.Add(Path.Combine(entorno, "modelos", file));
        }

        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".entorno", "modelos")))
            {
                candidates.Add(Path.Combine(d.FullName, ".entorno", "modelos", file));
                break;
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>El modelo de la CPU: <c>cpuModel</c>, el menor junto al exe o en RODAJE_MODELOS, y si falta, el grande.</summary>
    internal static string CpuModel(Options options, string gpuModel) => TryFindModel(options.CpuModel, CpuModelFile) ?? gpuModel;

    private async Task<Transcript> TranscribeWithFallbackAsync(
        float[] samples, string model, Options options, Action<int> onProgress, Action onLoaded, CancellationToken ct)
    {
        if (options.Device != "cpu")
        {
            try
            {
                Factory(model, gpu: true);
                // Sin Vulkan, whisper.cpp abre el modelo en la CPU: con «auto» se pasa al menor.
                if (_device == "vulkan" || options.Device == "gpu")
                {
                    return await TranscribeAsync(samples, model, true, options, onProgress, onLoaded, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (options.Device == "auto" && ex is not OperationCanceledException and not ModuleException)
            {
                // La GPU no pudo (sin memoria, controlador): se repite en la CPU.
            }

            ResetFactory();
        }

        return await TranscribeAsync(samples, CpuModel(options, model), false, options, onProgress, onLoaded, ct).ConfigureAwait(false);
    }

    private async Task<Transcript> TranscribeAsync(
        float[] samples, string model, bool gpu, Options options, Action<int> onProgress, Action onLoaded, CancellationToken ct)
    {
        WhisperFactory factory = Factory(model, gpu);
        onLoaded();
        IReadOnlyList<(int Start, int Length)> chunks = AudioChunks.Split(samples, SampleRate);
        // En la CPU, un hilo por núcleo físico (whisper.cpp pone 4 por defecto); en la GPU casi no influye.
        int threads = options.Threads > 0 ? options.Threads : Math.Max(4, Environment.ProcessorCount / 2);
        string language = options.Language;
        int done = 0;
        void Report(double fraction) => onProgress((int)((Volatile.Read(ref done) + fraction) * 100 / chunks.Count));

        // Varios tramos a la vez (con mucha VRAM o muchos hilos): grupos seguidos, cada uno con su estado de Whisper y su
        // contexto; el modelo se comparte. Con «auto», el primer tramo va solo para saber el idioma.
        int groups = Math.Clamp(options.Parallel, 1, MaxParallel);
        var segments = new List<TranscriptSegment>?[chunks.Count];
        int first = 0;
        if (groups > 1 && chunks.Count > 1 && language == "auto")
        {
            (segments[0], language) = await TranscribeChunkAsync(factory, samples, chunks[0], language, options.Prompt, threads, Report, ct)
                .ConfigureAwait(false);
            Interlocked.Increment(ref done);
            first = 1;
        }

        int remaining = chunks.Count - first;
        groups = Math.Max(1, Math.Min(groups, remaining));
        var detected = new string?[groups];
        string known = language;
        await Task.WhenAll(Enumerable.Range(0, groups).Select(g => Task.Run(async () =>
        {
            int from = first + (remaining * g / groups), to = first + (remaining * (g + 1) / groups);
            string groupLanguage = known;
            string? prompt = Context(segments, from) ?? options.Prompt;
            for (int c = from; c < to; c++)
            {
                ct.ThrowIfCancellationRequested();
                (segments[c], groupLanguage) = await TranscribeChunkAsync(factory, samples, chunks[c], groupLanguage, prompt, threads, groups == 1 ? Report : _ => { }, ct)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref done);
                if (groups > 1)
                {
                    Report(0);
                }

                // El tramo siguiente sigue con el contexto del anterior, como haría Whisper en una sola pasada.
                prompt = Context(segments, c + 1) ?? options.Prompt;
            }

            detected[g] = groupLanguage;
        }, ct))).ConfigureAwait(false);
        if (language == "auto")
        {
            language = detected.FirstOrDefault(l => l is not null and not "auto") ?? "auto";
        }

        ct.ThrowIfCancellationRequested();
        List<TranscriptSegment> all = [.. segments.SelectMany(s => s ?? [])];
        if (options.Onsets)
        {
            OnsetRefiner refiner = new(samples, SampleRate);
            double previousEnd = 0;
            for (int i = 0; i < all.Count; i++)
            {
                all[i] = refiner.Refine(all[i], previousEnd);
                previousEnd = all[i].Words.Count > 0 ? all[i].Words[^1].End : all[i].End;
            }
        }

        return new Transcript(language == "auto" ? "und" : language, _device, all);
    }

    /// <summary>Las últimas frases antes del tramo <paramref name="next"/> (hasta 200 caracteres), o <c>null</c>.</summary>
    private static string? Context(List<TranscriptSegment>?[] segments, int next)
    {
        if (next <= 0 || segments[next - 1] is not { Count: > 0 } previous)
        {
            return null;
        }

        string context = string.Join(' ', previous.TakeLast(3).Select(s => s.Text));
        return context.Length > 0 ? context[Math.Max(0, context.Length - 200)..] : null;
    }

    private static async Task<(List<TranscriptSegment> Segments, string Language)> TranscribeChunkAsync(WhisperFactory factory, float[] samples,
        (int Start, int Length) chunk, string language, string? prompt, int threads, Action<double> onProgress, CancellationToken ct)
    {
        (int from, int length) = chunk;
        double offset = (double)from / SampleRate;
        WhisperProcessorBuilder builder = factory.CreateBuilder()
            .WithLanguage(language)
            .WithTokenTimestamps()
            .WithProbabilities()
            .WithProgressHandler(p => onProgress(p / 100.0))
            .WithEncoderBeginHandler(_ => !ct.IsCancellationRequested)
            .WithThreads(threads);
        if (!string.IsNullOrEmpty(prompt))
        {
            builder = builder.WithPrompt(prompt);
        }

        List<TranscriptSegment> segments = [];
        await using WhisperProcessor processor = builder.Build();
        await foreach (SegmentData segment in processor.ProcessAsync(new ReadOnlyMemory<float>(samples, from, length), ct).ConfigureAwait(false))
        {
            if (language == "auto" && !string.IsNullOrEmpty(segment.Language))
            {
                language = segment.Language;
            }

            double start = offset + segment.Start.TotalSeconds;
            double end = Math.Max(start, offset + segment.End.TotalSeconds);
            List<TokenTiming> tokens = new(segment.Tokens.Length);
            foreach (WhisperToken t in segment.Tokens)
            {
                tokens.Add(new TokenTiming(t.Text ?? "", offset + (t.Start / 100.0), offset + (t.End / 100.0),
                    t.DtwTimestamp >= 0 ? offset + (t.DtwTimestamp / 100.0) : -1, t.Probability));
            }

            string text = segment.Text.Trim();
            if (text.Length > 0)
            {
                segments.Add(new TranscriptSegment(WordBuilder.Round(start), WordBuilder.Round(end), text, WordBuilder.Build(tokens, start, end, text)));
            }
        }

        return (segments, language);
    }

    private WhisperFactory Factory(string model, bool gpu)
    {
        string key = $"{model}|{gpu}";
        if (_factory is not null && _factoryKey == key)
        {
            return _factory;
        }

        ResetFactory();
        _factory = WhisperFactory.FromPath(model, new WhisperFactoryOptions
        {
            UseGpu = gpu,
            UseDtwTimeStamps = true,
            HeadsPreset = HeadsFor(model),
        });
        _factoryKey = key;
        _device = gpu && RuntimeOptions.LoadedLibrary == RuntimeLibrary.Vulkan ? "vulkan" : "cpu";
        return _factory;
    }

    /// <summary>Las cabezas de alineamiento del DTW dependen del tamaño del modelo; se deduce del nombre del archivo ggml.</summary>
    internal static WhisperAlignmentHeadsPreset HeadsFor(string model)
    {
        string name = Path.GetFileName(model);
        return name.Contains("-small", StringComparison.OrdinalIgnoreCase) ? WhisperAlignmentHeadsPreset.Small
            : name.Contains("-base", StringComparison.OrdinalIgnoreCase) ? WhisperAlignmentHeadsPreset.Base
            : name.Contains("-tiny", StringComparison.OrdinalIgnoreCase) ? WhisperAlignmentHeadsPreset.Tiny
            : WhisperAlignmentHeadsPreset.LargeV3Turbo;
    }

    private void ResetFactory()
    {
        _factory?.Dispose();
        _factory = null;
        _factoryKey = null;
    }

    private static float[] ReadMono(Span<byte> bytes, AudioLayout audio)
    {
        ReadOnlySpan<float> interleaved = MemoryMarshal.Cast<byte, float>(bytes)[..checked((int)(audio.Frames * audio.Channels))];
        float[] mono = new float[audio.Frames];
        if (audio.Channels == 1)
        {
            interleaved.CopyTo(mono);
            return mono;
        }

        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int c = 0; c < audio.Channels; c++)
            {
                sum += interleaved[(i * audio.Channels) + c];
            }

            mono[i] = sum / audio.Channels;
        }

        return mono;
    }

    private static async Task PumpProgressAsync(ModuleCall call, Func<(double Fraction, string Stage)> read, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                (double fraction, string stage) = read();
                await call.ReportProgressAsync(Math.Min(fraction, 0.99), stage, ct).ConfigureAwait(false);
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static ModuleOutput Output(ModuleCall call, Transcript transcript)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(transcript, TranscriptJson.Default.Transcript);
        if (call.Buffers.Count == 2)
        {
            SharedBufferRef target = call.Buffers[1];
            Span<byte> span = call.Map(target);
            if (json.Length > span.Length)
            {
                throw new ModuleCallException(ErrorCodes.BufferOutOfRange, $"El resultado ocupa {json.Length} bytes y el búfer de salida {span.Length}.");
            }

            json.CopyTo(span);
            using JsonDocument size = JsonDocument.Parse($$"""{"bytes":{{json.Length}}}""");
            return new ModuleOutput(size.RootElement.Clone(), [target]);
        }

        if (json.Length > MaxInlineResult)
        {
            throw new ModuleCallException(ErrorCodes.InvalidParameters,
                $"El resultado ocupa {json.Length} bytes: pasa un búfer de salida o parte el audio en tramos.");
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        return new ModuleOutput(doc.RootElement.Clone());
    }

    internal sealed record Options(string Language, string Device, string? Model, string? Prompt, int Threads, bool Onsets, string? CpuModel = null, int Parallel = 1)
    {
        public static Options Parse(JsonElement? parameters)
        {
            string language = Text(parameters, "language") ?? "auto";
            string device = Text(parameters, "device") ?? "auto";
            if (device is not ("auto" or "gpu" or "cpu"))
            {
                throw new ModuleCallException(ErrorCodes.InvalidParameters, $"«device» es auto, gpu o cpu, no «{device}».");
            }

            if (language != "auto" && WhisperFactory.GetSupportedLanguages().All(l => l != language))
            {
                throw new ModuleCallException(ErrorCodes.InvalidParameters, $"Whisper no conoce el idioma «{language}».");
            }

            int threads = parameters is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("threads", out JsonElement t) && t.TryGetInt32(out int n)
                ? Math.Clamp(n, 0, 64)
                : 0;
            // «onsets»: false deja los inicios tal como los da el DTW (para medir el ajuste por energía).
            bool onsets = !(parameters is { ValueKind: JsonValueKind.Object } q && q.TryGetProperty("onsets", out JsonElement o) && o.ValueKind == JsonValueKind.False);
            // «parallel» (v1.1): tramos a la vez; los módulos 1.0 lo ignoran.
            int parallel = parameters is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("parallel", out JsonElement pe) && pe.TryGetInt32(out int pn)
                ? Math.Clamp(pn, 1, MaxParallel)
                : 1;
            return new Options(language, device, Text(parameters, "model"), Text(parameters, "prompt"), threads, onsets, Text(parameters, "cpuModel"), parallel);
        }

        private static string? Text(JsonElement? parameters, string name) =>
            parameters is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }
}
