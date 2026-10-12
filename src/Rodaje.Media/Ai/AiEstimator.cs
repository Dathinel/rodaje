using System.Globalization;

namespace Rodaje.Media.Ai;

/// <summary>Lo que se espera de una función en este equipo: cuánto tarda, dónde corre y, si la memoria no alcanza, qué hacer.</summary>
/// <param name="Seconds">Segundos estimados, con la carga del modelo.</param>
/// <param name="Key">Clave de la medida (<c>función|variante|dispositivo</c>) con la que se aprende la velocidad real.</param>
/// <param name="Where">«GPU · Whisper large-v3-turbo», «CPU · 8 hilos»…</param>
/// <param name="Warning">Por qué no alcanza la memoria, o <c>null</c>.</param>
/// <param name="Alternative">Qué se ofrece en su lugar («calidad normal», «CPU»), o <c>null</c>.</param>
/// <param name="Measured">La velocidad sale de corridas anteriores en este equipo, no de la tabla.</param>
public sealed record AiEstimate(double Seconds, string Key, string Where, string? Warning = null, string? Alternative = null, bool Measured = false)
{
    /// <summary>«En este equipo: unos 3 min (GPU · Whisper large-v3-turbo).»</summary>
    public string Summary => $"En este equipo: {AiEstimator.Format(Seconds)} ({Where}).";
}

/// <summary>Velocidades medidas en este equipo (unidades por segundo por clave), o <c>null</c> si no hay.</summary>
public interface IAiMeasuredRates
{
    double? Rate(string key);
}

/// <summary>
/// Estima cuánto tarda cada función con el <see cref="AiPlan"/>. Parte de velocidades de referencia (RTX 3050 de 4 GB y
/// 16 hilos) escaladas por la clase de GPU y los hilos; en cuanto una función corre una vez en el equipo, manda lo medido.
/// Unidades: segundos de audio (subtítulos, ruido), caracteres (traducción, voz) y cuadros (visión).
/// </summary>
public static class AiEstimator
{
    public const string Subtitles = "subtitulos";
    public const string Translate = "traducir";
    public const string RemoveBackground = "quitar-fondo";
    public const string Detect = "detectar";
    public const string NoiseReduction = "reducir-ruido";
    public const string Voice = "voz";

    // Referencia: unidades por segundo en una RTX 3050 de portátil (4 GB) y 16 hilos, afinadas con la corrida real del 12-oct
    // (D-037). Las cargas incluyen abrir el proceso del módulo y el modelo.
    private const double TurboGpu = 25, SmallGpu = 45, BaseGpu = 70;
    private const double TurboCpu = 0.8, SmallCpu = 3.9, BaseCpu = 8.6;
    private const double TranslateGpu = 90, TranslateCpu = 8;
    private const double ModnetGpu = 12, ModnetCpu = 4, BiRefNetGpu = 0.3, BiRefNetCpu = 0.08;
    private const double DetectGpu = 10, DetectCpu = 3;
    private const double NoiseCpu = 25, VoiceCpu = 120;

    /// <summary>Decodificar y escribir cada cuadro en la app (no depende del módulo).</summary>
    private const double FrameOverheadSeconds = 1.0 / 60;

    public static AiEstimate ForSubtitles(AiPlan plan, double audioSeconds, IAiMeasuredRates? measured = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        SubtitlesPlan s = plan.Subtitles;
        bool gpu = s.Device == AiDevice.Gpu;
        double rate = (s.Model, gpu) switch
        {
            (WhisperModel.Turbo, true) => TurboGpu,
            (WhisperModel.Small, true) => SmallGpu,
            (WhisperModel.Base, true) => BaseGpu,
            (WhisperModel.Turbo, false) => TurboCpu,
            (WhisperModel.Small, false) => SmallCpu,
            _ => BaseCpu,
        };
        rate *= (gpu ? GpuFactor(plan.Machine) : CpuFactor(plan.Machine.LogicalCores)) * Math.Pow(s.Parallel, 0.7);
        double load = gpu ? 4 : 8;
        string key = Key(Subtitles, ModelName(s.Model), s.Device);
        string where = $"{DeviceName(s.Device, s.Threads)} · Whisper {ModelName(s.Model)}";
        string? warning = plan.Machine.RamFreeMb < s.RamMb
            ? $"Whisper {ModelName(s.Model)} necesita unos {AiMachine.Gb(s.RamMb)} de RAM libre y hay {AiMachine.Gb(plan.Machine.RamFreeMb)}."
            : null;
        string? alternative = warning is not null && s.Model != WhisperModel.Base ? "Cierra otros programas o elige «Calidad de la IA › Rápida» (Whisper base)." : null;
        return Build(key, audioSeconds, rate, load, where, warning, alternative, measured);
    }

