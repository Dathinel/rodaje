using System.Runtime.CompilerServices;

namespace Rodaje.Render.Gpu;

/// <summary>Formatos de textura que usan Rodaje y Unicomp. Sin sRGB: la gamma la hacen los shaders (D-016).</summary>
public enum GpuFormat
{
    Unknown,
    R8Unorm,
    Rg8Unorm,
    R16Unorm,
    Rg16Unorm,
    R16Uint,
    R32Uint,
    R32Float,
    Rg32Float,
    Rgba8Unorm,
    Bgra8Unorm,
    Rgb10A2Unorm,
    Rgba16Unorm,
    Rgba16Float,
    Rgba32Float,
    D32Float,
}

/// <summary>Para qué sirve una textura. La copia de y hacia ella siempre está permitida.</summary>
[Flags]
public enum GpuTextureUsage
{
    None = 0,
    Sampled = 1,
    RenderTarget = 2,
    Storage = 4,
    DepthStencil = 8,

    /// <summary>Admite <see cref="IGpuFrame.GenerateMips"/>; exige <see cref="Sampled"/> y <see cref="RenderTarget"/>.</summary>
    GenerateMips = 16,
}

/// <summary>Textura 2D (o 3D con <see cref="Depth"/> &gt; 1).</summary>
/// <param name="MipLevels">0 = la cadena completa.</param>
/// <param name="Samples">MSAA (1, 2, 4, 8): solo 2D, sin mips y sin <see cref="GpuTextureUsage.Storage"/>.</param>
public readonly record struct GpuTextureDesc(int Width, int Height, GpuFormat Format, GpuTextureUsage Usage,
    int MipLevels = 1, int Depth = 1, int Samples = 1);

public enum GpuBufferKind
{
    /// <summary><c>cbuffer</c>: se reescribe entero en cada <see cref="IGpuFrame.Write{T}(GpuBuffer, ReadOnlySpan{T})"/>.</summary>
    Constant,
    Vertex,
    Index,

    /// <summary><c>StructuredBuffer</c>/<c>RWStructuredBuffer</c> de <see cref="GpuBufferDesc.Stride"/> bytes por elemento.</summary>
    Structured,

    /// <summary><c>ByteAddressBuffer</c>/<c>RWByteAddressBuffer</c>.</summary>
    Raw,
}

/// <param name="Size">Bytes; en <see cref="GpuBufferKind.Constant"/>, múltiplo de 16.</param>
/// <param name="Storage">Se escribe desde un shader de cómputo (<c>u#</c>).</param>
public readonly record struct GpuBufferDesc(int Size, GpuBufferKind Kind, bool Storage = false, int Stride = 0);

public enum GpuFilter
{
    Linear,
    Point,
}

public enum GpuAddressMode
{
    Clamp,
    Wrap,
    Mirror,

    /// <summary>Fuera de la textura, negro transparente.</summary>
    Border,
}

public readonly record struct GpuSamplerDesc(GpuFilter Filter = GpuFilter.Linear, GpuAddressMode Address = GpuAddressMode.Clamp);

/// <summary>Factores de mezcla, como los de D3D11 y Vulkan.</summary>
public enum GpuBlendFactor
{
    Zero,
    One,
    SourceColor,
    InverseSourceColor,
    SourceAlpha,
    InverseSourceAlpha,
    DestinationColor,
    InverseDestinationColor,
    DestinationAlpha,
    InverseDestinationAlpha,
}

/// <summary>Mezcla aditiva: <c>destino = fuente · Source + destino · Destination</c>, por separado en color y en alfa.</summary>
public readonly record struct GpuBlend(GpuBlendFactor Source, GpuBlendFactor Destination, GpuBlendFactor SourceAlpha, GpuBlendFactor DestinationAlpha)
{
    /// <summary>Sin mezcla: se escribe la fuente.</summary>
    public static GpuBlend Opaque => default;

    /// <summary>«Over» con alfa premultiplicado, también en alfa.</summary>
    public static GpuBlend PremultipliedOver => new(GpuBlendFactor.One, GpuBlendFactor.InverseSourceAlpha, GpuBlendFactor.One, GpuBlendFactor.InverseSourceAlpha);

    /// <summary>Suma en los cuatro canales.</summary>
    public static GpuBlend Additive => new(GpuBlendFactor.One, GpuBlendFactor.One, GpuBlendFactor.One, GpuBlendFactor.One);

    public bool IsOpaque => this == default;
}

public enum GpuTopology
{
    TriangleStrip,
    TriangleList,
    LineList,
    LineStrip,
}

public enum GpuCullMode
{
    None,
    Back,
    Front,
}

