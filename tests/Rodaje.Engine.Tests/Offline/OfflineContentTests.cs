using System.Collections.Concurrent;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Tests.Offline;

public sealed class OfflineContentTests
{
    private static readonly ResourceKey Look = new("rodaje-filtros@1.0.0/look-calido");

    // Tope anticuelgue: la carga nueva termina en un hilo propio en milisegundos.
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public void Una_LUT_que_se_recarga_por_Invalidate_durante_la_espera_no_cuenta_como_que_falta()
    {
        var content = new ReloadingContent();
        content.BeforeWhenLoaded = () =>
        {
            content.BeforeWhenLoaded = null;
            content.Invalidate();
        };

        Assert.True(OfflineContent.WaitForLut(content, Look, Generous, CancellationToken.None));
        Assert.Equal(2, content.Loads);
    }

    [Fact]
    public void Una_LUT_que_no_se_puede_leer_cuenta_como_que_falta_sin_esperar_al_tope()
    {
        var content = new ReloadingContent { Fails = true };

        Assert.False(OfflineContent.WaitForLut(content, Look, Generous, CancellationToken.None));
        Assert.Equal(1, content.Loads);
    }

    /// <summary>
    /// Un aviso de carga que llega en el hueco antes de esperar <c>Changed</c> no se pierde: 1, entre la primera pregunta y
    /// la suscripción (lo recoge la comprobación del bucle, ya suscrito); 2, entre la comprobación del bucle y el
    /// <c>Wait</c> (lo guarda el semáforo). La espera sondea cada 50 ms y vuelve a mirar al acabar el tope, así que un
    /// aviso perdido no da <c>false</c>: solo deja el hilo bloqueado hasta el sondeo. Por eso otro hilo vigila si el que
    /// espera pasa por <see cref="ThreadState.WaitSleepJoin"/> mientras dura el hueco (desde el aviso hasta la pregunta
    /// siguiente). En el caso correcto no se bloquea nunca, sin depender de tiempos; el vigilante solo hace falta para ver
    /// el fallo (si no llegara a verlo, el fallo pasaría, nunca al revés).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Un_aviso_de_carga_en_el_hueco_antes_de_la_espera_no_se_pierde(int noticeAt)
    {
        var content = new FakeEffectContent();
        Thread waiter = Thread.CurrentThread;
        int notReady = 0;
        int phase = 0; // 0: antes del aviso; 1: en el hueco; 2: después.
        bool blocked = false;
        using var watching = new ManualResetEventSlim();
        var watcher = new Thread(() =>
        {
            watching.Set();
            while (Volatile.Read(ref phase) < 2)
            {
                // Las dos lecturas de la fase rodean la del estado: así se vio dentro del hueco y no después.
                if (Volatile.Read(ref phase) == 1 && (waiter.ThreadState & ThreadState.WaitSleepJoin) != 0 && Volatile.Read(ref phase) == 1)
                {
                    Volatile.Write(ref blocked, true);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Vigilante del hueco",
        };
        watcher.Start();
        Assert.True(watching.Wait(Generous));
        content.LutAsked = _ =>
        {
            if (Volatile.Read(ref phase) == 1)
            {
                Volatile.Write(ref phase, 2);
            }
        };
        content.LutNotReady = key =>
        {
            if (++notReady == noticeAt)
            {
                content.FinishLoading(key.Reference!);
                Volatile.Write(ref phase, 1);
            }
        };

        bool loaded;
        try
        {
            loaded = OfflineContent.WaitForLut(content, Look, Generous, CancellationToken.None);
        }
        finally
        {
            Volatile.Write(ref phase, 2);
        }

        Assert.True(watcher.Join(Generous));
        Assert.True(loaded);
        Assert.False(Volatile.Read(ref blocked), "La espera se bloqueó con el aviso de carga ya dado: lo perdió y la sacó el sondeo.");
        Assert.Equal(noticeAt + 1, content.LutRequests);
    }

    /// <summary>
    /// Como <c>EffectContent</c>: una entrada por referencia que se carga en otro hilo, y <see cref="Invalidate"/> las descarta;
    /// <see cref="BeforeWhenLoaded"/> deja meter el <see cref="Invalidate"/> justo entre la primera TryGetLut y la espera.
    /// </summary>
    private sealed class ReloadingContent : IAwaitableEffectContent
    {
        private readonly ConcurrentDictionary<string, Entry> _luts = new(StringComparer.Ordinal);
        private int _loads;

        public event EventHandler? Changed;

        public Action? BeforeWhenLoaded { get; set; }

        public bool Fails { get; init; }

        public int Loads => Volatile.Read(ref _loads);

        public bool TryGetLut(ResourceKey key, out LutData? lut)
        {
            if (_luts.TryGetValue(key.Reference, out Entry? entry))
            {
                lut = entry.Lut;
                return lut is not null;
            }

            lut = null;
            var created = new Entry();
            if (_luts.TryAdd(key.Reference, created))
            {
                Interlocked.Increment(ref _loads);
                new Thread(() =>
                {
                    if (!Fails)
                    {
                        created.Lut = new LutData(2, new float[2 * 2 * 2 * 3]);
                    }

                    created.Loaded.TrySetResult();
                    Changed?.Invoke(this, EventArgs.Empty);
                })
                { IsBackground = true }.Start();
            }

            return false;
        }

        public TransitionCurves? FindTransition(string reference) => null;

        public Task WhenLutLoadedAsync(ResourceKey key)
        {
            BeforeWhenLoaded?.Invoke();
            return _luts.TryGetValue(key.Reference, out Entry? entry) ? entry.Loaded.Task : Task.CompletedTask;
        }

        public void Invalidate()
        {
            _luts.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private sealed class Entry
        {
            private volatile LutData? _lut;

            public LutData? Lut
            {
                get => _lut;
                set => _lut = value;
            }

            public TaskCompletionSource Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
