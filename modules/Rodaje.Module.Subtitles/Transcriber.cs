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
    public const int SampleRate = 16000;

    /// <summary>Por debajo del máximo de 1 MiB de un mensaje, con margen para el sobre.</summary>
    private const int MaxInlineResult = 900 * 1024;

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
        string model = FindModel(options.Model);

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

    internal static string FindModel(string? requested)
    {
        List<string> candidates = [];
        if (!string.IsNullOrWhiteSpace(requested))
        {
            candidates.Add(requested);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "modelos", ModelFile));
        if (Environment.GetEnvironmentVariable("RODAJE_MODELOS") is { Length: > 0 } dir)
        {
            candidates.Add(Path.Combine(dir, ModelFile));
        }

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new ModuleCallException(ErrorCodes.InvalidParameters, $"Falta el modelo {ModelFile}: reinstala el módulo de subtítulos.");
    }

    private async Task<Transcript> TranscribeWithFallbackAsync(
        float[] samples, string model, Options options, Action<int> onProgress, Action onLoaded, CancellationToken ct)
    {
        bool gpu = options.Device != "cpu";
        try
        {
            return await TranscribeAsync(samples, model, gpu, options, onProgress, onLoaded, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (gpu && options.Device == "auto" && ex is not OperationCanceledException and not ModuleException)
        {
            // La GPU no pudo (sin memoria, controlador): se repite en la CPU.
            ResetFactory();
            return await TranscribeAsync(samples, model, false, options, onProgress, onLoaded, ct).ConfigureAwait(false);
        }
    }

    private async Task<Transcript> TranscribeAsync(
        float[] samples, string model, bool gpu, Options options, Action<int> onProgress, Action onLoaded, CancellationToken ct)
    {
        WhisperFactory factory = Factory(model, gpu);
        onLoaded();
        IReadOnlyList<(int Start, int Length)> chunks = AudioChunks.Split(samples, SampleRate);
        List<TranscriptSegment> segments = [];
        string language = options.Language;
        string? prompt = options.Prompt;
        for (int c = 0; c < chunks.Count; c++)
        {
            ct.ThrowIfCancellationRequested();
            (int from, int length) = chunks[c];
            double offset = (double)from / SampleRate;
            int done = c;
            WhisperProcessorBuilder builder = factory.CreateBuilder()
                .WithLanguage(language)
                .WithTokenTimestamps()
                .WithProbabilities()
                .WithProgressHandler(p => onProgress((int)((done + (p / 100.0)) * 100 / chunks.Count)))
                .WithEncoderBeginHandler(_ => !ct.IsCancellationRequested);
            // En la CPU, un hilo por núcleo físico (whisper.cpp pone 4 por defecto); en la GPU casi no influye.
            int threads = options.Threads > 0 ? options.Threads : Math.Max(4, Environment.ProcessorCount / 2);
            builder = builder.WithThreads(threads);

            if (!string.IsNullOrEmpty(prompt))
            {
                builder = builder.WithPrompt(prompt);
            }

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

            // El tramo siguiente sigue con el contexto del anterior, como haría Whisper en una sola pasada.
            string context = string.Join(' ', segments.TakeLast(3).Select(s => s.Text));
            prompt = context.Length > 0 ? context[Math.Max(0, context.Length - 200)..] : options.Prompt;
        }

        ct.ThrowIfCancellationRequested();
        if (options.Onsets)
        {
            OnsetRefiner refiner = new(samples, SampleRate);
            double previousEnd = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                segments[i] = refiner.Refine(segments[i], previousEnd);
                previousEnd = segments[i].Words.Count > 0 ? segments[i].Words[^1].End : segments[i].End;
            }
        }

        return new Transcript(language == "auto" ? "und" : language, _device, segments);
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
            HeadsPreset = WhisperAlignmentHeadsPreset.LargeV3Turbo,
        });
        _factoryKey = key;
        _device = gpu && RuntimeOptions.LoadedLibrary == RuntimeLibrary.Vulkan ? "vulkan" : "cpu";
        return _factory;
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

    internal sealed record Options(string Language, string Device, string? Model, string? Prompt, int Threads, bool Onsets)
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
            return new Options(language, device, Text(parameters, "model"), Text(parameters, "prompt"), threads, onsets);
        }

        private static string? Text(JsonElement? parameters, string name) =>
            parameters is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }
}
