using Rodaje.Playback.Composition;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;

namespace Rodaje.Render.Text;

public interface ITitleRasterizer : IGeneratedContentCache, IDisposable
{
    IGpu Engine { get; }
    event EventHandler? Changed;
    long CacheCapBytes { get; }
    long MaxTitleBytes { get; }
    long CachedBytes { get; }
    int CachedCount { get; }
    long Rasterized { get; }
    double LastRasterMilliseconds { get; }
    int Failures { get; }
    string? LastError { get; }
    IReadOnlyList<string> RegisterFonts(IEnumerable<string> fontFiles);
    IReadOnlyList<MissingFont> FindMissingFonts(IEnumerable<GeneratedContent> contents);
    TitleTexture? TryGet(GeneratedContent content);
    TitleTexture? Rasterize(GeneratedContent content);
    bool IsReady(GeneratedContent content);
    void BeginFrame();
    void Trim();
}

public static class TitleRasterizers
{
    public static ITitleRasterizer Create(IGpu gpu, TitleRasterizerOptions? options = null) =>
        OperatingSystem.IsWindowsVersionAtLeast(7) && gpu is D3D11Gpu d3d
            ? new TitleRasterizer(d3d.Device, options)
            : new SkiaTitleRasterizer(gpu, options);
}
