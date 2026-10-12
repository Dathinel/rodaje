using Microsoft.ML.OnnxRuntime;

namespace Rodaje.Module.AudioAi;

/// <summary>
/// Sesiones de ONNX Runtime, solo en CPU (D-034): con las GRU de DeepFilterNet3 la GPU por DirectML iba más lenta que la
/// CPU y sumaba un paquete. Se dejan los hilos por omisión (uno por núcleo físico).
/// </summary>
internal sealed class SessionFactory(int cpuThreads = 0)
{
    public const string Device = "cpu";

    public InferenceSession Create(string modelPath)
    {
        using SessionOptions cpu = new() { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (cpuThreads > 0)
        {
            cpu.IntraOpNumThreads = cpuThreads;
        }

        return new InferenceSession(modelPath, cpu);
    }
}
