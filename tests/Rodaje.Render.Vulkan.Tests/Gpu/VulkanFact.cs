using Rodaje.Render.Gpu.Vulkan;

namespace Rodaje.Render.Tests.Gpu;

/// <summary>El motor Vulkan de las pruebas: el de la CPU (lavapipe) si lo hay, para que dé lo mismo en cualquier equipo; si no, la GPU.</summary>
internal static class VulkanTestGpu
{
    private static readonly Lazy<(VulkanGpu? Gpu, string? Reason)> s_gpu = new(() =>
        VulkanGpu.TryCreate(preferSoftware: true, out VulkanGpu? gpu, out string? reason) ? (gpu, null) : (null, "Omitida: " + reason));

    public static VulkanGpu Gpu => s_gpu.Value.Gpu ?? throw new InvalidOperationException(s_gpu.Value.Reason);

    /// <summary>Por qué se omiten; con <c>RODAJE_VK_EXIGIR=1</c> (la CI de Linux, con lavapipe) no se omiten y fallan con el motivo.</summary>
    public static string? SkipReason => Environment.GetEnvironmentVariable("RODAJE_VK_EXIGIR") == "1" ? null : s_gpu.Value.Reason;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class VulkanFactAttribute : FactAttribute
{
    public VulkanFactAttribute() => Skip = VulkanTestGpu.SkipReason;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class VulkanTheoryAttribute : TheoryAttribute
{
    public VulkanTheoryAttribute() => Skip = VulkanTestGpu.SkipReason;
}