    public static AiEstimate ForTranslate(AiPlan plan, int characters, IAiMeasuredRates? measured = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        TranslatePlan t = plan.Translate;
        double gpuRate = TranslateGpu * GpuFactor(plan.Machine);
        double cpuRate = TranslateCpu * CpuFactor(plan.Machine.LogicalCores);
        // Cada token pasa por todas las capas: el tiempo se suma por partes.
        double f = t.GpuFraction;
        double rate = 1 / ((f / gpuRate) + ((1 - f) / cpuRate));
        string variant = t.GpuLayers >= AiHardwarePlan.AllLayers ? "gpu" : t.GpuLayers > 0 ? "gpu" + t.GpuLayers.ToString(CultureInfo.InvariantCulture) : "cpu";
        string where = t.GpuLayers >= AiHardwarePlan.AllLayers ? "GPU · Qwen3-4B entero"
            : t.GpuLayers > 0 ? $"GPU y CPU · {t.GpuLayers} de {AiHardwarePlan.TranslateLayers} capas en la GPU"
            : DeviceName(AiDevice.Cpu, t.Threads) + " · Qwen3-4B";
        string? warning = plan.Machine.RamFreeMb < t.RamMb
            ? $"El traductor necesita unos {AiMachine.Gb(t.RamMb)} de RAM libre y hay {AiMachine.Gb(plan.Machine.RamFreeMb)}: puede ir muy lento o quedarse sin memoria."
            : null;
        string? alternative = warning is null ? null : "Cierra otros programas antes de traducir, o traduce la pista en tandas más cortas.";
        return Build(Key(Translate, variant, t.Device), Math.Max(0, characters), rate, t.Device == AiDevice.Gpu ? 8 : 15, where, warning, alternative, measured);
    }

    public static AiEstimate ForRemoveBackground(AiPlan plan, int frames, bool high, IAiMeasuredRates? measured = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        VisionPlan v = plan.Vision;
        AiDevice device = high ? (v.HighOnGpu ? AiDevice.Gpu : AiDevice.Cpu) : v.Device;
        bool gpu = device == AiDevice.Gpu;
        double rate = high ? (gpu ? BiRefNetGpu : BiRefNetCpu) : (gpu ? ModnetGpu : ModnetCpu) * Math.Pow(512.0 / v.MaskSide, 2);
        rate *= gpu ? GpuFactor(plan.Machine) : CpuFactor(plan.Machine.LogicalCores) * Math.Pow(v.Parallel, 0.6);
        rate = 1 / ((1 / rate) + FrameOverheadSeconds);
        string variant = high ? "birefnet" : "modnet" + v.MaskSide.ToString(CultureInfo.InvariantCulture);
        string where = $"{DeviceName(device, v.Threads)} · {(high ? "BiRefNet_lite" : "MODNet")}";
        string? warning = null, alternative = null;
        if (high && !v.HighOnGpu)
        {
            warning = plan.Machine.HasGpu
                ? $"La calidad alta necesita unos {AiMachine.Gb(v.HighVramMb)} de VRAM y este equipo deja {AiMachine.Gb(plan.GpuBudgetMb)} para la IA: irá por la CPU, mucho más lenta."
                : "La calidad alta va por la CPU en este equipo: es mucho más lenta.";
            alternative = "Usar la calidad normal (MODNet)";
        }

        return Build(Key(RemoveBackground, variant, device), Math.Max(0, frames), rate, 2, where, warning, alternative, measured);
    }

