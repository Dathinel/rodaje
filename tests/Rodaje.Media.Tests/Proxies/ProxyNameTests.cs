using Rodaje.Media.Proxies;
using Rodaje.Media.Tests.Support;

namespace Rodaje.Media.Tests.Proxies;

/// <summary>
/// El nombre del proxy (tarea 227): válido en Windows sea cual sea el nombre del original (acentos, emoji, NFC o NFD,
/// nombres reservados, rutas largas) y distinto para dos originales distintos aunque tengan el mismo contenido.
/// </summary>
public sealed class ProxyNameTests : IDisposable
{
    private static readonly DateTime SameDate = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "LPT1", "CONIN$", "CONOUT$"];
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Dos_originales_con_el_mismo_contenido_y_la_misma_fecha_no_comparten_proxy()
    {
        var random = new Random(227);
        string longFolder = Path.Combine([.. Enumerable.Range(1, 4).Select(i => $"{i} " + string.Concat(Enumerable.Repeat("carpeta larga ñ ", 5)))]);
        string[] names =
        [
            "Vídeo.mp4", "Vi\u0301deo.mp4", "toma 🎬.mp4", "𝄞 música.mov", "  espacios  .mp4", "a.b.c.mp4",
            "aux.mp4.mov", "con-guion.mp4", Path.Combine(longFolder, "toma 🎬.mp4"), Path.Combine("otra", "toma 🎬.mp4"),
        ];
        byte[] content = new byte[3 << 20];
        random.NextBytes(content);
        var store = new ProxyStore(_temp.Combine("proxies"));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            string source = _temp.Combine(name);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllBytes(source, content);
            File.SetLastWriteTimeUtc(source, SameDate);

            string proxy = store.ProxyPath(SourceFingerprint.Compute(source), ProxySettings.Default);
            string file = Path.GetFileName(proxy);

            Assert.True(paths.Add(proxy), $"«{name}» comparte el proxy {file} con otro original.");
            Assert.Equal(store.Directory, Path.GetDirectoryName(proxy));
            Assert.True(file.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'), file);
            Assert.True(file.Length < 100, file);
            Assert.DoesNotContain(Reserved, r => file.StartsWith(r + ".", StringComparison.OrdinalIgnoreCase));
        }
    }
}
