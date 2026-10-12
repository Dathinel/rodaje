using System.Text.Json;
using Rodaje.Media.Ai;

namespace Rodaje.App.Services.Modules;

/// <summary>
/// El plan de IA convertido en los parámetros de cada módulo. Son campos opcionales: los módulos v1.0 ignoran los que no
/// conocen (<c>parallel</c>, <c>lado</c>, <c>hilos</c>, <c>paralelo</c>) y entienden los que ya tenían (<c>device</c>,
/// <c>threads</c>, <c>model</c>, <c>cpuModel</c>, <c>gpuLayers</c>, <c>dispositivo</c>). Ver protocolo-modulos.md §«Perfil de IA».
/// </summary>
public static class AiParameters
{
    public const string TurboFile = "ggml-large-v3-turbo-q5_0.bin";
    public const string SmallFile = "ggml-small-q5_1.bin";
    public const string BaseFile = "ggml-base-q5_1.bin";

    public static string WhisperFile(WhisperModel model) => model switch
    {
        WhisperModel.Turbo => TurboFile,
        WhisperModel.Small => SmallFile,
        _ => BaseFile,
    };

    /// <summary>
    /// El plan de subtítulos con una variante que esté instalada: si falta la pedida, la siguiente (base → small → turbo;
    /// turbo → small). <paramref name="exists"/> dice si hay un archivo de modelo.
    /// </summary>
    public static AiPlan WithInstalledWhisper(AiPlan plan, Func<WhisperModel, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(exists);
        WhisperModel wanted = plan.Subtitles.Model;
        WhisperModel[] order = wanted switch
        {
            WhisperModel.Base => [WhisperModel.Base, WhisperModel.Small, WhisperModel.Turbo],
            WhisperModel.Small => [WhisperModel.Small, WhisperModel.Turbo],
            _ => [WhisperModel.Turbo, WhisperModel.Small],
        };
        WhisperModel used = order.FirstOrDefault(exists, wanted);
        if (used == wanted)
        {
            return plan;
        }

        int vram = plan.Subtitles.Device != AiDevice.Gpu ? 0 : used switch
        {
            WhisperModel.Turbo => AiHardwarePlan.TurboVramMb,
            WhisperModel.Small => AiHardwarePlan.SmallVramMb,
            _ => AiHardwarePlan.BaseVramMb,
        } + ((plan.Subtitles.Parallel - 1) * AiHardwarePlan.WhisperParallelVramMb);
        return plan with { Subtitles = plan.Subtitles with { Model = used, VramMb = vram } };
    }

    /// <summary>
    /// <c>subtitulos</c>: en la GPU, <c>device</c> «auto» con el modelo de la GPU y el de la CPU de repuesto (si la GPU falla,
    /// el módulo sigue en la CPU); en la CPU, «cpu» con su modelo. <paramref name="path"/> da la ruta de cada variante.
    /// </summary>
    public static JsonDocument Subtitles(string language, AiPlan plan, Func<WhisperModel, string?> path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(path);
        SubtitlesPlan s = plan.Subtitles;
        return Write(w =>
        {
            w.WriteString("language", language);
            w.WriteString("device", s.Device == AiDevice.Gpu ? "auto" : "cpu");
            w.WriteNumber("threads", s.Threads);
            w.WriteNumber("parallel", s.Parallel);
            if (s.Device == AiDevice.Gpu)
            {
                WriteIf(w, "model", path(s.Model));
                WriteIf(w, "cpuModel", path(plan.OnCpu().Subtitles.Model));
            }
            else
            {
                WriteIf(w, "cpuModel", path(s.Model));
            }
        });
    }

    /// <summary>Lo que se le añade a cada llamada a <c>traducir</c>: dispositivo, capas en la GPU (descarga parcial) e hilos.</summary>
    public static void WriteTranslate(Utf8JsonWriter writer, TranslatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(plan);
        writer.WriteString("device", plan.GpuLayers > 0 ? "auto" : "cpu");
        writer.WriteNumber("gpuLayers", plan.GpuLayers);
        writer.WriteNumber("threads", plan.Threads);
    }

    /// <summary><c>quitar-fondo</c>: la calidad alta va por la CPU si BiRefNet no cabe en la GPU.</summary>
    public static JsonDocument RemoveBackground(AiPlan plan, bool high, string session, bool reset)
    {
        ArgumentNullException.ThrowIfNull(plan);
        VisionPlan v = plan.Vision;
        return Write(w =>
        {
            w.WriteString("calidad", high ? "alta" : "normal");
            w.WriteString("sesion", session);
            w.WriteBoolean("reiniciar", reset);
            w.WriteString("dispositivo", RemoveBackgroundDevice(plan, high) == AiDevice.Gpu ? "gpu" : "cpu");
            w.WriteNumber("lado", v.MaskSide);
            w.WriteNumber("hilos", v.Threads);
            w.WriteNumber("paralelo", v.Parallel);
        });
    }

    public static AiDevice RemoveBackgroundDevice(AiPlan plan, bool high)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return high ? (plan.Vision.HighOnGpu ? AiDevice.Gpu : AiDevice.Cpu) : plan.Vision.Device;
    }

    /// <summary><c>detectar</c>: rostros o personas en el dispositivo del plan.</summary>
    public static JsonDocument Detect(AiPlan plan, bool faces)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Write(w =>
        {
            w.WriteString("objetivo", faces ? "rostros" : "personas");
            w.WriteString("dispositivo", plan.Vision.Device == AiDevice.Gpu ? "gpu" : "cpu");
            w.WriteNumber("hilos", plan.Vision.Threads);
        });
    }

    /// <summary>Lo que pide el trabajo: la VRAM del plan si va por la GPU; si no, un hueco de CPU.</summary>
    public static ModuleDemand Demand(AiDevice device, int vramMb) => device == AiDevice.Gpu ? ModuleDemand.Gpu(vramMb) : ModuleDemand.Cpu;

    private static void WriteIf(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteString(name, value);
        }
    }

    private static JsonDocument Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray());
    }
}
