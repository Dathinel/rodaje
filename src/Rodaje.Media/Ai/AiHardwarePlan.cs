namespace Rodaje.Media.Ai;

/// <summary>Dónde corre un modelo.</summary>
public enum AiDevice
{
    Cpu,
    Gpu,
}

/// <summary>Variantes de Whisper que trae (o puede traer) el módulo de subtítulos.</summary>
public enum WhisperModel
{
    /// <summary>large-v3-turbo q5_0: el mejor; necesita unos 1,5 GB de VRAM o mucha CPU.</summary>
    Turbo,

    /// <summary>small q5_1: el de la CPU (3,9× tiempo real con 16 hilos, WER 10 %).</summary>
    Small,

    /// <summary>base q5_1: el más rápido y el peor (WER 28 %); solo con «rápida» o con muy poca RAM.</summary>
    Base,
}

/// <summary>Subtítulos: modelo, dispositivo, hilos de la CPU y cuántos tramos se transcriben a la vez.</summary>
/// <param name="Device">Dónde corre.</param>
/// <param name="Model">Variante de Whisper.</param>
/// <param name="Threads">Hilos de whisper.cpp por tramo.</param>
/// <param name="Parallel">Tramos a la vez (cada uno con su estado de Whisper; el modelo se comparte).</param>
/// <param name="VramMb">Lo que se reserva de la VRAM (0 en la CPU).</param>
/// <param name="RamMb">RAM que necesita.</param>
public sealed record SubtitlesPlan(AiDevice Device, WhisperModel Model, int Threads, int Parallel, int VramMb, int RamMb);

/// <summary>Traducción (Qwen3-4B): capas en la GPU (0 = CPU, <see cref="AiHardwarePlan.AllLayers"/> = todas), hilos, memoria.</summary>
public sealed record TranslatePlan(AiDevice Device, int GpuLayers, int Threads, int VramMb, int RamMb)
{
    /// <summary>Fracción del modelo en la GPU (0 a 1).</summary>
    public double GpuFraction => GpuLayers >= AiHardwarePlan.AllLayers ? 1 : (double)GpuLayers / AiHardwarePlan.TranslateLayers;
}

/// <summary>Visión: dispositivo, lado de trabajo de MODNet, si la calidad alta (BiRefNet) cabe en la GPU, lote, hilos y cuadros a la vez en la CPU.</summary>
/// <param name="Device">Dónde corren MODNet y la detección.</param>
/// <param name="MaskSide">Lado de trabajo de MODNet (múltiplo de 32; el área es lado²).</param>
/// <param name="HighOnGpu">BiRefNet_lite cabe en la GPU; si no, la calidad alta va por la CPU.</param>
/// <param name="FramesPerCall">Cuadros por llamada a «quitar-fondo» (el protocolo admite hasta 8 pares).</param>
/// <param name="Threads">Hilos de ONNX Runtime por cuadro en la CPU.</param>
/// <param name="Parallel">Cuadros que el módulo infiere a la vez en la CPU (en la GPU, 1: DirectML serializa).</param>
/// <param name="VramMb">VRAM que se reserva con MODNet.</param>
/// <param name="HighVramMb">VRAM que pide BiRefNet_lite.</param>
public sealed record VisionPlan(AiDevice Device, int MaskSide, bool HighOnGpu, int FramesPerCall, int Threads, int Parallel, int VramMb, int HighVramMb);

/// <summary>
/// Lo que decide <see cref="AiHardwarePlan.For"/> para un equipo y una calidad: el presupuesto de VRAM de los módulos,
/// cuántos pesados y cuántos de CPU corren a la vez y el plan de cada módulo. Puro: mismas entradas, mismo plan.
/// </summary>
/// <param name="Machine">El equipo.</param>
/// <param name="Quality">La calidad pedida.</param>
/// <param name="GpuBudgetMb">VRAM para los módulos (la dedicada menos la reserva de la vista previa; en una integrada, un tope de la compartida).</param>
/// <param name="MaxHeavyAtOnce">Módulos pesados (≥ 1 GB) que pueden convivir en la GPU si caben.</param>
/// <param name="CpuSlots">Trabajos de IA por CPU a la vez (ruido, voz, y los de GPU que van por la CPU).</param>
/// <param name="CpuThreads">Hilos de cómputo por trabajo de CPU (uno por núcleo físico).</param>
/// <param name="Subtitles">Plan de los subtítulos.</param>
/// <param name="Translate">Plan del traductor.</param>
/// <param name="Vision">Plan de la visión.</param>
public sealed record AiPlan(AiMachine Machine, AiQuality Quality, int GpuBudgetMb, int MaxHeavyAtOnce, int CpuSlots, int CpuThreads,
    SubtitlesPlan Subtitles, TranslatePlan Translate, VisionPlan Vision)
{
    /// <summary>Dos módulos ligeros (ruido, voz, detección) pueden ir a la vez.</summary>
    public bool LightModulesTogether => CpuSlots >= 2;

    /// <summary>El mismo plan con todo en la CPU (si la GPU falla a mitad).</summary>
    public AiPlan OnCpu() => AiHardwarePlan.For(Machine with { HasGpu = false, Integrated = false, DedicatedVramMb = 0, SharedVramMb = 0 }, Quality);
}

