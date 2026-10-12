namespace Rodaje.TestServer.Tests;

/// <summary>Temporal (se quita antes de unir): repite en la CI de Windows el corte tras un fallo 20 veces seguidas.</summary>
public sealed class HandlerFaultRepeatTests
{
    public static TheoryData<int> Runs => [.. Enumerable.Range(1, 20)];

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task Repeat_abort_after_fault(int run)
    {
        Assert.InRange(run, 1, 20);
        var tests = new HandlerFaultTests();
        await tests.An_unexpected_exception_aborts_the_connection_and_is_recorded();
        await tests.A_fault_never_reaches_the_client_as_a_complete_response();
        await tests.The_server_keeps_serving_after_a_fault();
    }
}
