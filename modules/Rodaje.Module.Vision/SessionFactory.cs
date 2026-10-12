using Microsoft.ML.OnnxRuntime;

namespace Rodaje.Module.Vision;

/// <summary>Dónde corre un modelo.</summary>
internal enum ComputeDevice
{
    Cpu,

    /// <summary>DirectML (cualquier GPU con DirectX 12); si no se puede abrir, se vuelve a la CPU.</summary>
    Gpu,
}

/// <summary>
/// Sesiones de ONNX Runtime. DirectML pide memoria sin patrones y ejecución secuencial; en CPU se dejan los hilos por
/// omisión (uno por núcleo físico). Como en modulo-audio-ia, más <paramref name="fuseGraph"/>: sin la fusión de DirectML,
/// BiRefNet_lite pide un tercio de la memoria (la fusión deja tensores intermedios sin reutilizar).
/// </summary>
internal sealed class SessionFactory(ComputeDevice device, int cpuThreads = 0, bool fuseGraph = true)
{
    private bool _gpu = device == ComputeDevice.Gpu;

    /// <summary>«directml» o «cpu» (también si DirectML no se pudo abrir).</summary>
    public string Device => _gpu ? "directml" : "cpu";

    /// <param name="dimensions">Dimensiones libres que se fijan (nombre simbólico → tamaño).</param>
    public InferenceSession Create(string modelPath, IReadOnlyDictionary<string, long>? dimensions = null)
    {
        if (_gpu)
        {
            try
            {
                using SessionOptions gpu = new() { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
                if (!fuseGraph)
                {
                    gpu.AddSessionConfigEntry("ep.dml.disable_graph_fusion", "1");
                }

                foreach ((string name, long size) in dimensions ?? new Dictionary<string, long>())
                {
                    gpu.AddFreeDimensionOverrideByName(name, size);
                }

                gpu.AppendExecutionProvider_DML(0);
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
