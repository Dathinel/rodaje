using System.Text;
using FotoToma.Core.Model;
using FotoToma.Core.Serialization;

namespace FotoToma.Core.Tests.Serialization;

/// <summary>Guardado y apertura en disco, en una carpeta con espacios, tildes y eñe.</summary>
public sealed class ProjectFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FotoToma pruebas", $"Prueba con espacios ñ {Guid.NewGuid():N}");

    public ProjectFileTests() => Directory.CreateDirectory(_dir);

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
    }

    private string PathFor(string name = "Mi edición final") => Path.Combine(_dir, name + ProjectSerializer.FileExtension);

    [Fact]
    public void FileExtension_IsFtoma() => Assert.Equal(".ftoma", ProjectSerializer.FileExtension);

    [Fact]
    public void Save_NewFile_WritesUtf8WithoutBomAndNoLeftovers()
    {
        string path = PathFor();
        Project project = SerializationSamples.Rich();

        ProjectSerializer.Save(project, path);

        byte[] bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal(ProjectSerializer.Serialize(project) + "\n", Encoding.UTF8.GetString(bytes));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Save_Overwrite_KeepsPreviousVersionAsBak()
    {
        string path = PathFor();
        Project first = SerializationSamples.Rich();
        ProjectSerializer.Save(first, path);
        string firstText = File.ReadAllText(path);

        Project second = SerializationSamples.Rich();
        second.Name = "Segunda versión";
        ProjectSerializer.Save(second, path);

        Assert.Equal(firstText, File.ReadAllText(path + ".bak"));
        Assert.Equal("Segunda versión", ProjectSerializer.Load(path).Name);
        Assert.Equal("Vacaciones en Cartagena — edición ñ", ProjectSerializer.Load(path + ".bak").Name);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(
            [Path.GetFileName(path), Path.GetFileName(path) + ".bak"],
            Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Save_OverwritesStaleTempFromInterruptedSave()
    {
        string path = PathFor();
        File.WriteAllText(path + ".tmp", "basura de un guardado interrumpido");

        ProjectSerializer.Save(SerializationSamples.Rich(), path);

        Assert.False(File.Exists(path + ".tmp"));
        SerializationSamples.AssertDeepEqual(SerializationSamples.Rich(), ProjectSerializer.Load(path));
    }

    [Fact]
    public void Save_RelativePath_IsResolvedAndWorks()
    {
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, PathFor("relativo"));
        ProjectSerializer.Save(SerializationSamples.Rich(), relative);
        Assert.True(File.Exists(PathFor("relativo")));
    }

    [Fact]
    public void Save_Failure_RemovesTempAndRethrows()
    {
        // El destino es una carpeta: el temporal se escribe, pero no se puede mover sobre ella.
        string path = Path.Combine(_dir, "carpeta.ftoma");
        Directory.CreateDirectory(path);

        Assert.ThrowsAny<IOException>(() => ProjectSerializer.Save(SerializationSamples.Rich(), path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_Failure_WhenTempCannotBeDeleted_StillRethrowsOriginalError()
    {
        // El temporal es una carpeta no vacía: no se puede crear ni borrar como archivo.
        string path = PathFor("bloqueado");
        Directory.CreateDirectory(path + ".tmp");
        File.WriteAllText(Path.Combine(path + ".tmp", "dentro.txt"), "x");

        Assert.ThrowsAny<UnauthorizedAccessException>(() => ProjectSerializer.Save(SerializationSamples.Rich(), path));
        Assert.True(Directory.Exists(path + ".tmp"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Save_TempLockedByAnotherProgram_ThrowsAndKeepsExistingFile()
    {
        string path = PathFor("en uso");
        ProjectSerializer.Save(SerializationSamples.Rich(), path);
        string before = File.ReadAllText(path);

        using (new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => ProjectSerializer.Save(new Project { Id = "otro" }, path));
        }

        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Load_PathWithSpacesAndAccents_RoundTrips()
    {
        string path = PathFor("Canción del año ñandú");
        ProjectSerializer.Save(SerializationSamples.Rich(), path);

        Project loaded = ProjectSerializer.Load(path);

        SerializationSamples.AssertDeepEqual(SerializationSamples.Rich(), loaded);
    }

    [Fact]
    public void Load_AcceptsUtf8Bom()
    {
        string path = PathFor("con bom");
        File.WriteAllText(path, ProjectSerializer.Serialize(SerializationSamples.Rich()), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal("project-1", ProjectSerializer.Load(path).Id);
    }

    [Fact]
    public void Load_CorruptFile_NamesTheFileAndKeepsProblems()
    {
        string path = PathFor("dañado");
        File.WriteAllText(path, ProjectSerializer.Serialize(SerializationSamples.Rich())[..200]);

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(path));

        Assert.StartsWith("No se pudo abrir «dañado.ftoma».", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no es JSON bien formado", ex.Message, StringComparison.Ordinal);
        Assert.IsType<ProjectFormatException>(ex.InnerException);
    }

    [Fact]
    public void Load_InvalidProject_KeepsProblemList()
    {
        Project project = SerializationSamples.Rich();
        project.Media.Clear();
        string path = PathFor("sin medios");
        ProjectSerializer.Save(project, path);

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(path));

        Assert.Equal(4, ex.Problems.Count);
    }

    [Fact]
    public void Load_NotUtf8_ThrowsFormatException()
    {
        string path = PathFor("binario");
        File.WriteAllBytes(path, [0x7B, 0xC3, 0x28, 0xFF, 0x7D]);

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(path));

        Assert.Contains("no es texto UTF-8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => ProjectSerializer.Load(PathFor("no existe")));
    }

    [Fact]
    public void Arguments_AreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectSerializer.Save(null!, PathFor()));
        Assert.Throws<ArgumentNullException>(() => ProjectSerializer.Save(new Project(), null!));
        Assert.Throws<ArgumentException>(() => ProjectSerializer.Save(new Project(), " "));
        Assert.Throws<ArgumentException>(() => ProjectSerializer.Load(""));
    }
}
