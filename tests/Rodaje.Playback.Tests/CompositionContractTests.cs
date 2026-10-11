using System.Runtime.CompilerServices;
using Rodaje.Core.Effects;
using Rodaje.Playback.Composition;

namespace Rodaje.Playback.Tests;

/// <summary>Lo que el compositor recibe del motor coincide con el registro de efectos del núcleo.</summary>
public class CompositionContractTests
{
    [Fact]
    public void BlendModes_MatchTheRegistryInOrder()
    {
        Assert.Equal(
            EffectRegistry.BlendModes.AsEnumerable(),
            Enum.GetValues<BlendMode>().Select(m => char.ToLowerInvariant(m.ToString()[0]) + m.ToString()[1..]));
    }

    [Fact]
    public void EffectValues_HoldEveryParameterByValue()
    {
        var values = default(EffectValues);
        values[EffectDefinition.MaxParameters - 1] = 2.5f;

        Assert.Equal(EffectDefinition.MaxParameters * sizeof(float), Unsafe.SizeOf<EffectValues>());
        Assert.Equal(2.5f, values[EffectDefinition.MaxParameters - 1]);
        Assert.True(EffectRegistry.All.All(d => d.Parameters.Length <= EffectDefinition.MaxParameters));
    }

    [Fact]
    public void LutData_ChecksItsShape()
    {
        var lut = new LutData(2, new float[24]);

        Assert.Equal(2, lut.Size);
        Assert.Equal(24, lut.Rgb.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LutData(1, new float[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LutData(66, new float[3]));
        Assert.Throws<ArgumentException>(() => new LutData(2, new float[23]));
    }
}
