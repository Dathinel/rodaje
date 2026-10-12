using Microsoft.ML.OnnxRuntime;

namespace Rodaje.Module.AudioAi;

/// <summary>Dónde corre un modelo.</summary>
internal enum ComputeDevice
{
    Cpu,

    /// <summary>DirectML (cualquier GPU con DirectX 12); si no se puede abrir, se vuelve a la CPU.</summary>
    Gpu,
}

/// <summary>
/// Sesiones de ONNX Runtime. DirectML pide memoria sin patrones y ejecución secuencial; en CPU se dejan los hilos por
/// omisión (uno por núcleo físico). Aislado aquí para pasar a Windows ML sin tocar los modelos (riesgo de la fase 7).
/// </summary>
internal sealed class SessionFactory(ComputeDevice device, int cpuThreads = 0)
{
    private bool _gpu = device == ComputeDevice.Gpu;

    /// <summary>«directml» o «cpu» (también si DirectML no se pudo abrir).</summary>
    public string Device => _gpu ? "directml" : "cpu";

    public InferenceSession Create(string modelPath)
    {
        if (_gpu)
        {
            try
            {
                using SessionOptions gpu = new() { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
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
