using Rodaje.App.Services.Effects;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Engine.Timeline;
using Rodaje.Paquetes.Content;
using Rodaje.Playback.Composition;

namespace Rodaje.App.Tests.Effects;

/// <summary>Contenido de paquetes de mentira: lo que la prueba registra, contando las lecturas.</summary>
internal sealed class FakeContentLoader : IContentLoader
{
    public Dictionary<string, LutTable> Luts { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, LutTable> LutFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, TransitionPreset> Transitions { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, FilterPreset> Filters { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, EffectPreset> Effects { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, TextStylePreset> TextStyles { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Broken { get; } = new(StringComparer.Ordinal);

    public int Reads;

    public static LutTable Identity(int size = 2)
    {
        float[] rgb = new float[size * size * size * 3];
        int i = 0;
        for (int b = 0; b < size; b++)
        {
            for (int g = 0; g < size; g++)
            {
                for (int r = 0; r < size; r++)
                {
                    rgb[i++] = r / (size - 1f);
                    rgb[i++] = g / (size - 1f);
                    rgb[i++] = b / (size - 1f);
                }
            }
        }

        return new LutTable("", size, rgb);
    }

    public LutTable? LoadLut(string reference) => Get(Luts, reference);

    public LutTable LoadLutFile(string path) => Get(LutFiles, path) ?? throw new PackageContentException($"No existe «{path}».");

    public TransitionPreset? LoadTransition(string reference) => Get(Transitions, reference);

    public FilterPreset? LoadFilter(string reference) => Get(Filters, reference);

    public EffectPreset? LoadEffect(string reference) => Get(Effects, reference);

    public TextStylePreset? LoadTextStyle(string reference) => Get(TextStyles, reference);

    private T? Get<T>(Dictionary<string, T> items, string key)
        where T : class
    {
        Interlocked.Increment(ref Reads);
        if (Broken.Contains(key))
        {
            throw new PackageContentException($"«{key}» está dañado.");
        }

        return items.GetValueOrDefault(key);
    }
}

public class EffectContentTests
{
    private const string Look = "rodaje-filtros@1.0.0/look-calido";

    [Fact]
    public async Task Lut_SeCargaFueraDelHilo_YLuegoSaleDeLaCache()
    {
        var loader = new FakeContentLoader();
        loader.Luts[Look] = FakeContentLoader.Identity(3);
        var content = new EffectContent(loader);
        int changed = 0;
        content.Changed += (_, _) => Interlocked.Increment(ref changed);

        Assert.False(content.TryGetLut(new ResourceKey(Look), out LutData? lut));
        Assert.Null(lut);
        await content.WhenLoadedAsync(Look);

        Assert.True(content.TryGetLut(new ResourceKey(Look), out lut));
        Assert.Equal(3, lut!.Size);
        Assert.True(content.TryGetLut(new ResourceKey(Look), out _));
        Assert.Equal(1, loader.Reads);
        Assert.Equal(1, Volatile.Read(ref changed));
    }

    [Fact]
    public async Task LutQueFaltaODaniada_NoSeReintenta_HastaInvalidar()
    {
        var loader = new FakeContentLoader();
        loader.Broken.Add("roto@1.0.0/lut");
        var content = new EffectContent(loader);
        var problems = new List<string>();
        content.Problem += (_, e) => { lock (problems) { problems.Add(e.Reference); } };

        Assert.False(content.TryGetLut(new ResourceKey("falta@1.0.0/lut"), out _));
        Assert.False(content.TryGetLut(new ResourceKey("roto@1.0.0/lut"), out _));
        await content.WhenLoadedAsync("falta@1.0.0/lut");
        await content.WhenLoadedAsync("roto@1.0.0/lut");
        Assert.False(content.TryGetLut(new ResourceKey("falta@1.0.0/lut"), out _));
        Assert.Equal(2, loader.Reads);
        Assert.Equal(["roto@1.0.0/lut"], problems);

        loader.Luts["falta@1.0.0/lut"] = FakeContentLoader.Identity();
        content.Invalidate();
        Assert.False(content.TryGetLut(new ResourceKey("falta@1.0.0/lut"), out _));
        await content.WhenLoadedAsync("falta@1.0.0/lut");
        Assert.True(content.TryGetLut(new ResourceKey("falta@1.0.0/lut"), out _));
        Assert.False(content.TryGetLut(new ResourceKey(""), out _));
    }

    [Fact]
    public async Task LutDeArchivo_RelativaALaCarpetaDelProyecto()
    {
        string folder = Path.Combine(Path.GetTempPath(), "proyecto con ñ");
        string file = Path.Combine(folder, "luts", "mi look.cube");
        var loader = new FakeContentLoader();
        loader.LutFiles[file] = FakeContentLoader.Identity();
        var content = new EffectContent(loader);
        string reference = LutReference.ForFile(file, Path.Combine(folder, "corto.rodaje"));
        Assert.Equal("file:luts/mi look.cube", reference);

        Assert.False(content.TryGetLut(new ResourceKey(reference), out _));
        await content.WhenLoadedAsync(reference);
        Assert.False(content.TryGetLut(new ResourceKey(reference), out _));

        content.ProjectFolder = folder;
        Assert.False(content.TryGetLut(new ResourceKey(reference), out _));
        await content.WhenLoadedAsync(reference);
        Assert.True(content.TryGetLut(new ResourceKey(reference), out _));
    }

    [Fact]
    public void Referencias_DeArchivo()
    {
        string other = Path.Combine(Path.GetTempPath(), "x.cube");
        Assert.Equal("file:" + Path.GetFullPath(other).Replace('\\', '/'), LutReference.ForFile(other, null));
        Assert.Equal(Path.GetFullPath(other), LutReference.ResolveFile(LutReference.ForFile(other, null), null));
        Assert.Null(LutReference.ResolveFile("file:rel/x.cube", null));
        Assert.Null(LutReference.ResolveFile("paquete@1.0.0/lut", @"C:\p"));
        Assert.Equal("mi look", LutReference.DisplayName("file:luts/mi look.cube", Rodaje.Paquetes.PackageScan.Empty));
        Assert.Equal("paquete@1.0.0/lut", LutReference.DisplayName("paquete@1.0.0/lut", Rodaje.Paquetes.PackageScan.Empty));
    }

    [Fact]
    public void Transicion_CurvasDeCeroAUno_YMezclaDelRegistro()
    {
        var loader = new FakeContentLoader();
        loader.Transitions["p@1.0.0/zoom"] = new TransitionPreset("zoom", "Zoom", 0.5, BuiltInTransitions.Dip, 1920, 1080,
            [new PresetCurve(BuiltInEffects.Transform, "scale", [new PresetKey(0, 1, PresetInterpolation.EaseIn), new PresetKey(0.5, 2, PresetInterpolation.Linear)])],
            [new PresetCurve(BuiltInEffects.Transform, "opacity", [new PresetKey(0.25, 0, PresetInterpolation.Hold)]),
             new PresetCurve(BuiltInEffects.Color, "exposure", [new PresetKey(0, 1, PresetInterpolation.Linear)])]);
        loader.Transitions["p@1.0.0/rara"] = new TransitionPreset("rara", "Rara", 1, "otra.mezcla", 1280, 720, [], []);
        var content = new EffectContent(loader);

        TransitionCurves curves = content.FindTransition("p@1.0.0/zoom")!;
        Assert.Equal(EffectRegistry.Find(BuiltInTransitions.Dip)!.Code, curves.MixCode);
        Assert.Equal((1920, 1080), (curves.ReferenceWidth, curves.ReferenceHeight));
        Assert.Equal([0.0, 1.0], curves.Outgoing[0].Keys.Select(k => k.Progress));
        Assert.Equal(Interpolation.EaseIn, curves.Outgoing[0].Keys[0].Interpolation);
        Assert.Equal("opacity", Assert.Single(curves.Incoming).Parameter);
        Assert.Equal(0.5, curves.Incoming[0].Keys[0].Progress);

        Assert.Equal(EffectRegistry.Find(BuiltInTransitions.Crossfade)!.Code, content.FindTransition("p@1.0.0/rara")!.MixCode);
        Assert.Null(content.FindTransition("p@1.0.0/falta"));
        Assert.Same(curves, content.FindTransition("p@1.0.0/zoom"));
        Assert.Equal(3, loader.Reads);
    }

    [Fact]
    public void Transicion_LlevaLosParametrosYOpcionesDeSuMezcla()
    {
        var loader = new FakeContentLoader();
        loader.Transitions["p@1.0.0/barrido"] = new TransitionPreset("barrido", "Barrido", 0.5, BuiltInTransitions.Wipe, 1920, 1080, [], [])
        {
            MixParameters = new Dictionary<string, double> { ["angle"] = 90, ["softness"] = 0.2 },
            MixOptions = new Dictionary<string, string> { ["direction"] = "up" },
        };

        TransitionCurves curves = new EffectContent(loader).FindTransition("p@1.0.0/barrido")!;

        Assert.Equal(EffectRegistry.Find(BuiltInTransitions.Wipe)!.Code, curves.MixCode);
        Assert.Equal(90, curves.MixParameters["angle"]);
        Assert.Equal(0.2, curves.MixParameters["softness"]);
        Assert.Equal("up", curves.MixOptions["direction"]);
    }
}