public enum GpuCompare
{
    Never,
    Less,
    Equal,
    LessEqual,
    Greater,
    NotEqual,
    GreaterEqual,
    Always,
}

/// <summary>Profundidad (nivel <i>Raster</i>): prueba, escritura y comparación por separado.</summary>
public readonly record struct GpuDepthState(bool Test, bool Write, GpuCompare Compare = GpuCompare.Less)
{
    public static GpuDepthState Disabled => default;
}

/// <summary>Estado del rasterizador. Las caras delanteras van en el sentido de las agujas del reloj, como en D3D11.</summary>
/// <param name="DepthBias">Desplazamiento constante en unidades del formato de profundidad (D3D11 <c>DepthBias</c>).</param>
/// <param name="SlopeScaledDepthBias">Desplazamiento por pendiente.</param>
public readonly record struct GpuRasterState(GpuTopology Topology = GpuTopology.TriangleStrip, GpuCullMode Cull = GpuCullMode.None,
    int DepthBias = 0, float SlopeScaledDepthBias = 0, float DepthBiasClamp = 0);

public enum GpuVertexFormat
{
    Float1,
    Float2,
    Float3,
    Float4,
    Unorm8x4,
    Uint1,
}

/// <summary>Un atributo de vértice; <see cref="Semantic"/> es la semántica HLSL (<c>POSITION</c>, <c>COLOR</c>…).</summary>
public readonly record struct GpuVertexElement(string Semantic, int SemanticIndex, GpuVertexFormat Format, int Offset);

/// <summary>Disposición de un único búfer de vértices (<see cref="Stride"/> bytes por vértice).</summary>
public sealed record GpuVertexLayout(int Stride, params GpuVertexElement[] Elements);

/// <summary>
/// Una canalización: sombreadores y estado fijo. Para dibujar, <see cref="Vertex"/> y <see cref="Pixel"/>; para cómputo, solo
/// <see cref="Compute"/>. <see cref="VertexLayout"/>, <see cref="Depth"/> y <see cref="Samples"/> &gt; 1 son del nivel <i>Raster</i>.
/// </summary>
public sealed record GpuPipelineDesc
{
    public GpuShader? Vertex { get; init; }

    public GpuShader? Pixel { get; init; }

    public GpuShader? Compute { get; init; }

    public GpuBlend Blend { get; init; }

    public GpuRasterState Raster { get; init; }

    public GpuDepthState Depth { get; init; }

    /// <summary><c>null</c>: sin búfer de vértices (los vértices salen de <c>SV_VertexID</c>).</summary>
    public GpuVertexLayout? VertexLayout { get; init; }

    public int Samples { get; init; } = 1;
}

/// <summary>Una caja de píxeles: <c>[X, X + Width)</c> × <c>[Y, Y + Height)</c>.</summary>
public readonly record struct GpuBox(int X, int Y, int Width, int Height);

public readonly record struct GpuColor(float R, float G, float B, float A)
{
    public static GpuColor TransparentBlack => default;

    public static GpuColor OpaqueBlack => new(0, 0, 0, 1);
}

public readonly record struct GpuViewport(float X, float Y, float Width, float Height, float MinDepth = 0, float MaxDepth = 1)
{
    public static GpuViewport Of(int width, int height) => new(0, 0, width, height);
}

public enum GpuIndexFormat
{
    Bits16,
    Bits32,
}

/// <summary>
/// Qué se dibuja. Sin <see cref="VertexBuffer"/> los vértices salen de <c>SV_VertexID</c> (los cuadriláteros de Rodaje); con
/// <see cref="IndexBuffer"/> se dibuja indexado (<see cref="Count"/> índices) y, si no, <see cref="Count"/> vértices.
/// </summary>
public readonly record struct GpuDrawCall(int Count, GpuBuffer? VertexBuffer = null, GpuBuffer? IndexBuffer = null,
    GpuIndexFormat IndexFormat = GpuIndexFormat.Bits16, int Instances = 1, int FirstIndex = 0, int BaseVertex = 0);

/// <summary>Hasta cuatro destinos de color y uno de profundidad opcional.</summary>
public struct GpuTargets
{
    public const int MaxColors = 4;
    private ColorSlots _colors;

    public GpuTargets(GpuTexture color, GpuTexture? depth = null)
    {
        _colors[0] = color;
        Count = 1;
        Depth = depth;
    }

    public int Count { get; private set; }

    public GpuTexture? Depth { get; set; }

    public readonly GpuTexture? this[int index] => _colors[index];

