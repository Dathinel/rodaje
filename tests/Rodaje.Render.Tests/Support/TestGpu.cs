using System.Runtime.Versioning;
using Rodaje.Media.Gpu;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Shaders;
using Rodaje.Render.Tests.Gpu;

namespace Rodaje.Render.Tests.Support;

internal enum TestEngine
{
    D3D11,
    Vulkan,
}

/// <summary>
/// La GPU de una prueba del compositor, en el motor que pide <c>RODAJE_MOTOR</c> (<c>d3d11</c> o <c>vulkan</c>;
/// sin la variable, D3D11 en Windows y Vulkan en los demás). Con D3D11 es exactamente lo de antes:
/// un <see cref="GpuDevice"/> propio y el compositor con su constructor de siempre. Con Vulkan, el
/// <see cref="VulkanGpu"/> compartido de <see cref="VulkanTestGpu"/> (no se libera aquí).
/// </summary>
internal sealed class TestGpu : IDisposable
{
    public const string EngineVariable = "RODAJE_MOTOR";

    private static readonly TestEngine? s_engine = Parse();

    private readonly GpuDevice? _device;
    private IGpu? _gpu;

    private TestGpu(GpuDevice device) => _device = device;

    private TestGpu()
    {
    }

    /// <summary>El motor elegido; <see cref="InvalidOperationException"/> si <c>RODAJE_MOTOR</c> no es válido.</summary>
    public static TestEngine Engine => s_engine ?? throw new InvalidOperationException(
        $"{EngineVariable}='{Environment.GetEnvironmentVariable(EngineVariable)}' no es 'd3d11' ni 'vulkan'.");

    public static bool IsVulkan => s_engine == TestEngine.Vulkan;

    /// <summary>La superficie <see cref="IGpu"/> del motor elegido.</summary>
    public IGpu Gpu => _gpu ??= IsVulkan || !OperatingSystem.IsWindowsVersionAtLeast(7) ? VulkanTestGpu.Gpu : NewD3D11Engine();

    /// <summary>El dispositivo Direct3D 11; solo existe con el motor D3D11.</summary>
    [SupportedOSPlatform("windows7.0")]
    public GpuDevice Device => _device ?? throw new InvalidOperationException("GpuDevice solo existe con el motor D3D11.");

    /// <summary>Nombre del adaptador, para los mensajes de las pruebas.</summary>
    public string AdapterName => IsVulkan || !OperatingSystem.IsWindowsVersionAtLeast(7) ? Gpu.Caps.Adapter : Device.AdapterName;

    public static TestGpu Create(bool warp = false)
    {
        if (Engine == TestEngine.Vulkan)
        {
            return new TestGpu();
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            throw new InvalidOperationException("El motor D3D11 solo existe en Windows.");
        }

        return CreateD3D11(warp);
    }

    /// <summary>El mismo compositor que creaba la prueba: con D3D11, el constructor de <see cref="GpuDevice"/>.</summary>
    public Compositor NewCompositor(int width, int height, CompositeFormat format = CompositeFormat.Rgba16Float,
        ShaderCache? shaders = null, long? poolCapBytes = null) =>
        IsVulkan || !OperatingSystem.IsWindowsVersionAtLeast(7)
            ? new Compositor(Gpu, width, height, format, shaders, poolCapBytes)
            : new Compositor(Device, width, height, format, shaders, poolCapBytes);

    public Rodaje.Render.Text.ITitleRasterizer NewTitles(Rodaje.Render.Text.TitleRasterizerOptions? options = null) =>
        Rodaje.Render.Text.TitleRasterizers.Create(Gpu, options);

    /// <summary>Por qué se omite una prueba del motor elegido, o <c>null</c> si puede correr.</summary>
    public static string? SkipReason => IsVulkan ? VulkanTestGpu.SkipReason : Rodaje.TestMedia.Skip.GpuTextures;

    /// <summary>Motivo de omisión de una prueba solo para D3D11, o <c>null</c> con ese motor en Windows.</summary>
    public static string? D3D11Only(string why)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            return $"Omitida: solo D3D11 ({why}); Direct3D 11 solo existe en Windows.";
        }

        return IsVulkan ? $"Omitida con el motor Vulkan: solo D3D11 ({why})." : null;
    }

    /// <summary>
    /// La tolerancia de una comprobación en el motor elegido: con D3D11, exactamente la de antes; con Vulkan,
    /// la que mide la diferencia real de su filtrado. <paramref name="why"/> dice por qué se ensancha.
    /// </summary>
    public static double Tolerance(double d3d11, double vulkan, string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        return IsVulkan ? vulkan : d3d11;
    }

    /// <summary>
    /// Dos lecturas que en D3D11 salen idénticas byte a byte (dos caminos que deben dar lo mismo). Con Vulkan se admite un código de
    /// diferencia por canal: lavapipe redondea de otra forma el filtro y las mezclas en coma flotante (criterio de la fase 4: 1/255).
    /// </summary>
    public static void AssertSameImage(byte[] expected, byte[] actual)
    {
        if (!IsVulkan)
        {
            Assert.Equal(expected, actual);
            return;
        }

        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) <= 1, $"Byte {i}: {actual[i]} frente a {expected[i]}.");
        }
    }

    public void Dispose()
    {
        if (_device is null || !OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            return;
        }

        // El motor de D3D11Gpu.For es del dispositivo y no se cierra aparte, como antes de TestGpu.
        _device.Dispose();
    }

    private static TestEngine? Parse() => Environment.GetEnvironmentVariable(EngineVariable)?.ToLowerInvariant() switch
    {
        null => OperatingSystem.IsWindowsVersionAtLeast(7) ? TestEngine.D3D11 : TestEngine.Vulkan,
        "d3d11" => TestEngine.D3D11,
        "vulkan" => TestEngine.Vulkan,
        "" => OperatingSystem.IsWindowsVersionAtLeast(7) ? TestEngine.D3D11 : TestEngine.Vulkan,
        _ => null,
    };

    [SupportedOSPlatform("windows7.0")]
    private static TestGpu CreateD3D11(bool warp) => new(warp ? GpuDevice.CreateWarp() : GpuDevice.Create());

    [SupportedOSPlatform("windows7.0")]
    private D3D11Gpu NewD3D11Engine() => D3D11Gpu.For(Device);
}
