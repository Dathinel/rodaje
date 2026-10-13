using System.IO.Pipes;
using System.Text;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Tarea 211: una llamada salía de <c>active</c> al terminar su función y después escribía su respuesta. Si en ese
/// hueco llegaba <c>shutdown</c> (o el editor cerraba), <c>ModuleHost.RunAsync</c> solo esperaba a lo que quedaba en
/// <c>active</c>, volvía y desechaba el canal: la respuesta final se perdía en silencio. En la CI de Windows lo destapó
/// <c>HostileMessageTests</c> con la semilla 21261124 («La llamada 2 tuvo 0 respuestas finales»); en Linux, una llamada
/// a «fallar» seguida de <c>shutdown</c> en la misma escritura perdía la respuesta en 133 a 166 de cada 1000 rondas, y
/// en 45 a 162 con el código de antes de la tarea 195 (#193), que solo cambió el hilo en que corre la función.
/// </summary>
[Collection(FuzzGroup.Name)]
public sealed class ReplyRaceTests
{
    private const string Hello = """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""";

    /// <summary>
    /// Determinista: la costura detiene la llamada justo en el hueco (id libre, respuesta sin escribir) y entonces llega
    /// <c>shutdown</c>. El cierre tiene que esperar a la respuesta, y la respuesta tiene que llegar.
    /// </summary>
    [Fact]
    public async Task Shutdown_entre_el_fin_de_la_funcion_y_la_respuesta_no_pierde_la_respuesta()
    {
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ModuleHost host = new(Support.EchoInfo) { BeforeReplyForTests = async _ => { reached.TrySetResult(); await gate.Task; } };
        host.Register("eco", (call, _) => ValueTask.FromResult(new ModuleOutput(call.Parameters)));

        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (editor)
        using (module)
        {
            Task<bool> run = host.RunAsync(module);
            await editor.WriteAsync(Support.Frame(Hello));
            await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "el welcome");
            Task<List<byte[]>> drain = FuzzSupport.DrainAsync(editor);
            await editor.WriteAsync(Support.Frame("""{"type":"invoke","id":2,"function":"eco"}"""));
            await FuzzSupport.Capped(reached.Task, "la llamada");
            await editor.WriteAsync(Support.Frame("""{"type":"shutdown"}"""));

            // Sin el arreglo, RunAsync vuelve enseguida y cierra el canal con la respuesta sin escribir.
            await Task.WhenAny(run, Task.Delay(500));
            bool closedEarly = run.IsCompleted;
            gate.SetResult();
            Assert.True(await FuzzSupport.Capped(run, "RunAsync"));
            module.Dispose();
            List<string> replies = [.. (await FuzzSupport.Capped(drain, "la lectura")).Select(r => Encoding.UTF8.GetString(r))];

            Assert.False(closedEarly, "RunAsync terminó sin esperar la respuesta de una llamada que ya había salido de «active».");
            ResultMessage result = Assert.IsType<ResultMessage>(MessageChannel.Parse(Encoding.UTF8.GetBytes(Assert.Single(replies))));
            Assert.Equal(2, result.Id);
        }
    }

    /// <summary>
    /// Tarea 295. Determinista: el hilo de la llamada no arranca hasta que <c>RunAsync</c> ha vuelto (en la CI de Windows,
    /// <see cref="Thread.Start()"/> llegó a tardar más de 10 s), así que el cierre agota su espera de 2 s con la llamada sin
    /// contestar. Antes se cerraba el canal sin más y la llamada se quedaba sin respuesta final («6 de 300 llamadas
    /// quedaron sin respuesta final» en <see cref="Una_llamada_y_shutdown_seguidos_siempre_tienen_respuesta"/>). Ahora el
    /// cierre la contesta con <c>cancelled</c> antes de cerrar, y la llamada, cuando por fin arranca, no contesta otra vez.
    /// El orden lo fija la costura, no el reloj: el hilo se suelta solo cuando <c>RunAsync</c> ya terminó.
    /// <para>
    /// Tarea 302: lo que hace la llamada al arrancar tarde se comprueba después de soltarla y de que su tarea termine
    /// (<c>AfterCallForTests</c>). El módulo escribe a través de <see cref="KeepOpenStream"/>, que no cierra la tubería al
    /// desechar el canal: una respuesta escrita tarde llegaría al editor. Ni la función corre (<c>ran</c>) ni sale otra
    /// trama. Antes de la tarea 302, con la comprobación previa a la función quitada o con la respuesta enviada sin
    /// reclamarla, la prueba seguía en verde porque solo miraba lo que había llegado antes de soltar el hilo.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Una_llamada_cuyo_hilo_no_arranca_antes_de_cerrar_recibe_cancelled()
    {
        using ManualResetEventSlim reached = new();
        StartGate gate = new();
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int ran = 0;
        ModuleHost host = new(Support.EchoInfo)
        {
            BeforeCallThreadStartsForTests = _ =>
            {
                reached.Set();
                gate.Wait();
            },
            AfterCallForTests = _ => ended.TrySetResult(),
        };
        host.Register("eco", (call, _) =>
        {
            Interlocked.Increment(ref ran);
            return ValueTask.FromResult(new ModuleOutput(call.Parameters));
        });

        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (editor)
        using (module)
        {
            try
            {
                Task<bool> run = host.RunAsync(new KeepOpenStream(module));
                await editor.WriteAsync(Support.Frame(Hello));
                await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "el welcome");
                Task<List<byte[]>> drain = FuzzSupport.DrainAsync(editor);
                await editor.WriteAsync((byte[])[.. Support.Frame("""{"type":"invoke","id":2,"function":"eco"}"""), .. Support.Frame("""{"type":"shutdown"}""")]);
                Assert.True(reached.Wait(FuzzSupport.CaseCap), "La llamada no llegó a arrancar su hilo.");

                Assert.True(await FuzzSupport.Capped(run, "RunAsync con un hilo de llamada sin arrancar"));

                // Se suelta el hilo y se espera a que la tarea de la llamada termine, con la tubería del módulo aún abierta.
                gate.Open();
                await FuzzSupport.Capped(ended.Task, "la tarea de la llamada");
                module.Dispose();
                List<byte[]> replies = await FuzzSupport.Capped(drain, "la lectura hasta el fin de flujo");

                Assert.Equal(0, Volatile.Read(ref ran));
                ErrorMessage error = Assert.IsType<ErrorMessage>(MessageChannel.Parse(Assert.Single(replies)));
                Assert.Equal(2, error.Id);
                Assert.Equal(ErrorCodes.Cancelled, error.Code);
            }
            finally
            {
                gate.Open();
            }
        }
    }

    /// <summary>Llamadas sin arrancar en las pruebas del tope del cierre (tarea 303); igual a su <c>MaxConcurrentCalls</c>.</summary>
    private const int Unstarted = 32;

    /// <summary>
    /// Tarea 303. Con el editor sin leer, el cierre contestaba cada llamada sin arrancar con un <c>cancelled</c> en serie,
    /// 1 s cada uno, y luego esperaba sin plazo: con 32 llamadas, <c>RunAsync</c> tardaba unos 34 s (2 s de espera más 32
    /// envíos de 1 s). Ahora toda la respuesta del cierre tiene un tope de 2 s, así que vuelve en unos 4 s. El tope de la
    /// prueba (10 s) queda lejos de los dos: no depende de lo rápido que sea el equipo. El módulo escribe por
    /// <see cref="UnreadStream"/>: tras el <c>welcome</c> ninguna escritura avanza hasta que se cancela, como una tubería
    /// llena, y cerrar el canal no la desatasca.
    /// </summary>
    [Fact]
    public async Task Con_el_editor_sin_leer_el_cierre_contesta_las_llamadas_sin_arrancar_dentro_de_un_tope()
    {
        using ManualResetEventSlim reached = new();
        StartGate gate = new();
        int ran = 0;
        ModuleHost host = new(Support.EchoInfo)
        {
            MaxConcurrentCalls = Unstarted,
            BeforeCallThreadStartsForTests = _ =>
            {
                reached.Set();
                gate.Wait();
            },
        };
        host.Register("eco", (call, _) =>
        {
            Interlocked.Increment(ref ran);
            return ValueTask.FromResult(new ModuleOutput(call.Parameters));
        });

        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (editor)
        using (module)
        {
            try
            {
                UnreadStream unread = new(module);
                Task<bool> run = host.RunAsync(unread);
                await editor.WriteAsync(Support.Frame(Hello));
                await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "el welcome");
                unread.StopReading();
                await editor.WriteAsync(Burst());
                Assert.True(reached.Wait(FuzzSupport.CaseCap), "La primera llamada no llegó a arrancar su hilo.");

                // Sin el tope, RunAsync no vuelve en unos 34 s.
                TimeSpan cap = TimeSpan.FromSeconds(10);
                bool returned = await Task.WhenAny(run, Task.Delay(cap)) == run;
                Assert.True(returned, $"Con el editor sin leer y {Unstarted} llamadas sin arrancar, RunAsync no volvió en {cap.TotalSeconds:0} s.");
                Assert.True(await run);
                Assert.Equal(0, Volatile.Read(ref ran));
            }
            finally
            {
                gate.Open();
            }
        }
    }

    /// <summary>
    /// Tarea 303, el otro lado del tope: con el editor leyendo, todas las llamadas sin arrancar reciben su <c>cancelled</c>
    /// (una sola respuesta final cada una) y ninguna corre su función.
    /// </summary>
    [Fact]
    public async Task Con_el_editor_leyendo_el_cierre_contesta_todas_las_llamadas_sin_arrancar()
    {
        using ManualResetEventSlim reached = new();
        StartGate gate = new();
        int ran = 0;
        ModuleHost host = new(Support.EchoInfo)
        {
            MaxConcurrentCalls = Unstarted,
            BeforeCallThreadStartsForTests = _ =>
            {
                reached.Set();
                gate.Wait();
            },
        };
        host.Register("eco", (call, _) =>
        {
            Interlocked.Increment(ref ran);
            return ValueTask.FromResult(new ModuleOutput(call.Parameters));
        });

        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (editor)
        using (module)
        {
            try
            {
                Task<bool> run = host.RunAsync(module);
                await editor.WriteAsync(Support.Frame(Hello));
                await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "el welcome");
                Task<List<byte[]>> drain = FuzzSupport.DrainAsync(editor);
                await editor.WriteAsync(Burst());
                Assert.True(reached.Wait(FuzzSupport.CaseCap), "La primera llamada no llegó a arrancar su hilo.");

                Assert.True(await FuzzSupport.Capped(run, "RunAsync con las llamadas sin arrancar"));
                List<byte[]> replies = await FuzzSupport.Capped(drain, "la lectura hasta el fin de flujo");

                List<ErrorMessage> errors = [.. replies.Select(r => Assert.IsType<ErrorMessage>(MessageChannel.Parse(r)))];
                Assert.All(errors, e => Assert.Equal(ErrorCodes.Cancelled, e.Code));
                Assert.Equal(Enumerable.Range(2, Unstarted).Select(i => (long?)i), errors.Select(e => e.Id).Order());
                Assert.Equal(0, Volatile.Read(ref ran));
            }
            finally
            {
                gate.Open();
            }
        }
    }

    /// <summary>
    /// Tarea 303, la rama <c>Started</c>: una llamada cuya función ya empezó y no atiende la cancelación (espera una puerta)
    /// no recibe <c>cancelled</c> del cierre, porque su respuesta es suya, pero el cierre tampoco la espera para siempre:
    /// <c>RunAsync</c> vuelve dentro de su tope (2 s de espera más 2 s de <c>UnansweredReplyBudget</c>; el de la prueba es
    /// de 10 s). La puerta se suelta cuando la tubería del editor ya se leyó hasta el fin de flujo, así que lo que se
    /// comprueba es lo que salió durante el cierre: nada. Antes de la tarea 303, <c>RunAsync</c> no volvía.
    /// </summary>
    [Fact]
    public async Task Una_llamada_empezada_que_no_atiende_la_cancelacion_no_recibe_cancelled_y_el_cierre_vuelve()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ModuleHost host = new(Support.EchoInfo);
        host.Register("eco", async (call, _) =>
        {
            started.TrySetResult();
            await gate.Task;
            return new ModuleOutput(call.Parameters);
        });

        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (editor)
        using (module)
        {
            try
            {
                Task<bool> run = host.RunAsync(module);
                await editor.WriteAsync(Support.Frame(Hello));
                await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "el welcome");
                Task<List<byte[]>> drain = FuzzSupport.DrainAsync(editor);
                await editor.WriteAsync(Support.Frame("""{"type":"invoke","id":2,"function":"eco"}"""));
                await FuzzSupport.Capped(started.Task, "la función");
                await editor.WriteAsync(Support.Frame("""{"type":"shutdown"}"""));

                TimeSpan cap = TimeSpan.FromSeconds(10);
                bool returned = await Task.WhenAny(run, Task.Delay(cap)) == run;
                Assert.True(returned, $"Con una función empezada que no atiende la cancelación, RunAsync no volvió en {cap.TotalSeconds:0} s.");
                Assert.True(await run);
                module.Dispose();
                List<byte[]> replies = await FuzzSupport.Capped(drain, "la lectura hasta el fin de flujo");

                Assert.DoesNotContain(replies, r => MessageChannel.Parse(r) is ErrorMessage { Id: 2 } e && e.Code == ErrorCodes.Cancelled);
                Assert.Empty(replies);
            }
            finally
            {
                gate.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Tarea 303, la rama <c>Started</c> con la puerta soltada dentro del tope: la costura
    /// <c>AfterUnansweredClaimedForTests</c> la suelta cuando el cierre ya agotó su espera y ya reclamó lo que no había
    /// empezado. La llamada contesta ella, dentro del tope, y sale exactamente una trama: su resultado, sin <c>cancelled</c>.
    /// </summary>
    [Fact]
    public async Task Una_llamada_empezada_que_termina_dentro_del_tope_contesta_solo_su_resultado()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ModuleHost host = new(Support.EchoInfo) { AfterUnansweredClaimedForTests = () => gate.TrySetResult() };
        host.Register("eco", async (call, _) =>
        {
            started.TrySetResult();
            await gate.Task;
            return new ModuleOutput(call.Parameters);
        });

        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (editor)
        using (module)
        {
            try
            {
                Task<bool> run = host.RunAsync(module);
                await editor.WriteAsync(Support.Frame(Hello));
                await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "el welcome");
                Task<List<byte[]>> drain = FuzzSupport.DrainAsync(editor);
                await editor.WriteAsync(Support.Frame("""{"type":"invoke","id":2,"function":"eco"}"""));
                await FuzzSupport.Capped(started.Task, "la función");
                await editor.WriteAsync(Support.Frame("""{"type":"shutdown"}"""));

                Assert.True(await FuzzSupport.Capped(run, "RunAsync"));
                Assert.True(gate.Task.IsCompleted, "El cierre no llegó a contestar lo pendiente: la llamada terminó antes de agotarse su espera.");
                module.Dispose();
                List<byte[]> replies = await FuzzSupport.Capped(drain, "la lectura hasta el fin de flujo");

                ResultMessage result = Assert.IsType<ResultMessage>(MessageChannel.Parse(Assert.Single(replies)));
                Assert.Equal(2, result.Id);
            }
            finally
            {
                gate.TrySetResult();
            }
        }
    }

    /// <summary><see cref="Unstarted"/> llamadas a «eco» (ids 2 en adelante) y <c>shutdown</c>, en una sola escritura.</summary>
    private static byte[] Burst()
    {
        List<byte> burst = [];
        for (int i = 0; i < Unstarted; i++) burst.AddRange(Support.Frame($$"""{"type":"invoke","id":{{2 + i}},"function":"eco"}"""));
        burst.AddRange(Support.Frame("""{"type":"shutdown"}"""));
        return [.. burst];
    }

    /// <summary>
    /// El flujo del módulo con un editor que deja de leer (tarea 303): tras <see cref="StopReading"/>, una escritura no
    /// avanza hasta que se cancela, como en una tubería llena, y desechar el canal no la desatasca (no cierra la tubería).
    /// </summary>
    private sealed class UnreadStream(Stream inner) : Stream
    {
        private volatile bool _stopped;

        public void StopReading() => _stopped = true;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _stopped ? Task.CompletedTask : inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_stopped) await Task.Delay(Timeout.Infinite, cancellationToken);
            else await inner.WriteAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// El flujo del módulo sin su cierre: <c>MessageChannel.Dispose</c> no cierra la tubería, así que lo que el módulo
    /// escriba después de que <c>RunAsync</c> vuelva todavía llega al editor (tarea 302).
    /// </summary>
    private sealed class KeepOpenStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Sin costura: una llamada que termina al instante y <c>shutdown</c> detrás, en la misma escritura.</summary>
    [Fact]
    public async Task Una_llamada_y_shutdown_seguidos_siempre_tienen_respuesta()
    {
        int lost = 0;
        for (int round = 0; round < 300; round++)
        {
            (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
            using (editor)
            using (module)
            {
                Task<bool> run = Support.EchoHost(maxConcurrent: 64).RunAsync(module);
                await editor.WriteAsync(Support.Frame(Hello));
                await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "el welcome");
                Task<List<byte[]>> drain = FuzzSupport.DrainAsync(editor);
                byte[] burst = [.. Support.Frame("""{"type":"invoke","id":2,"function":"fallar"}"""), .. Support.Frame("""{"type":"shutdown"}""")];
                await editor.WriteAsync(burst);
                Assert.True(await FuzzSupport.Capped(run, "RunAsync"));
                module.Dispose();
                List<byte[]> replies = await FuzzSupport.Capped(drain, "la lectura");
                if (!replies.Any(r => MessageChannel.Parse(r) is ErrorMessage { Id: 2 })) lost++;
            }
        }
        Assert.True(lost == 0, $"{lost} de 300 llamadas quedaron sin respuesta final.");
    }
}
