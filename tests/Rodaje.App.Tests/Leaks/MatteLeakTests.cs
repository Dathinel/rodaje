using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Tests.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;

namespace Rodaje.App.Tests.Leaks;

/// <summary>
/// Los videos de las máscaras de imagen (Quitar fondo) se leen con un decodificador por archivo que guarda su último
/// cuadro. Antes de este arreglo se quedaban abiertos hasta cerrar la app: al abrir otro proyecto se cierran.
/// </summary>
public sealed class MatteLeakTests : IDisposable
{
    private readonly TempDirectory _folder = new();
    private readonly FakeMattes _mattes = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_matte_opens_on_the_first_request()
    {
        // La espera tras un fallo se calculaba con long.MinValue y se desbordaba: el primer intento ya parecía un fallo reciente.
        var reader = new MatteReader(_mattes.Open);

        Assert.True(reader.TryGet(Touch("mascara.mkv"), 0, out MatteFrame? frame));
        Assert.Equal(4, frame!.Height);
        Assert.Equal(1, _mattes.Opened);
    }

    [Fact]
    public void Clear_closes_every_decoder_and_a_reader_closed_meanwhile_does_not_reopen()
    {
        var reader = new MatteReader(_mattes.Open);
        string[] files = [.. Enumerable.Range(0, 20).Select(i => Touch($"mascara {i}.mkv"))];
        foreach (string file in files)
        {
            Assert.True(reader.TryGet(file, 0.5, out MatteFrame? frame));
            Assert.NotNull(frame);
        }

        Assert.Equal(20, _mattes.Live);
        reader.Clear();

        Assert.Equal(0, _mattes.Live);
        Assert.Equal(0, reader.OpenSources);

        // Después de cerrar, pedirlo de nuevo abre uno nuevo (y solo uno).
        Assert.True(reader.TryGet(files[0], 0.5, out _));
        Assert.Equal(1, _mattes.Live);
    }

    [Fact]
    public void Opening_projects_a_hundred_times_keeps_at_most_the_mattes_of_the_open_one() => UiThread.Run(async () =>
    {
        var content = new EffectContent(new FakeContentLoader(), new MatteReader(_mattes.Open));
        var dialogs = new FakeDialogs { Choice = UnsavedChangesChoice.Discard };
        var session = new EditorSession(new FakePlayback(), new FakeProber(), dialogs, new SequentialIdGenerator());
        AppEffectContent.AttachSession(content, session);

        // Dos proyectos en la misma carpeta, cada uno con sus máscaras: la carpeta no cambia al ir de uno a otro.
        string[] projects = [Path.Combine(_folder.Path, "uno.rodaje"), Path.Combine(_folder.Path, "dos.rodaje")];
        foreach (string project in projects)
        {
            Assert.True(await session.SaveAsAsync(project));
            for (int i = 0; i < 3; i++)
            {
                Touch($"{Path.GetFileNameWithoutExtension(project)}.medios/mascara {i}.mkv");
            }
        }

        int worst = 0;
        for (int cycle = 0; cycle < 100; cycle++)
        {
            string project = projects[cycle % 2];
            Assert.True(await session.OpenProjectAsync(project));
            for (int i = 0; i < 3; i++)
            {
                var key = new ResourceKey($"{LutReference.FilePrefix}{Path.GetFileNameWithoutExtension(project)}.medios/mascara {i}.mkv");
                Assert.True(content.TryGetMatte(key, cycle / 30.0, out _));
            }

            worst = Math.Max(worst, _mattes.Live);
        }

        // Sin cerrar al abrir, quedaban los seis abiertos (los de los dos proyectos) desde la segunda vuelta.
        Assert.Equal(3, worst);
        Assert.Equal(3 * 100, _mattes.Opened);
    });

    private string Touch(string relative)
    {
        string path = Path.Combine(_folder.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    /// <summary>Decodificadores de mentira que cuentan cuántos siguen abiertos.</summary>
    private sealed class FakeMattes
    {
        private int _live;
        private int _opened;

        public int Live => Volatile.Read(ref _live);

        public int Opened => Volatile.Read(ref _opened);

        public IMatteDecoder Open(string path)
        {
            Interlocked.Increment(ref _live);
            Interlocked.Increment(ref _opened);
            return new Decoder(this);
        }

        private sealed class Decoder(FakeMattes owner) : IMatteDecoder
        {
            private int _disposed;

            public Rational FrameRate => Rational.Fps30;

            public int DisplayHeight => 4;

            public BgraImage? Decode(Time sourceTime, int height) => new(4, height, new byte[4 * height * 4]);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref owner._live);
                }
            }
        }
    }
}
