using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Rodaje.VulkanVideo;

public class ExtensionesVideoInfo
{
    [JsonPropertyName("presentes")]
    public List<string> Presentes { get; set; } = new();

    [JsonPropertyName("ausentes")]
    public List<string> Ausentes { get; set; } = new();
}

public class CuadrosDecodificadosInfo
{
    [JsonPropertyName("nv12")]
    public int Nv12 { get; set; }

    [JsonPropertyName("p010")]
    public int P010 { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

public class MsPorCuadroInfo
{
    [JsonPropertyName("p50")]
    public double P50 { get; set; }

    [JsonPropertyName("p95")]
    public double P95 { get; set; }
}

public class MemoriaMbInfo
{
    [JsonPropertyName("inicio")]
    public double Inicio { get; set; }

    [JsonPropertyName("mitad")]
    public double Mitad { get; set; }

    [JsonPropertyName("final")]
    public double Final { get; set; }
}

public class RecursosVivosInfo
{
    [JsonPropertyName("vk_images")]
    public int VkImages { get; set; }

    [JsonPropertyName("av_frames")]
    public int AvFrames { get; set; }
}

public class VerificacionInfo
{
    [JsonPropertyName("cuadros_verificados")]
    public int CuadrosVerificados { get; set; }

    [JsonPropertyName("error_medio_255")]
    public double ErrorMedio255 { get; set; }

    [JsonPropertyName("umbral_error_255")]
    public double UmbralError255 { get; set; }

    [JsonPropertyName("psnr_medio_db")]
    public double PsnrMedioDb { get; set; }

    [JsonPropertyName("supero_umbral")]
    public bool SuperoUmbral { get; set; }
}

public class ResultadoReporte
{
    [JsonPropertyName("gpu")]
    public string Gpu { get; set; } = "";

    [JsonPropertyName("driver")]
    public string Driver { get; set; } = "";

    [JsonPropertyName("camino")]
    public string Camino { get; set; } = "";

    [JsonPropertyName("extensiones_video")]
    public ExtensionesVideoInfo ExtensionesVideo { get; set; } = new();

    [JsonPropertyName("cuadros_decodificados")]
    public CuadrosDecodificadosInfo CuadrosDecodificados { get; set; } = new();

    [JsonPropertyName("fps")]
    public double Fps { get; set; }

    [JsonPropertyName("ms_por_cuadro")]
    public MsPorCuadroInfo MsPorCuadro { get; set; } = new();

    [JsonPropertyName("transferencias_cpu_camino_normal")]
    public int TransferenciasCpuCaminoNormal { get; set; }

    [JsonPropertyName("memoria_mb")]
    public MemoriaMbInfo MemoriaMb { get; set; } = new();

    [JsonPropertyName("recursos_vivos")]
    public RecursosVivosInfo RecursosVivos { get; set; } = new();

    [JsonPropertyName("errores_validacion")]
    public int ErroresValidacion { get; set; }

    [JsonPropertyName("verificacion")]
    public VerificacionInfo Verificacion { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ResultadoReporte))]
internal partial class ResultadoJsonContext : JsonSerializerContext
{
}
