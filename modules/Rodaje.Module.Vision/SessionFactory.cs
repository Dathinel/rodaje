using Microsoft.ML.OnnxRuntime;

namespace Rodaje.Module.Vision;

/// <summary>Dónde corre un modelo.</summary>
internal enum ComputeDevice
{
    Cpu,

    /// <summary>La GPU del sistema: DirectML en Windows y CoreML en macOS; si no se puede abrir (o en Linux), la CPU.</summary>
    Gpu,
}

/// <summary>
/// Sesiones de ONNX Runtime. DirectML pide memoria sin patrones y ejecución secuencial; en CPU se dejan los hilos por
/// omisión (uno por núcleo físico). Como en modulo-audio-ia, más <paramref name="fuseGraph"/>: sin la fusión de DirectML,
/// BiRefNet_lite pide un tercio de la memoria (la fusión deja tensores intermedios sin reutilizar). En Linux la IA va en la
/// CPU (D-066: CUDA quedaría como complemento aparte).
/// </summary>
internal sealed class SessionFactory(ComputeDevice device, int cpuThreads = 0, bool fuseGraph = true)
{
    private bool _gpu = device == ComputeDevice.Gpu && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());

    /// <summary>«directml», «coreml» o «cpu» (también si la GPU no se pudo abrir).</summary>
    public string Device => !_gpu ? "cpu" : OperatingSystem.IsMacOS() ? "coreml" : "directml";

    /// <summary>Si las inferencias de una sesión van de a una: DirectML y CoreML no admiten <c>Run</c> concurrentes.</summary>
    public static bool IsExclusive(string device) => device is "directml" or "coreml";

    /// <param name="dimensions">Dimensiones libres que se fijan (nombre simbólico → tamaño).</param>
    public InferenceSession Create(string modelPath, IReadOnlyDictionary<string, long>? dimensions = null)
    {
        if (_gpu)
        {
            try
            {
                using SessionOptions gpu = new() { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
                if (!fuseGraph && OperatingSystem.IsWindows())
                {
                    gpu.AddSessionConfigEntry("ep.dml.disable_graph_fusion", "1");
                }

                foreach ((string name, long size) in dimensions ?? new Dictionary<string, long>())
                {
                    gpu.AddFreeDimensionOverrideByName(name, size);
                }

                if (OperatingSystem.IsMacOS())
                {
                    gpu.AppendExecutionProvider_CoreML(default);
                }
                else
                {
                    gpu.AppendExecutionProvider_DML(0);
                }

                return new InferenceSession(modelPath, gpu);
            }
            catch (OnnxRuntimeException)
            {
                _gpu = false;
            }
        }

        using SessionOptions cpu = new() { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (cpuThreads > 0)
        {
            cpu.IntraOpNumThreads = cpuThreads;
        }

        return new InferenceSession(modelPath, cpu);
    }
}
