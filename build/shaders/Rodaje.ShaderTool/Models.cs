using System.Text.Json.Serialization;

namespace Rodaje.ShaderTool;

/// <summary>Un recurso enlazado por un shader SPIR-V.</summary>
public sealed record ResourceEntry
{
    /// <summary>Registro HLSL equivalente (ej. b0, t3, s0, u0).</summary>
    public required string Register { get; init; }

    public required string Name { get; init; }

    public required int Set { get; init; }

    public required int Binding { get; init; }

    /// <summary>uniform-buffer, storage-buffer, sampled-image, storage-image o sampler.</summary>
    public required string Kind { get; init; }

    public string? Dimension { get; init; }

    public int? Size { get; init; }
}

/// <summary>Una variante compilada a SPIR-V.</summary>
public sealed record VariantEntry
{
    public required string Id { get; init; }

    public required string File { get; init; }

    public required string EntryPoint { get; init; }

    public required string Profile { get; init; }

    public required string[] Defines { get; init; }

    public required string Sha256 { get; init; }

    public required ResourceEntry[] Resources { get; init; }
}

/// <summary>Manifiesto generado de todos los shaders SPIR-V compilados con DXC.</summary>
public sealed record ManifestModel
{
    public required string Compiler { get; init; }

    public required string[] Arguments { get; init; }

    public required VariantEntry[] Variants { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ManifestModel))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext
{
}
