using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Raw.Tests;

/// <summary>
/// <see cref="ProcessTests.Cancel_is_confirmed_quickly"/> admite hasta <see cref="ProcessTests.CiLimit"/> para confirmar
/// la cancelación de un revelado, pero el cliente, con las opciones de fábrica, cortaba a los 2 s: con la CPU llena,
/// LibRaw tardaba más en pasar de etapa y la prueba fallaba con «El módulo no confirmó la cancelación en 2 s» (tarea 221).
/// La gracia de fábrica no puede quedar por debajo de esa cota.
/// </summary>
public class CancelGraceTests
{
    [Fact]
    public void The_default_grace_is_not_shorter_than_what_the_cancel_test_allows() =>
        Assert.True(ModuleClientOptions.DefaultCancelGrace >= ProcessTests.CiLimit,
            $"El editor da {ModuleClientOptions.DefaultCancelGrace.TotalSeconds} s a la cancelación y la prueba admite {ProcessTests.CiLimit.TotalSeconds} s.");
}
