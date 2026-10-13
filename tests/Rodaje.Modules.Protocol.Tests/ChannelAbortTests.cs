using System.IO.Pipes;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// <c>MessageChannel.Abort</c> desconecta el lado servidor de la tubería antes de cerrarlo (tarea 137, #147). La regresión
/// de <c>Fuzz/EditorCloseTests</c> solo lo veía como un cuelgue probabilístico en Windows con la CPU llena; aquí se
/// comprueba la llamada por su efecto, que no depende de tiempos: <c>DisconnectNamedPipe</c> descarta lo que el módulo
/// no leyó, y solo cerrar el asa lo conserva. Si <c>Abort</c> solo cierra, o cierra antes de desconectar (entonces
/// <c>Disconnect</c> ya no hace nada), el módulo aún lee esos datos y la primera prueba falla.
/// </summary>
/// <remarks>Solo en Windows: fuera de él la tubería es un socket y <c>Abort</c> no desconecta a propósito.</remarks>
public sealed class ChannelAbortTests
{
    private static readonly byte[] Unread = Support.Frame("""{"type":"shutdown"}""");

    [Fact]
    public async Task Abort_disconnects_before_closing_so_the_module_does_not_read_what_was_left()
    {
        if (!OperatingSystem.IsWindows()) return;
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (module)
        {
            await editor.WriteAsync(Unread);
            await editor.FlushAsync();
            MessageChannel channel = new(editor);

            channel.Abort();

            Assert.Equal(0, await ReadAsync(module));
        }
    }

    /// <summary>
    /// El cierre normal (<c>Dispose</c>, tras <c>shutdown</c>) solo cierra y el módulo lee lo que quedaba: así se sabe que
    /// la prueba anterior distingue una cosa de la otra.
    /// </summary>
    [Fact]
    public async Task Dispose_only_closes_and_the_module_still_reads_what_was_left()
    {
        if (!OperatingSystem.IsWindows()) return;
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (module)
        {
            await editor.WriteAsync(Unread);
            await editor.FlushAsync();
            MessageChannel channel = new(editor);

            channel.Dispose();

            Assert.Equal(Unread.Length, await ReadAsync(module));
        }
    }

    /// <summary>Lo que el módulo llega a leer hasta el fin de flujo; un corte se cuenta como fin.</summary>
    private static async Task<int> ReadAsync(Stream module)
    {
        byte[] buffer = new byte[Unread.Length * 2];
        int total = 0;
        try
        {
            int n;
            while ((n = await module.ReadAsync(buffer.AsMemory(total)).AsTask().WaitAsync(Support.CiBound)) > 0) total += n;
        }
        catch (IOException)
        {
        }
        return total;
    }
}
