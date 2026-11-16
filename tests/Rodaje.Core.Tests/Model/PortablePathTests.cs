using Rodaje.Core.Model;

namespace Rodaje.Core.Tests.Model;

/// <summary>Pruebas de reconocimiento, mapeo y resolución de rutas entre plataformas (D-066).</summary>
public sealed class PortablePathTests
{
    [Theory]
    [InlineData("C:a.mp4", StoredPathKind.Relative)]
    [InlineData(@"\Videos\a.mp4", StoredPathKind.Relative)]
    [InlineData(@"\\servidor\recurso\a.mp4", StoredPathKind.WindowsAbsolute)]
    [InlineData(@"//servidor/recurso/a.mp4", StoredPathKind.WindowsAbsolute)]
    [InlineData("/", StoredPathKind.PosixAbsolute)]
    [InlineData("//srv/x", StoredPathKind.WindowsAbsolute)]
    [InlineData("", StoredPathKind.Relative)]
    [InlineData(@"C:\Material\a.mp4", StoredPathKind.WindowsAbsolute)]
    [InlineData("c:/Material/a.mp4", StoredPathKind.WindowsAbsolute)]
    [InlineData(@"D:\", StoredPathKind.WindowsAbsolute)]
    [InlineData("D:/", StoredPathKind.WindowsAbsolute)]
    [InlineData("/home/usuario/video.mp4", StoredPathKind.PosixAbsolute)]
    [InlineData("/Volumes/Material", StoredPathKind.PosixAbsolute)]
    [InlineData("relativo/video.mp4", StoredPathKind.Relative)]
    [InlineData(@"sub\archivo.mp4", StoredPathKind.Relative)]
    [InlineData("video.mp4", StoredPathKind.Relative)]
    [InlineData("C:", StoredPathKind.Relative)]
    public void Classify_identifica_el_tipo_de_ruta_por_su_forma(string path, StoredPathKind expected)
    {
        Assert.Equal(expected, PortablePath.Classify(path));
    }

    [Fact]
    public void ApplyMappings_respeta_el_limite_de_carpeta()
    {
        PathMapping[] mappings = [new(@"D:\Material", "/Volumes/Material")];

        Assert.Equal("/Volumes/Material/a.mp4", PortablePath.ApplyMappings(@"D:\Material\a.mp4", mappings));
        Assert.Equal("/Volumes/Material", PortablePath.ApplyMappings(@"D:\Material", mappings));
        Assert.Equal(@"D:\Materiales\a.mp4", PortablePath.ApplyMappings(@"D:\Materiales\a.mp4", mappings));
    }

    [Fact]
    public void ApplyMappings_en_windows_ignora_mayusculas_y_en_posix_las_distingue()
    {
        PathMapping[] winMappings = [new(@"d:\material", "/Volumes/Material")];
        Assert.Equal("/Volumes/Material/a.mp4", PortablePath.ApplyMappings(@"D:\Material\a.mp4", winMappings));
        Assert.Equal("/Volumes/Material/a.mp4", PortablePath.ApplyMappings("D:/MATERIAL/a.mp4", winMappings));

        PathMapping[] posixMappings = [new("/Volumes/Material", @"D:\Material")];
        Assert.Equal(@"D:\Material\a.mp4", PortablePath.ApplyMappings("/Volumes/Material/a.mp4", posixMappings));
        Assert.Equal("/volumes/material/a.mp4", PortablePath.ApplyMappings("/volumes/material/a.mp4", posixMappings));
    }

    [Fact]
    public void ApplyMappings_cambia_el_separador_en_los_dos_sentidos()
    {
        PathMapping[] toPosix = [new(@"D:\Material", "/Volumes/Material")];
        Assert.Equal("/Volumes/Material/sub/clip.mp4", PortablePath.ApplyMappings(@"D:\Material\sub\clip.mp4", toPosix));

        PathMapping[] toWin = [new("/Volumes/Material", @"D:\Material")];
        Assert.Equal(@"D:\Material\sub\clip.mp4", PortablePath.ApplyMappings("/Volumes/Material/sub/clip.mp4", toWin));
    }

    [Fact]
    public void ApplyMappings_la_primera_fila_gana()
    {
        PathMapping[] mappings =
        [
            new(@"D:\Material", "/Volumes/Primero"),
            new(@"D:\Material", "/Volumes/Segundo"),
        ];

        Assert.Equal("/Volumes/Primero/a.mp4", PortablePath.ApplyMappings(@"D:\Material\a.mp4", mappings));
    }

    [Fact]
    public void ApplyMappings_ignora_from_vacio_y_mantiene_sin_coincidencias()
    {
        PathMapping[] mappings =
        [
            new("", "/Volumes/Ignorado"),
            new(@"D:\Material", "/Volumes/Material"),
        ];

        Assert.Equal(@"E:\Otro\a.mp4", PortablePath.ApplyMappings(@"E:\Otro\a.mp4", mappings));
        Assert.Equal("/Volumes/Material/a.mp4", PortablePath.ApplyMappings(@"D:\Material\a.mp4", mappings));
    }

    [Fact]
    public void Resolve_en_no_windows_resuelve_relativas_con_barra_invertida()
    {
        string result = PortablePath.Resolve(@"sub\toma.mp4", "/proyectos/corto", null, hostIsWindows: false);
        Assert.Equal("/proyectos/corto/sub/toma.mp4", result);
    }

    [Fact]
    public void Resolve_en_no_windows_devuelve_windows_absolute_sin_mapeo_tal_cual()
    {
        string result = PortablePath.Resolve(@"D:\Material\toma.mp4", "/proyectos/corto", null, hostIsWindows: false);
        Assert.Equal(@"D:\Material\toma.mp4", result);
    }

    [Fact]
    public void Resolve_en_no_windows_aplica_mapeo_antes_de_resolver()
    {
        PathMapping[] mappings = [new(@"D:\Material", "/Volumes/Material")];
        string result = PortablePath.Resolve(@"D:\Material\toma.mp4", "/proyectos/corto", mappings, hostIsWindows: false);
        Assert.Equal("/Volumes/Material/toma.mp4", result);
    }

    [Fact]
    public void Resolve_en_windows_resuelve_rutas_relativas()
    {
        string result = PortablePath.Resolve(@"sub\toma.mp4", @"C:\proyectos\corto", null, hostIsWindows: true);
        Assert.Equal(@"C:\proyectos\corto\sub\toma.mp4", result);
    }

    [Fact]
    public void Resolve_en_windows_devuelve_posix_sin_mapeo_sin_romper()
    {
        string result = PortablePath.Resolve("/Volumes/Material/toma.mp4", @"C:\proyectos\corto", null, hostIsWindows: true);
        Assert.False(string.IsNullOrEmpty(result));
        Assert.Contains("Volumes", result, StringComparison.Ordinal);
        Assert.Contains("toma.mp4", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_en_windows_aplica_mapeo_antes_de_resolver()
    {
        PathMapping[] mappings = [new("/Volumes/Material", @"E:\Material")];
        string result = PortablePath.Resolve("/Volumes/Material/toma.mp4", @"C:\proyectos\corto", mappings, hostIsWindows: true);
        Assert.Equal(@"E:\Material\toma.mp4", result);
    }

    [Fact]
    public void MediaRef_ResolvePath_utiliza_mapeo()
    {
        var media = new MediaRef { Path = @"D:\Material\toma.mp4" };
        PathMapping[] mappings = [new(@"D:\Material", @"E:\NuevoMaterial")];

        string direct = media.ResolvePath(@"C:\Proyecto");
        Assert.Equal(@"D:\Material\toma.mp4", direct);

        string mapped = media.ResolvePath(@"C:\Proyecto", mappings);
        Assert.Equal(@"E:\NuevoMaterial\toma.mp4", mapped);
    }

    [Fact]
    public void FindIgnoringCase_devuelve_ruta_real_cuando_encuentra()
    {
        Dictionary<string, string[]> filesystem = new(StringComparer.Ordinal)
        {
            ["/"] = ["media", "usr"],
            ["/media"] = ["Videos", "Musica"],
            ["/media/Videos"] = ["Clip.MP4", "intro.mov"],
        };

        string? found = PortablePath.FindIgnoringCase(
            "/media/videos/clip.mp4",
            path => path == "/media/Videos/Clip.MP4",
            dir => filesystem.TryGetValue(dir, out string[]? entries) ? entries : []);

        Assert.Equal("/media/Videos/Clip.MP4", found);
    }

    [Fact]
    public void FindIgnoringCase_devuelve_null_cuando_no_encuentra()
    {
        Dictionary<string, string[]> filesystem = new(StringComparer.Ordinal)
        {
            ["/"] = ["media"],
            ["/media"] = ["Fotos"],
        };

        string? found = PortablePath.FindIgnoringCase(
            "/media/videos/clip.mp4",
            _ => false,
            dir => filesystem.TryGetValue(dir, out string[]? entries) ? entries : []);

        Assert.Null(found);
    }

    [Fact]
    public void FindIgnoringCase_devuelve_null_cuando_hay_ambiguedad()
    {
        Dictionary<string, string[]> filesystem = new(StringComparer.Ordinal)
        {
            ["/"] = ["media"],
            ["/media"] = ["videos"],
            ["/media/videos"] = ["clip.mp4", "CLIP.MP4"],
        };

        string? found = PortablePath.FindIgnoringCase(
            "/media/videos/clip.mp4",
            _ => false,
            dir => filesystem.TryGetValue(dir, out string[]? entries) ? entries : []);

        Assert.Null(found);
    }

    [Fact]
    public void FindIgnoringCase_devuelve_inmediatamente_si_ya_existe_con_esas_mayusculas()
    {
        bool listed = false;
        string? found = PortablePath.FindIgnoringCase(
            "/media/videos/clip.mp4",
            path => path == "/media/videos/clip.mp4",
            _ => { listed = true; return []; });

        Assert.Equal("/media/videos/clip.mp4", found);
        Assert.False(listed);
    }
}
