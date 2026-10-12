using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Rodaje.Module.Translate.Translation;
using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Translate;

/// <summary>
/// Qwen3-4B en llama.cpp vía LLamaSharp. La biblioteca nativa se elige una vez por proceso, al cargar el primer modelo:
/// la de Vulkan si el controlador trae <c>vulkan-1.dll</c>, si no la de CPU (AVX512, AVX2, AVX o sin AVX). Con Vulkan
/// se suben a la GPU todas las capas que quepan: si no cabe entero (4 GB de VRAM con el editor abierto) se repite con
/// la mitad y luego en la CPU («descarga parcial»).
/// </summary>
internal sealed class LlamaGenerator : ITextGenerator, IDisposable
{
    /// <summary>Contexto por lote: prompt (~800 tokens con 1000 caracteres) más respuesta (≤1200).</summary>
    public const int ContextSize = 3072;

    /// <summary>Qwen3-4B tiene 36 capas; con más se suben todas.</summary>
    public const int AllLayers = 99;

    private static readonly object NativeGate = new();
    private static bool _nativeConfigured;

    private readonly LLamaWeights _weights;
    private readonly ModelParams _parameters;

    private LlamaGenerator(LLamaWeights weights, ModelParams parameters, string device)
    {
        _weights = weights;
        _parameters = parameters;
        Device = device;
    }

    public string Device { get; }

    /// <summary>El archivo, el dispositivo y los hilos con que se cargó: si una llamada pide lo mismo, se reutiliza.</summary>
    public string Key { get; private init; } = "";

    public static string KeyFor(string model, string device, int threads, int gpuLayers) => $"{model}|{device}|{threads}|{gpuLayers}";

    /// <summary>Carga el modelo; con <paramref name="device"/> <c>auto</c> baja capas a la CPU hasta que quepa.</summary>
    public static async Task<LlamaGenerator> LoadAsync(string model, string device, int threads, int gpuLayers, CancellationToken cancellationToken)
    {
        ConfigureNative();
        int cpuThreads = threads > 0 ? threads : Math.Max(4, Environment.ProcessorCount / 2);
        int first = device == "cpu" ? 0 : gpuLayers >= 0 ? gpuLayers : AllLayers;
        List<int> attempts = device switch
        {
            "cpu" => [0],
            "gpu" => [first],
            _ => first == 0 ? [0] : [first, Math.Min(first, 18), 0],
        };

        Exception? last = null;
        foreach (int layers in attempts.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModelParams parameters = new(model)
            {
                ContextSize = ContextSize,
                GpuLayerCount = layers,
                Threads = cpuThreads,
                BatchThreads = cpuThreads,
                BatchSize = 512,
            };
            try
            {
                LLamaWeights weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken).ConfigureAwait(false);
                // Si cargó la biblioteca de CPU (sin Vulkan en el equipo), las capas «en la GPU» se quedan en la CPU.
                bool vulkan = NativeApi.llama_supports_gpu_offload();
                string where = layers == 0 || !vulkan ? "cpu" : layers >= AllLayers ? "vulkan" : "vulkan+cpu";
                return new LlamaGenerator(weights, parameters, where) { Key = KeyFor(model, device, threads, gpuLayers) };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && device == "auto")
            {
                // Sin memoria de video o un controlador que no puede: se intenta con menos capas en la GPU.
                last = ex;
            }
        }

        throw new ModuleCallException(ErrorCodes.OutOfMemory, "No se pudo cargar el modelo del traductor: " + (last?.Message ?? "sin memoria."));
    }

    public async Task<string> GenerateAsync(string prompt, int maxTokens, CancellationToken cancellationToken)
    {
        StatelessExecutor executor = new(_weights, _parameters) { ApplyTemplate = false };
        InferenceParams inference = new()
        {
            MaxTokens = maxTokens,
            AntiPrompts = [Prompt.EndOfTurn],
            // Sin azar: la misma entrada da la misma traducción.
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0 },
        };
        StringBuilder text = new();
        await foreach (string piece in executor.InferAsync(prompt, inference, cancellationToken).ConfigureAwait(false))
        {
            text.Append(piece);
        }

        return text.ToString();
    }

    public void Dispose() => _weights.Dispose();

    private static void ConfigureNative()
    {
        lock (NativeGate)
        {
            if (_nativeConfigured)
            {
                return;
            }

            // Vulkan si se puede, CPU si no; los avisos de llama.cpp solo si son errores (salen por stderr del módulo).
            NativeLibraryConfig.All
                .WithVulkan(true)
                .WithAutoFallback(true)
                .WithLogCallback((level, message) =>
                {
                    if (level == LLamaLogLevel.Error)
                    {
                        Console.Error.Write(message);
                    }
                });
            _nativeConfigured = true;
        }
    }
}
