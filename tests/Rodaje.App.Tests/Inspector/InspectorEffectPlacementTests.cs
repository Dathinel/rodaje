using Rodaje.App.Services.Inspector;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Inspector;

public sealed class InspectorEffectPlacementTests
{
    [Theory]
    [InlineData(TrackKind.Video)]
    [InlineData(TrackKind.Audio)]
    public void TransformIsNotOfferedAsAFilter(TrackKind kind)
    {
        Assert.DoesNotContain(EffectCatalog.For(kind), effect => effect.Type == BuiltInEffects.Transform);
    }
}