/// <summary>
/// Decide cómo corre cada módulo de IA en un equipo: de una integrada con 8 GB de RAM a una GPU de 24 GB con 32 hilos.
/// Las cifras de memoria son las medidas en una RTX 3050 de 4 GB (protocolo §«Límites», D-037); las de velocidad, en
/// <see cref="AiEstimator"/>.
/// </summary>
public static class AiHardwarePlan
{
    /// <summary>Lo máximo que se aparta para la vista previa (3/8 de la VRAM, hasta 3 GB).</summary>
    public const int PreviewReserveMaxMb = 3072;

    public const int TurboVramMb = 1500;
    public const int SmallVramMb = 700;
    public const int BaseVramMb = 350;

    /// <summary>VRAM de cada tramo de Whisper extra a la vez (su estado, no el modelo).</summary>
    public const int WhisperParallelVramMb = 350;

    public const int TurboRamMb = 1300;
    public const int SmallRamMb = 650;
    public const int BaseRamMb = 350;

    /// <summary>Capas de Qwen3-4B.</summary>
    public const int TranslateLayers = 36;

    /// <summary>Lo que llama.cpp entiende por «todas las capas» (también la de salida).</summary>
    public const int AllLayers = 99;

    /// <summary>Qwen3-4B Q4_K_M entero en la GPU: pesos, caché de 3072 tokens y búferes de cómputo.</summary>
    public const int TranslateFullVramMb = 3300;

    /// <summary>Cada capa en la GPU: unos 69 MB de pesos y 13 MB de caché.</summary>
    public const int TranslateLayerVramMb = 82;

    /// <summary>Búferes de cómputo de Vulkan con el modelo repartido.</summary>
    public const int TranslateOverheadVramMb = 450;

    /// <summary>Con menos capas en la GPU no compensa: todo a la CPU.</summary>
    public const int TranslateMinLayers = 8;

    public const int TranslateModelRamMb = 2500;
    public const int TranslateExtraRamMb = 700;

    public const int BiRefNetVramMb = 2400;

    /// <summary>El plan para <paramref name="machine"/> con la calidad <paramref name="quality"/>.</summary>
    public static AiPlan For(AiMachine machine, AiQuality quality = AiQuality.Auto)
    {
        ArgumentNullException.ThrowIfNull(machine);
        int budget = GpuBudgetMb(machine, quality);
        int threads = ComputeThreads(machine.LogicalCores);
        int heavy = budget >= 18_000 ? 3 : budget >= 9_000 ? 2 : 1;
        int cpuSlots = machine.LogicalCores <= 4 || machine.RamTotalMb <= 8192 ? 1 : machine.LogicalCores <= 12 ? 2 : 3;
        return new AiPlan(machine, quality, budget, heavy, cpuSlots, threads,
            Subtitles(machine, quality, budget, threads), Translate(machine, budget, threads), Vision(machine, quality, budget, threads));
    }

    /// <summary>
    /// VRAM para los módulos. Dedicada: lo que deja la vista previa (3/8, hasta 3 GB). Integrada: la reserva propia más
    /// un cuarto de la compartida, sin pasar de un cuarto de la RAM ni de 2 GB (1,5 con «rápida», 3 con «máxima»).
    /// </summary>
    public static int GpuBudgetMb(AiMachine machine, AiQuality quality = AiQuality.Auto)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (!machine.HasGpu)
        {
            return 0;
        }

        if (machine.Integrated)
        {
            int cap = quality switch { AiQuality.Fast => 1536, AiQuality.Max => 3072, _ => 2048 };
            int shared = Math.Min(machine.SharedVramMb / 4, machine.RamTotalMb / 4);
            return Math.Max(0, Math.Min(cap, machine.DedicatedVramMb + shared));
        }

