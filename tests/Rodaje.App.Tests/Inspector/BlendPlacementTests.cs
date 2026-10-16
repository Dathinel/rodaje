using Rodaje.App.Services.Inspector;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Inspector;

public sealed class BlendPlacementTests
{
    [Theory]
    [InlineData(TrackKind.Video)]
    [InlineData(TrackKind.Audio)]
    public void BlendModeIsNotOfferedAsAFilter(TrackKind kind)
    {
        Assert.DoesNotContain(EffectCatalog.For(kind), effect => effect.Type == BuiltInEffects.Blend);
    }
}
