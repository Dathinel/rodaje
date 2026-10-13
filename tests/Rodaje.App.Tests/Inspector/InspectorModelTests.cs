using System.Text.Json;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;

namespace Rodaje.App.Tests.Inspector;

public class AnimationPresetTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static EffectInstance Animated() => new()
    {
        Id = "fx",
        Type = BuiltInEffects.Transform,
        Parameters = new Dictionary<string, Parameter>
        {
            ["scale"] = new() { Value = 1, Keyframes = [new Keyframe(S(12), 0.5, Interpolation.EaseOut), new Keyframe(S(13.5), 1)] },
            ["opacity"] = new() { Value = 1, Keyframes = [new Keyframe(S(12.5), 0, Interpolation.Hold)] },
            ["x"] = new() { Value = 40 },
        },
    };

    [Fact]
    public void Json_TiemposRelativosValoresYCurvas()
    {
        string json = AnimationPreset.ToJson("propios.entrada", "Entrada rápida", Animated(), 1920, 1080);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal("rodaje.preset", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("propios.entrada", root.GetProperty("id").GetString());
        Assert.Equal("Entrada rápida", root.GetProperty("name").GetString());
        Assert.Equal("transition", root.GetProperty("kind").GetString());
        Assert.Equal("rodaje.transform", root.GetProperty("effect").GetString());
        Assert.Equal(1.5, root.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(1920, root.GetProperty("reference").GetProperty("width").GetInt32());

        JsonElement tracks = root.GetProperty("tracks");
        Assert.Equal(["opacity", "scale"], tracks.EnumerateObject().Select(p => p.Name));
        JsonElement[] scale = [.. tracks.GetProperty("scale").EnumerateArray()];
        Assert.Equal(0, scale[0].GetProperty("t").GetDouble());
        Assert.Equal(0.5, scale[0].GetProperty("value").GetDouble());
        Assert.Equal("easeOut", scale[0].GetProperty("interp").GetString());
        Assert.Equal(1.5, scale[1].GetProperty("t").GetDouble());
        Assert.Equal("linear", scale[1].GetProperty("interp").GetString());
        JsonElement opacity = tracks.GetProperty("opacity")[0];
        Assert.Equal(0.5, opacity.GetProperty("t").GetDouble());
        Assert.Equal("hold", opacity.GetProperty("interp").GetString());
        Assert.Contains("Entrada rápida", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SoloConDosKeyframesSeparados()
    {
        Assert.True(AnimationPreset.CanExport(Animated()));
        Assert.False(AnimationPreset.CanExport(null));
        var single = new EffectInstance
        {
            Type = BuiltInEffects.Transform,
            Parameters = new Dictionary<string, Parameter> { ["x"] = new() { Keyframes = [new Keyframe(S(1), 0)] } },
        };
        Assert.False(AnimationPreset.CanExport(single));
        Assert.Throws<InvalidOperationException>(() => AnimationPreset.ToJson("a", "b", single, 1, 1));
    }

    [Theory]
    [InlineData("Entrada rápida", "entrada-rapida")]
    [InlineData("  Zoom   ñandú!! ", "zoom-nandu")]
    [InlineData("***", "animacion")]
    public void Slug(string name, string expected) => Assert.Equal(expected, AnimationPreset.Slug(name));

    [Fact]
    public void Guardar_NoPisaUnoQueYaExiste()
    {
        using var dir = new TempDirectory();
        string first = AnimationPreset.Save(dir.Path, "Zoom", Animated(), 1920, 1080);
        string second = AnimationPreset.Save(dir.Path, "Zoom", Animated(), 1920, 1080);

        Assert.Equal("zoom.json", Path.GetFileName(first));
        Assert.Equal("zoom-2.json", Path.GetFileName(second));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(second));
        Assert.Equal("propios.zoom-2", document.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void CarpetaPropia_DentroDeLosDatosDeRodaje() =>
        Assert.Equal(Path.Combine(Rodaje.App.Services.AppDataPaths.Root, "complementos", "propios"), AnimationPreset.UserFolder);
}

public class ProxyJobListTests
{
    private const string Source = @"C:\videos\toma 1.mp4";

    [Fact]
    public void CicloCompleto_EnColaGenerandoListo()
    {
        var jobs = new ProxyJobList();
        Assert.False(jobs.Apply(new ProxyStatus(Source, ProxyState.Recommended)));
        Assert.Empty(jobs.Rows);

        Assert.True(jobs.Apply(new ProxyStatus(Source, ProxyState.Queued)));
        Assert.Equal(("toma 1.mp4", "En cola", true), (jobs.Rows[0].Title, jobs.Rows[0].Status, jobs.Rows[0].IsActive));
        Assert.Equal(1, jobs.ActiveCount);

        var detail = new ProxyProgress(Source, ProxyJobPhase.Encoding, 0.42, 42, 100, 3.14, "h264_nvenc");
        Assert.True(jobs.Apply(new ProxyStatus(Source, ProxyState.Generating, 0.42, Detail: detail)));
        Assert.Equal("Generando · 42 % · 3,1x", jobs.Rows[0].Status);
        Assert.Equal(0.42, jobs.Rows[0].Progress);
        Assert.False(jobs.Apply(new ProxyStatus(Source, ProxyState.Generating, 0.42, Detail: detail)));

        Assert.True(jobs.Apply(new ProxyStatus(Source, ProxyState.Ready, 1)));
        Assert.Equal(("Listo", false, 1.0), (jobs.Rows[0].Status, jobs.Rows[0].IsActive, jobs.Rows[0].Progress));
        Assert.Equal(0, jobs.ActiveCount);

        Assert.False(jobs.Apply(new ProxyStatus(Source, ProxyState.Recommended)));
        Assert.True(jobs.ClearFinished());
        Assert.Empty(jobs.Rows);
    }

    [Fact]
    public void CanceladoYFallido()
    {
        var jobs = new ProxyJobList();
        jobs.Apply(new ProxyStatus(Source, ProxyState.Queued));
        jobs.Apply(new ProxyStatus(Source, ProxyState.Recommended, Message: "Generación cancelada."));
        Assert.Equal(("Cancelado", false, false), (jobs.Rows[0].Status, jobs.Rows[0].IsActive, jobs.Rows[0].IsError));

        jobs.Apply(new ProxyStatus(@"C:\otro.mov", ProxyState.Failed, Message: "el codificador falló"));
        Assert.Equal(2, jobs.Rows.Count);
        Assert.Equal("Falló: el codificador falló", jobs.Rows[1].Status);
        Assert.True(jobs.Rows[1].IsError);
    }

    [Fact]
    public void ListoSinTrabajoPrevio_NoAparece()
    {
        var jobs = new ProxyJobList();
        Assert.False(jobs.Apply(new ProxyStatus(Source, ProxyState.Ready, 1)));
        Assert.False(jobs.ClearFinished());
    }
}

public class EffectCatalogTests
{
    [Fact]
    public void Video_OfreceLosFiltrosQueAplicaElMotor_YAudioSuVolumen()
    {
        Assert.Equal(
            ["Transformar", "Color y LUT", "Viñeta", "Desenfoque", "Nitidez", "Modo de mezcla", "Recortar", "Pixelar", "Anclaje de esquinas", "Croma (fondo verde)",
                "Ruedas de color", "Curvas", "Grano de película", "Resplandor", "Aberración cromática", "Reducción de ruido",
                "Espejo y pantalla dividida", "Distorsión de lente", "Corrección secundaria (HSL)"],
            EffectCatalog.For(TrackKind.Video).Select(e => e.Name));
        Assert.Equal([BuiltInEffects.Audio, BuiltInEffects.Equalizer, BuiltInEffects.Compressor, BuiltInEffects.Gate, BuiltInEffects.DeEsser, BuiltInEffects.Reverb,
            BuiltInEffects.Delay, BuiltInEffects.Saturation, BuiltInEffects.GraphicEqualizer, BuiltInEffects.PitchShift, BuiltInEffects.Limiter],
            EffectCatalog.For(TrackKind.Audio).Select(e => e.Type));
        Assert.Equal("paquete.raro", EffectCatalog.DisplayName("paquete.raro"));
    }

    [Fact]
    public void SeAplica_SaleDelRegistro()
    {
        foreach (EffectDescriptor descriptor in EffectCatalog.All)
        {
            EffectStage stage = Rodaje.Core.Effects.EffectRegistry.Find(descriptor.Type)!.Stage;
            Assert.Equal(stage != EffectStage.Deferred, descriptor.IsRendered);
        }

        Assert.Null(EffectCatalog.Find(BuiltInEffects.Speed));
        Assert.True(EffectCatalog.Find(BuiltInEffects.Stabilize)!.IsRendered);
        Assert.DoesNotContain(EffectCatalog.For(TrackKind.Video), e => e.Type == BuiltInEffects.Stabilize);
        Assert.True(EffectCatalog.Find(BuiltInEffects.Blur)!.IsRendered);
    }

    [Fact]
    public void Soporte_AplicadoPendienteTapadoYDesconocido()
    {
        var first = new EffectInstance { Id = "a", Type = BuiltInEffects.Transform };
        var second = new EffectInstance { Id = "b", Type = BuiltInEffects.Transform };
        var blur = new EffectInstance { Id = "c", Type = BuiltInEffects.Blur };
        var speed = new EffectInstance { Id = "d", Type = BuiltInEffects.Speed };
        var unknown = new EffectInstance { Id = "e", Type = "otro.efecto" };
        var clip = new Clip { Effects = [first, second, blur, speed, unknown] };

        Assert.Equal(EffectSupport.Applied, EffectCatalog.Support(clip, first, TrackKind.Video));
        Assert.Equal(EffectSupport.Shadowed, EffectCatalog.Support(clip, second, TrackKind.Video));
        Assert.Equal(EffectSupport.Applied, EffectCatalog.Support(clip, blur, TrackKind.Video));
        Assert.Equal(EffectSupport.Pending, EffectCatalog.Support(clip, speed, TrackKind.Video));
        Assert.Equal(EffectSupport.Pending, EffectCatalog.Support(clip, first, TrackKind.Audio));
        Assert.Equal(EffectSupport.Unknown, EffectCatalog.Support(clip, unknown, TrackKind.Video));

        first.Enabled = false;
        Assert.Equal(EffectSupport.Applied, EffectCatalog.Support(clip, second, TrackKind.Video));
    }
}

public class ParameterSpecTests
{
    [Fact]
    public void EscalaYOpacidad_SeVenEnPorcentaje()
    {
        IReadOnlyList<ParameterSpec> specs = ParameterSpec.Transform(1920, 1080);
        ParameterSpec scale = specs.Single(s => s.Name == "scale");
        Assert.Equal(150, scale.ToDisplay(1.5));
        Assert.Equal(1.505, scale.ToModel(150.5), 10);
        Assert.Equal("150.5", scale.Format(1.505));
        Assert.Equal(-1920, specs[0].Minimum);
        Assert.Equal(1080, specs[1].Maximum);
        // El obturador, su fase y sus muestras del desenfoque de movimiento no se animan; lo demás, sí.
        Assert.Equal(["shutterAngle", "shutterPhase", "motionSamples"], specs.Where(s => !s.Animatable).Select(s => s.Name));
    }

    [Fact]
    public void Restablecer_UsaLosValoresDeFabricaDelModelo()
    {
        IReadOnlyList<ParameterSpec> specs = ParameterSpec.Transform(1920, 1080);
        Assert.All(specs, s => Assert.Equal(BuiltInEffects.TransformParameters.DefaultValue(s.Name), s.DefaultValue));
        Assert.Equal("Restablecer posición X", specs[0].ResetDescription);
        Assert.Equal("Restablecer opacidad", specs[4].ResetDescription);
    }

    [Fact]
    public void Audio_FundidosHastaLaMitadDelClip()
    {
        IReadOnlyList<ParameterSpec> specs = ParameterSpec.Audio(7.3);
        Assert.Equal(3.6, specs[1].Maximum, 6);
        Assert.True(specs[0].Animatable);
        Assert.False(specs[1].Animatable);
        Assert.False(specs[2].Animatable);
    }

    [Fact]
    public void Curvas_EnElOrdenDelDesplegable()
    {
        Assert.Equal(["Lineal", "Suave", "Entrada", "Salida", "Rebote", "Elástico", "Bézier", "Mantener"], KeyframeEase.Options.Select(o => o.Name));
        Assert.Equal(1, KeyframeEase.IndexOf(Interpolation.EaseInOut));
        Assert.Equal(7, KeyframeEase.IndexOf(Interpolation.Hold));
        Assert.All(Enum.GetValues<Interpolation>(), i => Assert.Equal(i, KeyframeEase.Options[KeyframeEase.IndexOf(i)].Value));
    }
}
