using System.IO.Pipes;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// <see cref="ModuleProcess.AuthenticateClient"/>: tras aceptar el pipe se comprueba que el proceso del otro
/// lado es el que se lanzó. Un proceso extraño no puede ocupar el nombre del pipe y hacerse pasar por el módulo.
/// </summary>
public sealed class PipeClientAuthenticationTests
{
    [Fact]
    public async Task ElClienteConElPidEsperado_Pasa()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        await using (editor)
        await using (module)
        {
            // El cliente está en este mismo proceso: su PID es el nuestro.
            ModuleProcess.AuthenticateClient(editor, Environment.ProcessId);
            Assert.True(editor.IsConnected);
        }
    }

    [Fact]
    public async Task UnClienteConOtroPid_SeRechazaYCierraElPipe()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        await using (module)
        {
            var ex = Assert.Throws<ModuleProtocolException>(() =>
                ModuleProcess.AuthenticateClient(editor, Environment.ProcessId + 100_000));
            Assert.Contains("PID", ex.Message);
            Assert.False(editor.IsConnected);
            editor.Dispose();
        }
    }
}
