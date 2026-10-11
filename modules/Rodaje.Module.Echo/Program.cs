using System.Text.Json;
using Rodaje.Modules.Protocol;

ModuleInfo info = new("rodaje-eco", "0.1.0", "Eco",
[
    new FunctionInfo("eco", "Devolver los parámetros", ReportsProgress: false),
    new FunctionInfo("contar", "Contar con avance"),
    new FunctionInfo("copiar", "Copiar el primer búfer en el segundo", ReportsProgress: false, Inputs: ["video"], Outputs: ["video"]),
    new FunctionInfo("morir", "Terminar el proceso a mitad de una llamada", Cancellable: false, ReportsProgress: false),
    new FunctionInfo("colgar", "Ignorar la cancelación", Cancellable: false, ReportsProgress: false),
    new FunctionInfo("fallar", "Lanzar una excepción", ReportsProgress: false),
], Device: "cpu");

ModuleHost host = new(info);

host.Register("eco", (call, _) => ValueTask.FromResult(new ModuleOutput(call.Parameters)));

host.Register("contar", async (call, ct) =>
{
    int steps = Int(call.Parameters, "pasos", 10);
    int delay = Int(call.Parameters, "msPorPaso", 10);
    for (int i = 1; i <= steps; i++)
    {
        await Task.Delay(delay, ct).ConfigureAwait(false);
        await call.ReportProgressAsync((double)i / steps, "contando", ct).ConfigureAwait(false);
    }
    using JsonDocument doc = JsonDocument.Parse($$"""{"pasos":{{steps}}}""");
    return new ModuleOutput(doc.RootElement.Clone());
});

host.Register("copiar", (call, _) =>
{
    if (call.Buffers.Count != 2 || call.Buffers[0].Length != call.Buffers[1].Length)
        throw new ModuleCallException(ErrorCodes.InvalidParameters, "«copiar» necesita dos búferes del mismo tamaño.");
    call.Map(call.Buffers[0]).CopyTo(call.Map(call.Buffers[1]));
    return ValueTask.FromResult(new ModuleOutput(Buffers: [call.Buffers[1]]));
});

host.Register("morir", (call, _) =>
{
    Environment.Exit(Int(call.Parameters, "codigo", 3));
    return ValueTask.FromResult(new ModuleOutput());
});

host.Register("colgar", async (_, _) =>
{
    await Task.Delay(Timeout.Infinite, CancellationToken.None).ConfigureAwait(false);
    return new ModuleOutput();
});

host.Register("fallar", (_, _) => throw new InvalidOperationException("Fallo a propósito."));

return await host.RunAsync(args).ConfigureAwait(false);

static int Int(JsonElement? parameters, string name, int fallback) =>
    parameters is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int n) ? n : fallback;
