using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fase0.Shaders;

/// <summary>Un recurso de una variante: registro HLSL, binding de Vulkan y lo que hace falta para crear su descriptor.</summary>
public sealed record ResourceBinding
{
    /// <summary>Registro HLSL, p. ej. <c>b1</c>, <c>t5</c>, <c>s0</c>.</summary>
    public required string Register { get; init; }

    public required string Name { get; init; }

    public required int Set { get; init; }

    public required int Binding { get; init; }

    /// <summary><c>uniform-buffer</c>, <c>sampled-image</c> o <c>sampler</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Para imágenes: <c>2d</c>, <c>2d-array</c> o <c>3d</c>.</summary>
    public string? Dimension { get; init; }

    /// <summary>Para cbuffers: tamaño en bytes según los desplazamientos del SPIR-V.</summary>
    public int? Size { get; init; }
}

public sealed record ShaderEntry
{
    /// <summary>Índice en <c>EffectShaders.All</c>; -1 para el vertex shader.</summary>
    public required int Index { get; init; }

    public required string File { get; init; }

    public required string Entry { get; init; }

    public required string Profile { get; init; }

    public required string[] Defines { get; init; }

    /// <summary><c>ok</c>, <c>ok-hv2018</c> (compila solo con <c>-HV 2018</c>), <c>ok-parche</c> o <c>error</c>.</summary>
    public required string Status { get; init; }

    public string? Error { get; init; }

    public string? Warnings { get; init; }

    /// <summary>Con el parche mínimo de README (select/and) y el HLSL 2021 por defecto de DXC: <c>ok</c> o el error.</summary>
    public string? Hlsl2021Patch { get; init; }

    /// <summary>El mismo parche con D3DCompile ps_5_0 (O3, avisos como errores), para ver que fxc lo sigue aceptando.</summary>
    public string? FxcWithPatch { get; init; }

    public ResourceBinding[] Resources { get; init; } = [];
}

public sealed record Manifest
{
    public required string Compiler { get; init; }

    public required string[] Arguments { get; init; }

    public required ShaderEntry Vertex { get; init; }

    public required ShaderEntry[] Variants { get; init; }
}

/// <summary>Una textura de entrada, rellenada por fórmula (<see cref="Patterns"/>) o con datos literales.</summary>
public sealed record TextureSpec
{
    public required string Name { get; init; }

    /// <summary><c>rgba16f</c>, <c>rg16f</c>, <c>r16f</c>, <c>r32f</c> o <c>rgba32f</c>.</summary>
    public required string Format { get; init; }

    /// <summary><c>2d</c>, <c>2d-array</c> (una capa) o <c>3d</c>.</summary>
    public required string Dimension { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public int Depth { get; init; } = 1;

    /// <summary><c>suave</c>, <c>premultiplicada</c>, <c>lut</c>, <c>curvas</c>, <c>distancia</c> o <c>literal</c>.</summary>
    public required string Pattern { get; init; }

    public int Seed { get; init; }

    public float[]? Data { get; init; }

    /// <summary>FNV-1a de 64 bits de los bytes subidos, para comprobar que el otro lado los reconstruye idénticos.</summary>
    public string? Hash { get; init; }
}

public sealed record CaseSpec
{
    public required string Name { get; init; }

    /// <summary>Índice en <c>EffectShaders.All</c>.</summary>
    public required int Variant { get; init; }

    public required string Description { get; init; }

    /// <summary><c>cbuffer Quad</c> (b0): 17 float4.</summary>
    public required float[] Quad { get; init; }

    /// <summary><c>cbuffer Effects</c> (b1): 26 float4.</summary>
    public required float[] Effects { get; init; }

    /// <summary>Registro t → nombre de textura.</summary>
    public required Dictionary<string, string> Textures { get; init; }
}

public sealed record CaseFile
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Formato del objetivo y de los <c>.bin</c>: RGBA16F leído y guardado como float32 LE, fila a fila desde arriba.</summary>
    public required string Target { get; init; }

    public required TextureSpec[] TextureSet { get; init; }

    public required CaseSpec[] Cases { get; init; }
}

public sealed record CaseResult
{
    public required string Name { get; init; }

    public required int Variant { get; init; }

    public required string Status { get; init; }

    public double MaxError { get; init; }

    public double MeanError { get; init; }

    /// <summary>Valores (píxel y canal) que pasan del umbral, de 64 × 64 × 4.</summary>
    public int ValuesOver { get; init; }

    public int[]? WorstPixel { get; init; }

    public float[]? WorstReference { get; init; }

    public float[]? WorstVulkan { get; init; }

    public string? Detail { get; init; }
}

public sealed record RunResult
{
    public required string Device { get; init; }

    public required string Driver { get; init; }

    public required double Threshold { get; init; }

    public required int Compared { get; init; }

    public required int Passed { get; init; }

    /// <summary>Variantes con al menos un caso dentro del umbral.</summary>
    public required int VariantsCovered { get; init; }

    /// <summary>Variantes con todos sus casos dentro del umbral.</summary>
    public required int VariantsAllPassed { get; init; }

    public required CaseResult[] Cases { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(CaseFile))]
[JsonSerializable(typeof(RunResult))]
public sealed partial class Json : JsonSerializerContext
{
    public static T Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), info) ?? throw new InvalidDataException($"{path} está vacío.");

    public static void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, info));
}
