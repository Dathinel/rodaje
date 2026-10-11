using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Model;

/// <summary>Reglas del formato 3 que dependen del registro: tipos, opciones, efectos de pista y duración de transiciones.</summary>
public class RegistryValidationTests
{
    private const string Reference = "rodaje-filtros@1.0.0/calido";

    private static Track V1(Project p) => Seq(p).VideoTracks[0];

    private static Track A1(Project p) => Seq(p).AudioTracks[0];

    /// <summary>«clip-1» [0, 5) y «clip-5» [5, 7) seguidos en Video 1.</summary>
    private static Project WithCut()
    {
        Project p = Create();
        V1(p).Clips.Insert(1, NewClip("clip-5", 5, 2));
        return p;
    }

    private static TransitionInstance Cut(string type = BuiltInTransitions.Crossfade, double seconds = 1) =>
        new() { Id = "t-1", Type = type, From = "clip-1", To = "clip-5", Duration = S(seconds) };

    private static EffectInstance Effect(string type, params (string Name, string Value)[] options) =>
        new() { Id = "fx-n", Type = type, Options = options.ToDictionary(o => o.Name, o => o.Value) };

    private static void AssertSingleError(Project p, string expectedPart)
    {
        IReadOnlyList<string> errors = ModelValidator.Validate(p);
        Assert.Contains(expectedPart, Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Transition_UpToTheShorterClip_IsValid()
    {
        Project p = WithCut();
        V1(p).Transitions.Add(Cut(seconds: 2));
        Assert.Empty(ModelValidator.Validate(p));
    }

    [Fact]
    public void Transition_LongerThanAClip_IsRejected()
    {
        Project p = WithCut();
        TransitionInstance transition = Cut(seconds: 2);
        transition.Duration += Time.FromTicks(1);
        V1(p).Transitions.Add(transition);
        AssertSingleError(p, "dura más que uno de sus clips");
    }

    [Fact]
    public void Transition_WithAnEffectType_IsRejected_AndAnUnknownTypeIsKept()
    {
        Project p = WithCut();
        V1(p).Transitions.Add(Cut(BuiltInEffects.Blur));
        AssertSingleError(p, "no es una transición");

        p = WithCut();
        TransitionInstance unknown = Cut("otro.programa.transicion");
        unknown.Options["cualquiera"] = "lo que sea";
        V1(p).Transitions.Add(unknown);
        Assert.Empty(ModelValidator.Validate(p));
    }

    [Theory]
    [InlineData(BuiltInTransitions.Push, BuiltInTransitions.PushOptions.Direction, "up", true)]
    [InlineData(BuiltInTransitions.Push, BuiltInTransitions.PushOptions.Direction, "diagonal", false)]
    [InlineData(BuiltInTransitions.Push, BuiltInTransitions.PushOptions.Direction, "", false)]
    [InlineData(BuiltInTransitions.Push, "otra", "se conserva", true)]
    [InlineData(BuiltInTransitions.Preset, BuiltInTransitions.PresetOptions.Preset, Reference, true)]
    [InlineData(BuiltInTransitions.Preset, BuiltInTransitions.PresetOptions.Preset, "", true)]
    [InlineData(BuiltInTransitions.Preset, BuiltInTransitions.PresetOptions.Preset, "suave", false)]
    [InlineData(BuiltInTransitions.Crossfade, BuiltInEffects.CommonOptions.Source, "Mal@1/x", false)]
    public void TransitionOptions_FollowTheRegistry(string type, string name, string value, bool valid)
    {
        Project p = WithCut();
        TransitionInstance transition = Cut(type);
        transition.Options[name] = value;
        V1(p).Transitions.Add(transition);

        if (valid)
        {
            Assert.Empty(ModelValidator.Validate(p));
        }
        else
        {
            AssertSingleError(p, $"«{name}», que no es un valor válido");
        }
    }

    [Theory]
    [InlineData(BuiltInEffects.Blend, BuiltInEffects.BlendOptions.Mode, "softLight", true)]
    [InlineData(BuiltInEffects.Blend, BuiltInEffects.BlendOptions.Mode, "Multiply", false)]
    [InlineData(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, Reference, true)]
    [InlineData(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, "", true)]
    [InlineData(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, "C:\\luts\\calido.cube", false)]
    [InlineData(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, "file:luts/cálido.cube", true)]
    [InlineData(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, "file:", false)]
    [InlineData(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, "file:luts/a|b.cube", false)]
    [InlineData(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, "file:luts/a\tb.cube", false)]
    [InlineData(BuiltInEffects.Vignette, BuiltInEffects.CommonOptions.Source, "file:luts/x.cube", false)]
    [InlineData(BuiltInEffects.Vignette, BuiltInEffects.CommonOptions.Source, "rodaje-filtros@2.1.0-beta.1/vineta", true)]
    [InlineData(BuiltInEffects.Vignette, BuiltInEffects.CommonOptions.Source, "", false)]
    [InlineData(BuiltInEffects.Vignette, "futura", "se conserva", true)]
    [InlineData("otro.efecto", BuiltInEffects.BlendOptions.Mode, "lo que sea", true)]
    public void EffectOptions_FollowTheRegistry(string type, string name, string value, bool valid)
    {
        Project p = Create();
        V1(p).Clips[0].Effects.Add(Effect(type, (name, value)));

        if (valid)
        {
            Assert.Empty(ModelValidator.Validate(p));
        }
        else
        {
            AssertSingleError(p, $"«{name}», que no es un valor válido");
        }
    }

    [Fact]
    public void NullOptionValue_IsReportedNotThrown()
    {
        Project p = Create();
        EffectInstance effect = Effect(BuiltInEffects.Blend);
        effect.Options[BuiltInEffects.BlendOptions.Mode] = null!;
        V1(p).Clips[0].Effects.Add(effect);
        AssertSingleError(p, "no es un valor válido");
    }

    [Fact]
    public void ATransitionInAnEffectStack_IsRejected()
    {
        Project p = Create();
        V1(p).Clips[0].Effects.Add(Effect(BuiltInTransitions.Wipe));
        AssertSingleError(p, "que es una transición");
    }

    [Theory]
    [InlineData(TrackKind.Audio, BuiltInEffects.Audio, true)]
    [InlineData(TrackKind.Audio, BuiltInEffects.Color, false)]
    [InlineData(TrackKind.Audio, BuiltInEffects.Speed, true)]
    [InlineData(TrackKind.Video, BuiltInEffects.Color, true)]
    [InlineData(TrackKind.Video, BuiltInEffects.Audio, false)]
    [InlineData(TrackKind.Audio, "otro.efecto", true)]
    public void TrackEffects_MatchTheTrackKind(TrackKind kind, string type, bool valid)
    {
        Project p = Create();
        Track track = kind == TrackKind.Audio ? A1(p) : V1(p);
        track.Effects.Add(Effect(type));

        if (valid)
        {
            Assert.Empty(ModelValidator.Validate(p));
        }
        else
        {
            AssertSingleError(p, "no se puede aplicar a la pista");
        }
    }

    [Fact]
    public void ClipEffects_AreNotCheckedAgainstTheTrackKind()
    {
        // Proyectos anteriores pueden tener volumen en clips de video: se conservan.
        Project p = Create();
        V1(p).Clips[0].Effects.Add(Effect(BuiltInEffects.Audio));
        EffectInstance color = Effect(BuiltInEffects.Color);
        color.Id = "fx-m";
        A1(p).Clips[0].Effects.Add(color);
        Assert.Empty(ModelValidator.Validate(p));
    }

    [Theory]
    [InlineData("rodaje-filtros@1.0.0/calido", true)]
    [InlineData("a@0/b", true)]
    [InlineData("pack.uno@1.2.3+build.5/item-2", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("sin-version/item", false)]
    [InlineData("@1.0.0/item", false)]
    [InlineData("paquete@/item", false)]
    [InlineData("paquete@v1/item", false)]
    [InlineData("paquete@1.0.0/", false)]
    [InlineData("paquete@1.0.0", false)]
    [InlineData("paquete/item@1.0.0", false)]
    [InlineData("Paquete@1.0.0/item", false)]
    [InlineData("paquete@1.0.0/sub/item", false)]
    [InlineData("paquete@1.0 0/item", false)]
    [InlineData("paquete@1.0.0/ítem", false)]
    public void References_AreWellFormed(string? value, bool expected) =>
        Assert.Equal(expected, OptionDefinition.IsWellFormedReference(value));

    [Fact]
    public void References_HaveALengthLimit()
    {
        string longId = new('a', 65);
        Assert.False(OptionDefinition.IsWellFormedReference($"{longId}@1.0.0/x"));
        Assert.False(OptionDefinition.IsWellFormedReference($"x@1.0.0/{longId}"));
        Assert.False(OptionDefinition.IsWellFormedReference($"x@1{new string('0', 64)}/y"));
        Assert.True(OptionDefinition.IsWellFormedReference($"{longId[1..]}@1.0.0/{longId[1..]}"));
    }

    [Fact]
    public void Accepts_RejectsNull()
    {
        OptionDefinition option = EffectRegistry.Find(BuiltInEffects.Blend)!.FindOption(BuiltInEffects.BlendOptions.Mode)!;
        Assert.Throws<ArgumentNullException>(() => option.Accepts(null!));
        Assert.True(option.Accepts("normal"));
        Assert.False(option.Accepts(""));
    }
}
