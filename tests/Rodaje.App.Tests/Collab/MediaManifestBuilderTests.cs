using Rodaje.App.Services.Collab;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Collab;

/// <summary>El manifiesto de medios que el anfitrión manda con la copia inicial (revisión, hallazgo 8).</summary>
public sealed class MediaManifestBuilderTests
{
    private static string Hash { get; } = new string('b', 64);

    [Fact]
    public async Task Un_medio_sin_ruta_o_sin_archivo_no_impide_armar_el_manifiesto()
    {
        // Un medio remoto llega sin ruta en este equipo: antes, «new FileInfo(\"\")» lanzaba y abortaba toda la copia.
        string folder = CollabHarness.TempFolder();
        string ok = Path.Combine(folder, "bueno.bin");
        string broken = Path.Combine(folder, "roto.bin");
        await File.WriteAllBytesAsync(ok, new byte[8]);
        await File.WriteAllBytesAsync(broken, new byte[4]);

        MediaRef[] media =
        [
            new() { Id = "m-sin-ruta", Path = "", Name = "aun-no-esta.mp4" },
            new() { Id = "m-roto", Path = broken, Name = "roto.bin" },
            new() { Id = "m-bueno", Path = ok, Name = "bueno.bin" },
        ];

        var builder = new MediaManifestBuilder((path, ct) =>
            path.EndsWith("roto.bin", StringComparison.Ordinal)
                ? Task.FromException<string?>(new IOException("sin acceso"))
                : Task.FromResult<string?>(Hash));

        IReadOnlyList<MediaManifestEntry> manifest = await builder.BuildAsync(media, m => m.Path);

        Assert.Single(manifest);
        Assert.Equal("m-bueno", manifest[0].MediaId);
        Assert.Equal("bueno.bin", manifest[0].Name);
    }
}