    public static AiEstimate ForDetect(AiPlan plan, int frames, IAiMeasuredRates? measured = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        bool gpu = plan.Vision.Device == AiDevice.Gpu;
        double rate = gpu ? DetectGpu * GpuFactor(plan.Machine) : DetectCpu * CpuFactor(plan.Machine.LogicalCores);
        rate = 1 / ((1 / rate) + FrameOverheadSeconds);
        return Build(Key(Detect, "rtdetr", plan.Vision.Device), Math.Max(0, frames), rate, 5,
            DeviceName(plan.Vision.Device, plan.Vision.Threads) + " · YuNet / RT-DETRv2", null, null, measured);
    }

    public static AiEstimate ForNoiseReduction(AiPlan plan, double audioSeconds, IAiMeasuredRates? measured = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Build(Key(NoiseReduction, "deepfilternet3", AiDevice.Cpu), audioSeconds, NoiseCpu * CpuFactor(plan.Machine.LogicalCores), 4,
            DeviceName(AiDevice.Cpu, plan.CpuThreads) + " · DeepFilterNet3", null, null, measured);
    }

    public static AiEstimate ForVoice(AiPlan plan, int characters, IAiMeasuredRates? measured = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Build(Key(Voice, "kokoro", AiDevice.Cpu), Math.Max(0, characters), VoiceCpu * CpuFactor(plan.Machine.LogicalCores), 4,
            DeviceName(AiDevice.Cpu, plan.CpuThreads) + " · Kokoro", null, null, measured);
    }

    public static string Key(string function, string variant, AiDevice device) =>
        $"{function}|{variant}|{(device == AiDevice.Gpu ? "gpu" : "cpu")}";

    public static string ModelName(WhisperModel model) => model switch
    {
        WhisperModel.Turbo => "large-v3-turbo",
        WhisperModel.Small => "small",
        _ => "base",
    };

    /// <summary>«unos 40 s», «unos 3 min», «unos 1 h 20 min».</summary>
    public static string Format(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            return "un tiempo que no se puede estimar";
        }

        if (seconds < 10)
        {
            return "unos segundos";
        }

        if (seconds < 90)
        {
            return string.Create(CultureInfo.InvariantCulture, $"unos {Math.Round(seconds / 5) * 5:0} s");
        }

        double minutes = seconds / 60;
        if (minutes < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"unos {Math.Round(minutes):0} min");
        }

        int hours = (int)(minutes / 60);
        int rest = (int)Math.Round((minutes - (hours * 60)) / 10) * 10;
        if (rest == 60)
        {
            (hours, rest) = (hours + 1, 0);
        }

        return rest == 0
            ? string.Create(CultureInfo.InvariantCulture, $"unas {hours} h")
            : string.Create(CultureInfo.InvariantCulture, $"unas {hours} h {rest} min");
    }

    /// <summary>Hilos: la referencia es 16; con más ayuda menos que en proporción.</summary>
    internal static double CpuFactor(int logicalCores) => Math.Pow(Math.Clamp(logicalCores, 1, 64) / 16.0, 0.8);

    /// <summary>La clase de la GPU por su VRAM (no hay otra cifra fiable sin medir): 4 GB = 1.</summary>
    internal static double GpuFactor(AiMachine machine) =>
        machine.Integrated ? 0.3
        : machine.DedicatedVramMb <= 2048 ? 0.5
        : machine.DedicatedVramMb <= 4096 ? 1.0
        : machine.DedicatedVramMb <= 8192 ? 1.8
        : machine.DedicatedVramMb <= 12_288 ? 2.5
        : 3.5;

    private static string DeviceName(AiDevice device, int threads) =>
        device == AiDevice.Gpu ? "GPU" : string.Create(CultureInfo.InvariantCulture, $"CPU · {threads} hilos");

    private static AiEstimate Build(string key, double units, double rate, double load, string where, string? warning, string? alternative, IAiMeasuredRates? measured)
    {
        if (measured?.Rate(key) is { } real && real > 0 && double.IsFinite(real))
        {
            return new AiEstimate(units / real, key, where, warning, alternative, Measured: true);
        }

        return new AiEstimate(load + (units / Math.Max(rate, 1e-6)), key, where, warning, alternative);
    }
}
