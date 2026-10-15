using System.Text;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Serialization;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete «Guardado seguro y copia <c>.bak</c>» de <c>docs/formato-proyecto.md</c>. Cada prueba cita la frase.
/// </summary>
public sealed class ProjectSafeSaveContractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Rodaje pruebas", $"Guardado seguro ñ {Guid.NewGuid():N}");

    public ProjectSafeSaveContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Una carpeta temporal sobrante no afecta a otras pruebas.
        }
        catch (UnauthorizedAccessException)
        {
            // Igual: el antivirus puede tener abierto un archivo recién escrito.
        }
    }

    private string PathFor(string name = "Mi proyecto") => Path.Combine(_dir, name + ProjectSerializer.FileExtension);

    private static string Expected(Project project) => ProjectSerializer.Serialize(project) + "\n";

    private static Project Named(string name)
    {
        Project project = SerializationSamples.Rich();
        project.Name = name;
        return project;
    }

    /// <summary>Anota lo que hay en disco en el momento de cambiar el temporal por el proyecto.</summary>
    private sealed class Snapshot : ProjectSerializer.SaveFileSystem
    {
        public List<string> Calls { get; } = [];

        public string? TempAtSwap { get; private set; }

        public string? TargetAtSwap { get; private set; }

        public string? Source { get; private set; }

        public string? Backup { get; private set; }

        public override void Replace(string source, string destination, string backup)
        {
            Record("Replace", source, destination);
            Backup = backup;
            base.Replace(source, destination, backup);
        }

        public override void Move(string source, string destination)
        {
            Record("Move", source, destination);
            base.Move(source, destination);
        }

        private void Record(string call, string source, string destination)
        {
            Calls.Add(call);
            Source = source;
            TempAtSwap = File.ReadAllText(source);
            TargetAtSwap = File.Exists(destination) ? File.ReadAllText(destination) : null;
        }
    }

    // «Por eso Rodaje nunca escribe directamente sobre el proyecto. Primero escribe el contenido completo en un archivo
    // temporal con el mismo nombre y la terminación `.tmp` (`Mi proyecto.rodaje.tmp`), en la misma carpeta […] Solo
    // entonces lo cambia por el proyecto.»
    [Fact]
    public void Escribe_el_temporal_completo_junto_al_proyecto_antes_de_tocarlo()
    {
        string path = PathFor();
        Project first = Named("Primera");
        ProjectSerializer.Save(first, path);
        Project second = Named("Segunda");
        var fs = new Snapshot();

        ProjectSerializer.Save(second, path, fs);

        Assert.Equal(Path.Combine(_dir, "Mi proyecto.rodaje.tmp"), fs.Source);
        Assert.Equal(Expected(second), fs.TempAtSwap);
        Assert.Equal(Expected(first), fs.TargetAtSwap);
        Assert.Equal(Expected(second), File.ReadAllText(path));
    }

    // «Si el proyecto ya existía, el cambio se hace con una operación de reemplazo de Windows que deja la versión
    // anterior como `Mi proyecto.rodaje.bak`»
    [Fact]
    public void Si_ya_existia_se_reemplaza_y_la_version_anterior_queda_en_bak()
    {
        string path = PathFor();
        Project first = Named("Primera");
        ProjectSerializer.Save(first, path);
        var fs = new Snapshot();

        ProjectSerializer.Save(Named("Segunda"), path, fs);

        Assert.Equal(["Replace"], fs.Calls);
        Assert.Equal(Path.Combine(_dir, "Mi proyecto.rodaje.bak"), fs.Backup);
        Assert.Equal(Expected(first), File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    // «la copia `.bak` contiene el guardado anterior» (en cada guardado, no solo en el segundo).
    [Fact]
    public void Cada_guardado_deja_en_bak_el_inmediatamente_anterior()
    {
        string path = PathFor();
        ProjectSerializer.Save(Named("Uno"), path);
        ProjectSerializer.Save(Named("Dos"), path);

        ProjectSerializer.Save(Named("Tres"), path);

        Assert.Equal("Dos", ProjectSerializer.Load(path + ".bak").Name);
        Assert.Equal("Tres", ProjectSerializer.Load(path).Name);
    }

    // «si no existía, el temporal simplemente se renombra.»
    [Fact]
    public void Si_no_existia_el_temporal_solo_se_renombra()
    {
        string path = PathFor();
        File.WriteAllText(path + ".bak", "copia de otro día");
        Project project = Named("Nuevo");
        var fs = new Snapshot();

        ProjectSerializer.Save(project, path, fs);

        Assert.Equal(["Move"], fs.Calls);
        Assert.Null(fs.TargetAtSwap);
        Assert.Equal(Expected(project), File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal("copia de otro día", File.ReadAllText(path + ".bak"));
    }

    // «Se corte cuando se corte, en disco queda la versión anterior completa o la nueva completa, nunca una mezcla.»
    [Fact]
    public void Si_el_cambio_se_corta_queda_la_version_anterior_completa()
    {
        string path = PathFor();
        Project first = Named("Primera");
        ProjectSerializer.Save(first, path);
        var fs = new CutBeforeSwap();

        Assert.Throws<IOException>(() => ProjectSerializer.Save(Named("Segunda"), path, fs));

        Assert.Equal(Expected(first), File.ReadAllText(path));
    }

    // «Se corte cuando se corte, en disco queda la versión anterior completa o la nueva completa, nunca una mezcla.»
    // Corte justo después de que el reemplazo movió el original a .bak: la nueva queda completa en el proyecto.
    [Fact]
    public void Si_el_reemplazo_se_corta_tras_mover_el_original_queda_la_nueva_completa()
    {
        string path = PathFor();
        Project first = Named("Primera");
        ProjectSerializer.Save(first, path);
        Project second = Named("Segunda");

        ProjectSerializer.Save(second, path, new CutAfterBackup());

        Assert.Equal(Expected(second), File.ReadAllText(path));
        Assert.Equal(Expected(first), File.ReadAllText(path + ".bak"));
    }

    // «basta con hacer una copia de ella, quitarle la terminación `.bak` y abrirla.»
    [Fact]
    public void Una_copia_del_bak_sin_la_terminacion_abre_el_guardado_anterior()
    {
        string path = PathFor();
        Project first = SerializationSamples.Rich();
        ProjectSerializer.Save(first, path);
        ProjectSerializer.Save(Named("Segunda"), path);
        string copy = PathFor("Recuperado");

        File.Copy(path + ".bak", copy);

        SerializationSamples.AssertDeepEqual(first, ProjectSerializer.Load(copy));
    }

    // «Un `.tmp` que quede en la carpeta es el resto de un guardado interrumpido; no hace falta borrarlo, porque el
    // siguiente guardado lo reemplaza.»
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Un_tmp_sobrante_lo_reemplaza_el_siguiente_guardado(bool projectExists)
    {
        string path = PathFor();
        Project first = Named("Primera");
        if (projectExists)
        {
            ProjectSerializer.Save(first, path);
        }

        // Más largo que el proyecto: si no se truncara, quedaría cola del resto anterior.
        File.WriteAllText(path + ".tmp", new string('x', Encoding.UTF8.GetByteCount(Expected(first)) * 3));
        Project second = Named("Segunda");

        ProjectSerializer.Save(second, path);

        Assert.Equal(Expected(second), File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        if (projectExists)
        {
            Assert.Equal(Expected(first), File.ReadAllText(path + ".bak"));
        }
    }

    /// <summary>El cambio falla antes de tocar nada.</summary>
    private sealed class CutBeforeSwap : ProjectSerializer.SaveFileSystem
    {
        public override void Replace(string source, string destination, string backup) =>
            throw new IOException("Corte simulado.");
    }

    /// <summary>Como ERROR_UNABLE_TO_MOVE_REPLACEMENT_2: el original ya está en .bak y el nuevo no llegó.</summary>
    private sealed class CutAfterBackup : ProjectSerializer.SaveFileSystem
    {
        public override void Replace(string source, string destination, string backup)
        {
            File.Move(destination, backup, overwrite: true);
            throw new IOException("Corte simulado.");
        }
    }
}
