using Rodaje.Media;
using Rodaje.Media.Video;
using Rodaje.TestMedia;

namespace Rodaje.Render.Tests.Support;

/// <summary>Corre con el motor de <see cref="TestGpu.Engine"/>; se omite con <see cref="TestGpu.SkipReason"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EngineFactAttribute : FactAttribute
{
    private string? _vulkanSkip;

    public EngineFactAttribute() => Skip = Reason();

    /// <summary>Motivo por el que esta prueba no corre con Vulkan (un fallo real del motor o del compositor, no una mera diferencia).</summary>
    public string? VulkanSkip
    {
        get => _vulkanSkip;
        set
        {
            _vulkanSkip = value;
            Skip = Reason();
        }
    }

    private string? Reason() => TestGpu.IsVulkan && _vulkanSkip is not null
        ? "Omitida en Vulkan: " + _vulkanSkip
        : TestGpu.SkipReason;
}

/// <summary>Teoría que corre con el motor de <see cref="TestGpu.Engine"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EngineTheoryAttribute : TheoryAttribute
{
    private string? _vulkanSkip;

    public EngineTheoryAttribute() => Skip = Reason();

    /// <inheritdoc cref="EngineFactAttribute.VulkanSkip"/>
    public string? VulkanSkip
    {
        get => _vulkanSkip;
        set
        {
            _vulkanSkip = value;
            Skip = Reason();
        }
    }

    private string? Reason() => TestGpu.IsVulkan && _vulkanSkip is not null
        ? "Omitida en Vulkan: " + _vulkanSkip
        : TestGpu.SkipReason;
}

/// <summary>Solo corre con el motor D3D11 en Windows. <paramref name="why"/> es el motivo corto (qué usa que no es portable).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class D3D11FactAttribute : FactAttribute
{
    public D3D11FactAttribute(string why) => Skip = TestGpu.D3D11Only(why);
}

/// <summary>Teoría que solo corre con el motor D3D11 en Windows.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class D3D11TheoryAttribute : TheoryAttribute
{
    public D3D11TheoryAttribute(string why) => Skip = TestGpu.D3D11Only(why);
}

/// <summary>Solo D3D11 y, como <see cref="GpuFactAttribute"/>, con muestreo NV12/P010 por hardware.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class D3D11GpuFactAttribute : FactAttribute
{
    public D3D11GpuFactAttribute(string why) => Skip = TestGpu.D3D11Only(why) ?? Rodaje.TestMedia.Skip.GpuTextures;
}

/// <summary>Teoría solo D3D11 con muestreo NV12/P010 por hardware.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class D3D11GpuTheoryAttribute : TheoryAttribute
{
    public D3D11GpuTheoryAttribute(string why) => Skip = TestGpu.D3D11Only(why) ?? Rodaje.TestMedia.Skip.GpuTextures;
}

/// <summary>Solo D3D11 y, como <see cref="HardwareFactAttribute"/>, con FFmpeg y D3D11VA.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class D3D11HardwareFactAttribute : FactAttribute
{
    private readonly string _why;
    private readonly HardwareFactAttribute _inner;
    public D3D11HardwareFactAttribute(string why, params VideoCodec[] video)
    {
        _why = why;
        _inner = new HardwareFactAttribute(video);
        Skip = Reason();
    }

    public AudioCodec Audio
    {
        get => _inner.Audio;
        set
        {
            _inner.Audio = value;
            Skip = Reason();
        }
    }

    public bool LongRunning
    {
        get => _inner.LongRunning;
        set
        {
            _inner.LongRunning = value;
            Skip = Reason();
        }
    }

    /// <inheritdoc cref="HardwareFactAttribute.Encoder"/>
    public string? Encoder
    {
        get => _inner.Encoder;
        set
        {
            _inner.Encoder = value;
            Skip = Reason();
        }
    }

    private string? Reason() => TestGpu.D3D11Only(_why) ?? _inner.Skip;
}

/// <summary>Teoría solo D3D11 con FFmpeg y D3D11VA.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class D3D11HardwareTheoryAttribute : TheoryAttribute
{
    private readonly string _why;
    private readonly HardwareTheoryAttribute _inner;

    public D3D11HardwareTheoryAttribute(string why, params VideoCodec[] video)
    {
        _why = why;
        _inner = new HardwareTheoryAttribute(video);
        Skip = Reason();
    }

    public AudioCodec Audio
    {
        get => _inner.Audio;
        set
        {
            _inner.Audio = value;
            Skip = Reason();
        }
    }

    private string? Reason() => TestGpu.D3D11Only(_why) ?? _inner.Skip;
}

/// <summary>Solo D3D11 y, como <see cref="FfmpegLibraryFactAttribute"/>, con las DLL de FFmpeg.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class D3D11FfmpegLibraryFactAttribute : FactAttribute
{
    private readonly string _why;
    private readonly FfmpegLibraryFactAttribute _inner;

    public D3D11FfmpegLibraryFactAttribute(string why, params VideoCodec[] video)
    {
        _why = why;
        _inner = new FfmpegLibraryFactAttribute(video);
        Skip = Reason();
    }

    public AudioCodec Audio
    {
        get => _inner.Audio;
        set
        {
            _inner.Audio = value;
            Skip = Reason();
        }
    }

    public bool LongRunning
    {
        get => _inner.LongRunning;
        set
        {
            _inner.LongRunning = value;
            Skip = Reason();
        }
    }

    public bool Complete
    {
        get => _inner.Complete;
        set
        {
            _inner.Complete = value;
            Skip = Reason();
        }
    }

    /// <inheritdoc cref="FfmpegLibraryFactAttribute.Encoder"/>
    public string? Encoder
    {
        get => _inner.Encoder;
        set
        {
            _inner.Encoder = value;
            Skip = Reason();
        }
    }

    private string? Reason() => TestGpu.D3D11Only(_why) ?? _inner.Skip;
}

/// <summary>Teoría solo D3D11 con las DLL de FFmpeg.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class D3D11FfmpegLibraryTheoryAttribute : TheoryAttribute
{
    private readonly string _why;
    private readonly FfmpegLibraryTheoryAttribute _inner;

    public D3D11FfmpegLibraryTheoryAttribute(string why, params VideoCodec[] video)
    {
        _why = why;
        _inner = new FfmpegLibraryTheoryAttribute(video);
        Skip = Reason();
    }

    public AudioCodec Audio
    {
        get => _inner.Audio;
        set
        {
            _inner.Audio = value;
            Skip = Reason();
        }
    }

    public bool LongRunning
    {
        get => _inner.LongRunning;
        set
        {
            _inner.LongRunning = value;
            Skip = Reason();
        }
    }

    private string? Reason() => TestGpu.D3D11Only(_why) ?? _inner.Skip;
}
