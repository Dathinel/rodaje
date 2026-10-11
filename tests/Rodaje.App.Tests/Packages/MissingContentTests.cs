using Rodaje.App.Services.Packages;
using Rodaje.Core.Model;
using Rodaje.Paquetes;

namespace Rodaje.App.Tests.Packages;

public class MissingContentTests
{
    [Fact]
    public void Lista_las_LUT_y_transiciones_de_paquetes_que_no_estan_sin_repetir()
    {
        var clip = new Clip { Id = "a" };
        clip.Effects.Add(Effect(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, "rodaje-filtros@1.0.0/lut-calido"));
        clip.Effects.Add(Effect(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, "file:luts/mia.cube"));
        clip.Effects.Add(Effect("otro.efecto", "lut", "nadie@1.0.0/x"));
        var track = new Track { Id = "v", Kind = TrackKind.Video, Clips = [clip] };
        track.Effects.Add(Effect(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, "rodaje-filtros@1.0.0/lut-calido"));
        track.Transitions.Add(new TransitionInstance
        {
            Id = "t",
            Type = BuiltInTransitions.Preset,
            Options = new() { [BuiltInTransitions.PresetOptions.Preset] = "rodaje-transiciones@1.0.0/girar" },
        });
        var sequence = new Sequence { Id = "s", VideoTracks = [track] };
        var project = new Project { Id = "p", Sequences = [sequence] };

        IReadOnlyList<PackageReference> missing = MissingContent.Find(project, PackageScan.Empty);

        Assert.Equal(["rodaje-filtros@1.0.0/lut-calido", "rodaje-transiciones@1.0.0/girar"], missing.Select(r => r.ToString()));
        string text = MissingContent.Describe(missing);
        Assert.Contains("«rodaje-filtros», «rodaje-transiciones»", text, StringComparison.Ordinal);
        Assert.Contains("fundido cruzado", text, StringComparison.Ordinal);
    }

    private static EffectInstance Effect(string type, string option, string value) =>
        new() { Id = Guid.NewGuid().ToString("N"), Type = type, Options = new() { [option] = value } };
}
