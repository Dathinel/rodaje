using System.Collections.Concurrent;
using Rodaje.Engine.Offline;
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
