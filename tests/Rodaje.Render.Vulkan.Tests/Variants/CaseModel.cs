namespace Rodaje.Render.Tests.Variants;

internal sealed record TextureSpec
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

internal sealed record CaseSpec
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


