using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Rodaje.Module.Vision;

/// <summary>Salidas de un modelo, ya en float, y dónde corrió.</summary>
internal sealed record ModelOutput(float[][] Values, string Device);

/// <summary>Una sesión abierta. DirectML no admite <c>Run</c> concurrentes en la misma sesión: se serializan; en la CPU van a la vez.</summary>
internal sealed class LoadedModel(InferenceSession session, string device) : IDisposable
{
    private readonly ReaderWriterLockSlim _gate = new();
    private readonly bool _half = session.InputMetadata.Values.First().ElementDataType == TensorElementType.Float16;
    private bool _disposed;

    public string Device { get; } = device;

    public long LastUse { get; set; }

    public float[][] Run(string input, float[] data, long[] shape, string[] outputs)
    {
        using OrtValue value = _half
            ? OrtValue.CreateTensorValueFromMemory(Array.ConvertAll(data, v => (Float16)v), shape)
            : OrtValue.CreateTensorValueFromMemory(data, shape);
        using RunOptions options = new();
        bool exclusive = Device == "directml";
        if (exclusive)
        {
            _gate.EnterWriteLock();
        }
        else
        {
            _gate.EnterReadLock();
        }

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using IDisposableReadOnlyCollection<OrtValue> results = session.Run(options, [input], [value], outputs);
            return [.. results.Select(r => _half ? Widen(r.GetTensorDataAsSpan<Float16>()) : r.GetTensorDataAsSpan<float>().ToArray())];
        }
        finally
        {
            if (exclusive)
            {
                _gate.ExitWriteLock();
            }
            else
            {
                _gate.ExitReadLock();
            }
        }
    }

    public void Dispose()
    {
        _gate.EnterWriteLock();
        try
        {
            _disposed = true;
            session.Dispose();
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private static float[] Widen(ReadOnlySpan<Float16> values)
    {
        var result = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = (float)values[i];
        }

        return result;
    }
}

/// <summary>
/// Las sesiones de los modelos, abiertas al primer uso y reutilizadas. Un modelo con <see cref="ModelFile.FreeDimensions"/>
/// abre una sesión por forma de entrada con esas dimensiones fijadas (en DirectML va un 25 % más rápido: compila el grafo
/// entero); se guardan las dos últimas formas. Si DirectML falla al correr (por ejemplo, sin memoria de video), ese modelo
/// pasa a la CPU para el resto de la vida del proceso.
/// </summary>
internal sealed class VisionModels(ModelFiles files) : IDisposable
{
    private const int ShapesPerModel = 2;
    private readonly Dictionary<string, LoadedModel> _loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _gpuFailed = [];
    private readonly Lock _gate = new();
    private long _clock;
    private int _cpuThreads;

    /// <summary>Hilos de ONNX Runtime de las sesiones de CPU que se abran desde ahora (0 = uno por núcleo físico).</summary>
    public int CpuThreads
    {
        get => Volatile.Read(ref _cpuThreads);
        set => Volatile.Write(ref _cpuThreads, Math.Clamp(value, 0, 64));
    }

    public ModelOutput Run(ModelFile model, ComputeDevice device, string input, float[] data, long[] shape, string[] outputs)
    {
        for (int attempt = 0; ; attempt++)
        {
            LoadedModel loaded = Get(model, device, shape);
            try
            {
                return new ModelOutput(loaded.Run(input, data, shape, outputs), loaded.Device);
            }
            catch (ObjectDisposedException) when (attempt == 0)
            {
            }
            catch (OnnxRuntimeException) when (loaded.Device == "directml")
            {
                lock (_gate)
                {
                    _gpuFailed.Add(model.FileName);
                }

                LoadedModel cpu = Get(model, ComputeDevice.Cpu, shape);
                return new ModelOutput(cpu.Run(input, data, shape, outputs), cpu.Device);
            }
        }
    }

    private LoadedModel Get(ModelFile model, ComputeDevice device, long[] shape)
    {
        lock (_gate)
        {
            if (_gpuFailed.Contains(model.FileName))
            {
                device = ComputeDevice.Cpu;
            }

            bool fixedShape = model.FreeDimensions is not null && device == ComputeDevice.Gpu;
            int threads = device == ComputeDevice.Cpu ? CpuThreads : 0;
            string prefix = $"{model.FileName}|{device}|{threads}|";
            string key = prefix + (fixedShape ? string.Join('x', shape) : "");
            if (!_loaded.TryGetValue(key, out LoadedModel? loaded))
            {
                var overrides = new Dictionary<string, long>(StringComparer.Ordinal);
                for (int i = 0; fixedShape && i < shape.Length; i++)
                {
                    if (model.FreeDimensions![i] is { Length: > 0 } name)
                    {
                        overrides[name] = shape[i];
                    }
                }

                var siblings = _loaded.Where(l => l.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(l => l.Value.LastUse).ToList();
                for (int i = 0; i <= siblings.Count - ShapesPerModel; i++)
                {
                    _loaded.Remove(siblings[i].Key);
                    siblings[i].Value.Dispose();
                }

                var factory = new SessionFactory(device, threads, fuseGraph: !model.NoGraphFusion);
                loaded = new LoadedModel(factory.Create(files.Path(model), overrides), factory.Device);
                _loaded[key] = loaded;
            }

            loaded.LastUse = ++_clock;
            return loaded;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (LoadedModel loaded in _loaded.Values)
            {
                loaded.Dispose();
            }

            _loaded.Clear();
        }
    }
}
