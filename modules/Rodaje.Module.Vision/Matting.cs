namespace Rodaje.Module.Vision;

/// <summary>Alfa a la resolución del modelo (0..1) con la luminancia del mismo tamaño, para el filtro temporal.</summary>
internal sealed record MatteResult(float[] Alpha, float[] Gray, int Width, int Height, string Model, string Device);

/// <summary>Un cuadro listo para el modelo: tensor CHW, luminancia y tamaño.</summary>
internal sealed record MatteInput(float[] Tensor, float[] Gray, int Width, int Height, bool High);

/// <summary>
/// Quitar el fondo. «normal»: MODNet (retrato) a unos 512×512 píxeles de área, lados múltiplos de 32. «alta»: BiRefNet_lite
/// en fp16 a 1024×1024 fijo (estirado, como se entrenó), con sigmoide a la salida.
/// </summary>
internal sealed class Matting(VisionModels models)
{
    private const int ModnetArea = 512 * 512;
    private static readonly PlanarFormat ModnetFormat = PlanarFormat.Normalized(true, [0.5f, 0.5f, 0.5f], [0.5f, 0.5f, 0.5f]);
    private static readonly PlanarFormat ImageNetFormat = PlanarFormat.Normalized(true, [0.485f, 0.456f, 0.406f], [0.229f, 0.224f, 0.225f]);

    public MatteResult Run(BgraFrame frame, bool high, ComputeDevice device) => Infer(Prepare(frame, high), device);

    /// <summary>La parte de CPU (remuestreo y normalización): se adelanta mientras la GPU corre el cuadro anterior.</summary>
    public static MatteInput Prepare(BgraFrame frame, bool high)
    {
        (int w, int h) = high ? (1024, 1024) : ModnetSize(frame.Width, frame.Height);
        var tensor = new float[3 * w * h];
        var gray = new float[w * h];
        Imaging.ToPlanar(frame, tensor, 0, w, h, w, h, high ? ImageNetFormat : ModnetFormat, gray);
        return new MatteInput(tensor, gray, w, h, high);
    }

    public MatteResult Infer(MatteInput input, ComputeDevice device)
    {
        (float[] tensor, float[] gray, int w, int h, bool high) = input;
        ModelOutput result = high
            ? models.Run(ModelFiles.BiRefNetLite, device, "input_image", tensor, [1, 3, h, w], ["output_image"])
            : models.Run(ModelFiles.Modnet, device, "input", tensor, [1, 3, h, w], ["output"]);
        float[] alpha = result.Values[0];
        if (high)
        {
            for (int i = 0; i < alpha.Length; i++)
            {
                alpha[i] = 1f / (1f + MathF.Exp(-alpha[i]));
            }
        }

        return new MatteResult(alpha, gray, w, h, high ? "birefnet-lite" : "modnet", result.Device);
    }

    internal static (int Width, int Height) ModnetSize(int width, int height)
    {
        double scale = Math.Sqrt(ModnetArea / ((double)width * height));
        return (Math.Max(32, (int)Math.Round(width * scale / 32) * 32), Math.Max(32, (int)Math.Round(height * scale / 32) * 32));
    }
}

/// <summary>
/// Estabilidad temporal de la máscara por sesión (cuadros consecutivos de un clip). Media exponencial guiada por el
/// movimiento: donde la imagen no cambia, la máscara nueva pesa poco y el borde deja de temblar; donde hay movimiento (o la
/// máscara cambia de verdad) manda el cuadro nuevo. Después, histéresis: cambios menores que una banda muerta se ignoran.
/// Un corte de plano (la imagen cambia mucho) reinicia el estado.
/// </summary>
internal sealed class TemporalFilter
{
    private const int MaxSessions = 32;
    private const float BaseWeight = 0.3f;
    private const float MotionFloor = 0.03f;
    private const float MotionGain = 10f;
    private const float DeadBand = 0.03f;
    private const float SceneCut = 0.12f;

    private readonly Dictionary<string, State> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private long _clock;

    /// <summary>Devuelve el alfa estabilizado (puede ser el mismo arreglo) y guarda el estado de <paramref name="session"/>.</summary>
    public float[] Apply(string? session, bool reset, MatteResult matte)
    {
        if (session is null)
        {
            return matte.Alpha;
        }

        State state;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out state!))
            {
                if (_sessions.Count >= MaxSessions)
                {
                    _sessions.Remove(_sessions.MinBy(s => s.Value.LastUse).Key);
                }

                state = new State();
                _sessions[session] = state;
            }

            state.LastUse = ++_clock;
        }

        lock (state)
        {
            if (reset || state.Alpha is null || state.Width != matte.Width || state.Height != matte.Height || Difference(state.Gray!, matte.Gray) > SceneCut)
            {
                state.Alpha = (float[])matte.Alpha.Clone();
                state.Gray = matte.Gray;
                (state.Width, state.Height) = (matte.Width, matte.Height);
                return matte.Alpha;
            }

            float[] motion = Motion(state.Gray!, matte.Gray, matte.Width, matte.Height);
            float[] previous = state.Alpha;
            float[] current = matte.Alpha;
            var result = new float[current.Length];
            for (int i = 0; i < current.Length; i++)
            {
                float change = Math.Abs(current[i] - previous[i]);
                float weight = Math.Clamp(Math.Max(BaseWeight + (Math.Max(0, motion[i] - MotionFloor) * MotionGain), (change - 0.3f) * 2f), 0f, 1f);
                float value = previous[i] + ((current[i] - previous[i]) * weight);
                if (Math.Abs(value - previous[i]) < DeadBand)
                {
                    value = previous[i];
                }

                result[i] = value < 0.02f ? 0f : value > 0.98f ? 1f : value;
            }

            state.Alpha = result;
            state.Gray = matte.Gray;
            return result;
        }
    }

    private static float Difference(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return (float)(sum / a.Length);
    }

    /// <summary>Diferencia absoluta entre cuadros con un desenfoque de caja de 5×5 (el ruido del sensor no cuenta como movimiento).</summary>
    private static float[] Motion(float[] previous, float[] current, int width, int height)
    {
        var diff = new float[current.Length];
        for (int i = 0; i < diff.Length; i++)
        {
            diff[i] = Math.Abs(current[i] - previous[i]);
        }

        var rows = new float[diff.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                for (int k = -2; k <= 2; k++)
                {
                    sum += diff[(y * width) + Math.Clamp(x + k, 0, width - 1)];
                }

                rows[(y * width) + x] = sum / 5;
            }
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                for (int k = -2; k <= 2; k++)
                {
                    sum += rows[(Math.Clamp(y + k, 0, height - 1) * width) + x];
                }

                diff[(y * width) + x] = sum / 5;
            }
        }

        return diff;
    }

    private sealed class State
    {
        public float[]? Alpha;
        public float[]? Gray;
        public int Width;
        public int Height;
        public long LastUse;
    }
}
