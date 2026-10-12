using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Hardened;

/// <summary>
/// Versión endurecida de <c>ModelValidatorTests.AnyCombinationOfExtremes_NeverThrows</c>: allí bastaba con que el
/// validador devolviera algún error (<c>Assert.NotEmpty</c>), aunque fuera de otro clip o no dijera nada de los tiempos.
/// Aquí cada clip tiene que aparecer con los errores que corresponden a sus tiempos, y las dos entradas del validador
/// tienen que coincidir.
/// </summary>
public class HardenedModelValidatorTests
{
    [Theory]
    [InlineData(long.MinValue, long.MinValue, long.MinValue)]
    [InlineData(long.MaxValue, long.MaxValue, long.MaxValue)]
    [InlineData(long.MinValue, long.MaxValue, long.MinValue)]
    [InlineData(long.MaxValue, long.MinValue, long.MaxValue)]
    [InlineData(-1, long.MinValue, long.MaxValue)]
    public void Extreme_times_are_reported_for_every_clip_with_the_right_reason(long start, long sourceIn, long sourceOut)
    {
        Project p = ValidProject();
        Sequence sequence = p.Sequences[0];
        var clips = new List<(string Track, string Clip)>();
        foreach (Track track in sequence.AllTracks)
        {
            foreach (Clip clip in track.Clips)
            {
                clip.Start = Time.FromTicks(start);
                clip.SourceIn = Time.FromTicks(sourceIn);
                clip.SourceOut = Time.FromTicks(sourceOut);
                clips.Add((track.Name, clip.Id));
            }
        }

        Assert.NotEmpty(clips);
        IReadOnlyList<string> errors = ModelValidator.Validate(p);
        Assert.Equal(errors, ModelValidator.ValidateSequence(p, sequence));

        // Lo que tiene que decir cada clip según sus tiempos (la suma que no cabe en 64 bits es «fuera de rango»).
        bool negativeIn = sourceIn < 0;
        bool beforeZero = start < 0;
        bool emptyOrNegative = sourceOut <= sourceIn;
        bool overflows = (Int128)sourceOut - sourceIn > long.MaxValue || (Int128)start + ((Int128)sourceOut - sourceIn) > long.MaxValue
            || (Int128)start + ((Int128)sourceOut - sourceIn) < long.MinValue;
        foreach ((string track, string id) in clips)
        {
            string prefix = $"El clip «{id}» de la pista «{track}»";
            List<string> mine = [.. errors.Where(e => e.StartsWith(prefix, StringComparison.Ordinal))];
            Assert.True(mine.Count > 0, $"No hay ningún error del clip «{id}»: {string.Join(" | ", errors)}");
            Assert.Equal(negativeIn, mine.Contains($"{prefix} tiene un punto de entrada negativo."));
            Assert.Equal(beforeZero, mine.Contains($"{prefix} empieza antes del inicio de la línea de tiempo."));
            if (emptyOrNegative)
            {
                Assert.Contains($"{prefix} tiene duración cero o negativa.", mine);
            }

            if (overflows)
            {
                Assert.Contains($"{prefix} tiene tiempos fuera de rango.", mine);
            }
        }

        // Ningún error habla de algo que no sea un clip con tiempos extremos (los medios y las pistas siguen bien).
        Assert.All(errors, e => Assert.StartsWith("El clip «", e, StringComparison.Ordinal));
    }
}
