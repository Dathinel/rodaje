using System.Text.Json;
using Rodaje.Module.Translate.Translation;
using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Translate;

/// <summary>
/// La función <c>traducir</c>: comprueba la entrada, carga el modelo (y lo guarda entre llamadas; el editor cierra el
/// proceso tras 2 min ocioso) y traduce. Búferes opcionales: el primero, de bytes, con el JSON de entrada si no cabe en
/// un mensaje; el segundo, de bytes, para el resultado si pasa de un mensaje.
/// </summary>
internal sealed class TranslateFunction : IDisposable
{
    /// <summary>Por debajo del máximo de 1 MiB de un mensaje, con margen para el sobre.</summary>
    private const int MaxInlineResult = 900 * 1024;

    private readonly SemaphoreSlim _load = new(1, 1);
    private LlamaGenerator? _generator;

    public async ValueTask<ModuleOutput> RunAsync(ModuleCall call, CancellationToken cancellationToken)
    {
        if (call.Buffers.Count > 2)
        {
            throw new ModuleCallException(ErrorCodes.InvalidParameters, "«traducir» admite como mucho un búfer de entrada y uno de salida.");
        }

        if (call.Buffers.Any(b => b.Audio is not null || b.Video is not null))
        {
            throw new ModuleCallException(ErrorCodes.InvalidParameters, "«traducir» solo usa búferes de bytes.");
        }

        bool inputBuffer = call.Buffers.Count == 2 || (call.Buffers.Count == 1 && !HasInlineSegments(call.Parameters));
        TranslationRequest request = TranslationRequest.Parse(call.Parameters, inputBuffer ? call.Map(call.Buffers[0]) : default);
        string model = ModelFiles.Find(request.Model);

        await call.ReportProgressAsync(0, "cargando", cancellationToken).ConfigureAwait(false);
        LlamaGenerator generator = await GeneratorAsync(model, request, cancellationToken).ConfigureAwait(false);

        double fraction = 0;
        using CancellationTokenSource pumpStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task pump = PumpProgressAsync(call, () => Volatile.Read(ref fraction), pumpStop.Token);
        TranslationResult result;
        try
        {
            result = await new SubtitleTranslator(generator)
                .TranslateAsync(request, new SyncProgress(v => Volatile.Write(ref fraction, v)), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await pumpStop.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false);
        }

        await call.ReportProgressAsync(1, "traduciendo", cancellationToken).ConfigureAwait(false);
        SharedBufferRef? output = call.Buffers.Count == 2 ? call.Buffers[1] : call.Buffers.Count == 1 && !inputBuffer ? call.Buffers[0] : null;
        return Output(call, result, output);
    }

    public void Dispose()
    {
        _generator?.Dispose();
        _load.Dispose();
    }

    internal static ModuleOutput Output(ModuleCall call, TranslationResult result, SharedBufferRef? target)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(result, TranslationJson.Default.TranslationResult);
        if (target is not null)
        {
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
                $"El resultado ocupa {json.Length} bytes: pasa un búfer de salida o parte los subtítulos en tandas.");
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        return new ModuleOutput(doc.RootElement.Clone());
    }

    private static bool HasInlineSegments(JsonElement? parameters) =>
        parameters is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("segments", out _);

    private async Task<LlamaGenerator> GeneratorAsync(string model, TranslationRequest request, CancellationToken cancellationToken)
    {
        await _load.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string key = LlamaGenerator.KeyFor(model, request.Device, request.Threads, request.GpuLayers);
            if (_generator is not null && _generator.Key == key)
            {
                return _generator;
            }

            _generator?.Dispose();
            _generator = null;
            _generator = await LlamaGenerator.LoadAsync(model, request.Device, request.Threads, request.GpuLayers, cancellationToken).ConfigureAwait(false);
            return _generator;
        }
        finally
        {
            _load.Release();
        }
    }

    private static async Task PumpProgressAsync(ModuleCall call, Func<double> read, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await call.ReportProgressAsync(Math.Min(read(), 0.99), "traduciendo", ct).ConfigureAwait(false);
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Sin pasar por el contexto de sincronización (<see cref="Progress{T}"/> lo haría en otro hilo, tarde).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
