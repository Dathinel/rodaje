using CsCheck;

namespace Rodaje.Core.Tests.Properties;

/// <summary>
/// Casos de <see cref="SpeedMaterialPropertiesTests"/> que fallaron con una semilla al azar (CI de main y de los PR del
/// 12-oct, y corridas de 300 000 casos en Linux), fijados para que no vuelvan.
/// </summary>
public class SpeedMaterialRegressionTests
{
    /// <summary>
    /// <c>0pZ2Mq9N8IE2</c>: un enlazado que empieza fuera de un cuadro y llega al final del archivo, al volver a 1× iba al
    /// cuadro de antes (perdía material que su primero recuperaba) y desde ahí cada cambio le comía más: tres cuadros.
    /// Los demás: enlazados con el inicio en fases distintas del cuadro, que comparten un material que solo puede caer
    /// en la rejilla de uno. En las cuatro se edita el que empieza fuera de un cuadro. En
    /// <c>7LMaTUJGUpm6</c>, <c>ePnp7RIsF_41</c> y <c>7UwXLCK11vo3</c> el otro, que empieza en un cuadro y tenía el final en
    /// la rejilla, vuelve a 1× con un cuadro de diferencia: es lo que admite la propiedad para el miembro fuera de fase
    /// con el editado.
    /// </summary>
    [Theory]
    [InlineData("0pZ2Mq9N8IE2")]
    [InlineData("7LMaTUJGUpm6")]
    [InlineData("61Idl4YbLW02")]
    [InlineData("ePnp7RIsF_41")]
    [InlineData("7UwXLCK11vo3")]
    public void La_semilla_vuelve_a_1x_con_su_material(string seed)
    {
        bool checkedChain = false;
        SpeedMaterialPropertiesTests.GenChain.Sample(
            s => checkedChain = SpeedMaterialPropertiesTests.RunChain(s.Project, s.Clip, s.Steps, seed),
            seed: seed,
            iter: 1,
            threads: 1);
        Assert.True(checkedChain, $"la semilla {seed} ya no genera una cadena comprobable");
    }
}