    public void Add(GpuTexture color)
    {
        if (Count == MaxColors)
        {
            throw new InvalidOperationException($"Como mucho {MaxColors} destinos de color.");
        }

        _colors[Count++] = color;
    }

    [InlineArray(MaxColors)]
    private struct ColorSlots
    {
        private GpuTexture? _element;
    }
}

/// <summary>
/// Recursos de una pasada por número de registro HLSL: <c>t#</c> (texturas y búferes de lectura), <c>s#</c>, <c>b#</c> y
/// <c>u#</c>. Lo que no se pone queda sin enlazar.
/// </summary>
public struct GpuBindings
{
    public const int MaxResources = 16;
    public const int MaxSamplers = 4;
    public const int MaxConstants = 4;
    public const int MaxStorage = 8;
    private Resources _resources;
    private Samplers _samplers;
    private Constants _constants;
    private Storage _storage;

    /// <summary>Uno más que el <c>t#</c> más alto enlazado.</summary>
    public int ResourceCount { get; private set; }

    public int SamplerCount { get; private set; }

    public int ConstantCount { get; private set; }

    public int StorageCount { get; private set; }

    public readonly GpuResource? Resource(int register) => _resources[register];

    public readonly GpuSampler? Sampler(int register) => _samplers[register];

    public readonly GpuBuffer? ConstantBuffer(int register) => _constants[register];

    public readonly GpuResource? StorageResource(int register) => _storage[register];

    /// <summary><c>t#</c>: una textura muestreable o un búfer estructurado o sin tipo.</summary>
    public void SetResource(int register, GpuResource? resource)
    {
        _resources[register] = resource;
        ResourceCount = Math.Max(ResourceCount, register + 1);
    }

    public void SetSampler(int register, GpuSampler? sampler)
    {
        _samplers[register] = sampler;
        SamplerCount = Math.Max(SamplerCount, register + 1);
    }

    public void SetConstants(int register, GpuBuffer? buffer)
    {
        _constants[register] = buffer;
        ConstantCount = Math.Max(ConstantCount, register + 1);
    }

    /// <summary><c>u#</c>: una textura o un búfer con escritura desde el shader.</summary>
    public void SetStorage(int register, GpuResource? resource)
    {
        _storage[register] = resource;
        StorageCount = Math.Max(StorageCount, register + 1);
    }

    [InlineArray(MaxResources)]
    private struct Resources
    {
        private GpuResource? _element;
    }

    [InlineArray(MaxSamplers)]
    private struct Samplers
    {
        private GpuSampler? _element;
    }

    [InlineArray(MaxConstants)]
    private struct Constants
    {
        private GpuBuffer? _element;
    }

    [InlineArray(MaxStorage)]
    private struct Storage
    {
        private GpuResource? _element;
    }
}

/// <summary>Lo que el motor sabe hacer.</summary>
public sealed record GpuCaps
{
    /// <summary>Nombre del adaptador.</summary>
    public required string Adapter { get; init; }

    /// <summary>En la CPU (WARP, lavapipe).</summary>
    public bool IsSoftware { get; init; }

    public int MaxTextureSize { get; init; }

    /// <summary>Nivel <i>Raster</i>: búferes de vértices e índices, profundidad, líneas, MSAA.</summary>
    public bool Raster { get; init; }

    /// <summary><see cref="IGpuFrame.Copy(GpuTexture, in GpuBox, GpuReadback)"/> admite <see cref="GpuFormat.D32Float"/>.</summary>
    public bool DepthReadback { get; init; }

    /// <summary>Muestras de MSAA admitidas por formato de destino (1 siempre).</summary>
    public Func<GpuFormat, int> MaxSamples { get; init; } = static _ => 1;

    /// <summary>El formato admite mezcla como destino.</summary>
    public Func<GpuFormat, bool> CanBlend { get; init; } = static _ => true;
}

/// <summary>Tamaños de los formatos.</summary>
public static class GpuFormats
{
    public static int BytesPerPixel(GpuFormat format) => format switch
    {
        GpuFormat.R8Unorm => 1,
        GpuFormat.Rg8Unorm or GpuFormat.R16Unorm or GpuFormat.R16Uint => 2,
        GpuFormat.Rg16Unorm or GpuFormat.R32Uint or GpuFormat.R32Float or GpuFormat.Rgba8Unorm or GpuFormat.Bgra8Unorm
            or GpuFormat.Rgb10A2Unorm or GpuFormat.D32Float => 4,
        GpuFormat.Rg32Float or GpuFormat.Rgba16Unorm or GpuFormat.Rgba16Float => 8,
        GpuFormat.Rgba32Float => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Formato sin tamaño por píxel."),
    };
}