        int vram = machine.DedicatedVramMb;
        return Math.Max(0, vram - Math.Min(vram * 3 / 8, PreviewReserveMaxMb));
    }

    /// <summary>Hilos de cómputo: uno por núcleo físico (con SMT, la mitad de los lógicos); con 4 o menos, todos.</summary>
    public static int ComputeThreads(int logicalCores) => logicalCores >= 8 ? logicalCores / 2 : Math.Max(1, logicalCores);

    private static SubtitlesPlan Subtitles(AiMachine machine, AiQuality quality, int budget, int threads)
    {
        if (machine.HasGpu && budget >= SmallVramMb)
        {
            WhisperModel model = budget >= TurboVramMb && quality != AiQuality.Fast ? WhisperModel.Turbo : WhisperModel.Small;
            int modelVram = model == WhisperModel.Turbo ? TurboVramMb : SmallVramMb;
            // Dos tramos a la vez en una RTX 3050: 3 min de voz en 17,7 s en lugar de 29,6 con el mismo WER. Cada tramo
            // extra cuesta su estado; en una integrada no se mide y va de a uno.
            int cap = machine.Integrated ? 1 : budget >= 12_000 ? 4 : budget >= 6_000 ? 3 : 2;
            int parallel = Math.Max(1, Math.Min(cap, 1 + ((budget - modelVram) / WhisperParallelVramMb)));
            int vram = modelVram + ((parallel - 1) * WhisperParallelVramMb);
            // En la GPU la CPU solo prepara: con 8 hilos sobra.
            return new SubtitlesPlan(AiDevice.Gpu, model, Math.Min(8, threads), parallel, vram, 600);
        }

        int free = machine.RamFreeMb;
        WhisperModel cpu = quality == AiQuality.Max && machine.LogicalCores >= 16 && free >= TurboRamMb + 1024 ? WhisperModel.Turbo
            : quality == AiQuality.Fast || free < SmallRamMb + 256 ? WhisperModel.Base
            : WhisperModel.Small;
        // whisper.cpp escala mal con muchos hilos: con 16, dos tramos de 4 hilos van 1,5 veces más rápido que uno de 8.
        int cpuParallel = machine.LogicalCores >= 16 ? 2 : 1;
        int ram = cpu switch { WhisperModel.Turbo => TurboRamMb, WhisperModel.Small => SmallRamMb, _ => BaseRamMb };
        return new SubtitlesPlan(AiDevice.Cpu, cpu, Math.Max(1, threads / cpuParallel), cpuParallel, 0, ram + ((cpuParallel - 1) * 200));
    }

    private static TranslatePlan Translate(AiMachine machine, int budget, int threads)
    {
        int layers = 0;
        // En una integrada la «VRAM» es la misma RAM y la CPU genera igual de rápido: llama.cpp va por la CPU.
        if (machine.HasGpu && !machine.Integrated)
        {
            layers = budget >= TranslateFullVramMb ? AllLayers : Math.Max(0, (budget - TranslateOverheadVramMb) / TranslateLayerVramMb);
            if (layers < TranslateMinLayers)
            {
                layers = 0;
            }
        }

        int onGpu = Math.Min(layers, TranslateLayers);
        int vram = layers >= AllLayers ? TranslateFullVramMb : layers > 0 ? TranslateOverheadVramMb + (layers * TranslateLayerVramMb) : 0;
        int ram = (TranslateModelRamMb * (TranslateLayers - onGpu) / TranslateLayers) + TranslateExtraRamMb;
        return new TranslatePlan(layers > 0 ? AiDevice.Gpu : AiDevice.Cpu, layers, threads, vram, ram);
    }

    private static VisionPlan Vision(AiMachine machine, AiQuality quality, int budget, int threads)
    {
        bool gpu = machine.HasGpu && budget >= 400;
        int side = quality == AiQuality.Fast || (!gpu && machine.LogicalCores <= 4) ? 384
            : quality == AiQuality.Max && gpu && budget >= 4000 ? 640
            : 512;
        int vram = !gpu ? 0 : side <= 384 ? 400 : side <= 512 ? 600 : 900;
        bool high = machine.HasGpu && budget >= BiRefNetVramMb;
        int frames = (gpu ? budget < 1000 : machine.RamTotalMb <= 8192) ? 4 : 8;
        int parallel = !gpu && machine.LogicalCores >= 16 ? 2 : 1;
        return new VisionPlan(gpu ? AiDevice.Gpu : AiDevice.Cpu, side, high, frames, Math.Max(1, threads / parallel), parallel, vram, BiRefNetVramMb);
    }
}
