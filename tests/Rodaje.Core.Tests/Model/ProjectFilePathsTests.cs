using System.Text;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Hardened;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Model;

/// <summary>
/// Rutas que el editor guarda y vuelve a resolver (tarea 227): nombres con acentos, emoji, pares sustitutos, NFC frente a
/// NFD, espacios y puntos, rutas de más de 260 caracteres y <c>..</c>. Ida y vuelta: guardar, mover la carpeta del
/// proyecto y abrir encuentra los medios y los archivos sueltos (<c>file:</c>) de las LUT y las máscaras de imagen.
/// </summary>
public sealed class ProjectFilePathsTests : IDisposable
{
    private const int Seed = 227;

    // Trozos de nombre válidos en Windows y en Linux. «Vídeo» va en NFC y en NFD: son nombres distintos en NTFS y en ext4.
    private static readonly string[] Parts =
    [
        "toma ñandú", "Vídeo", "Vi\u0301deo", "🎬 rodaje", "𝄞 música", "  espacios  dentro", "a.b.c", "Ελληνικά", "日本語",
        "dos  espacios", "punto.final.mp4", "#100% [b] {c} ~d", "'comillas' & más", "con\u00A0nbsp",
    ];

    // Solo fuera de Windows: ahí no se pueden crear (nombres reservados, punto o espacio al final del nombre).
    private static readonly string[] UnixOnlyParts = ["CON", "aux", "nombre.", "nombre ", "LPT1"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rodaje-rutas-" + Guid.NewGuid().ToString("N"));

    public ProjectFilePathsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Guardar_mover_la_carpeta_y_abrir_encuentra_los_medios_y_los_archivos_sueltos()
    {
        var random = new Random(Seed);
        string projectA = Path.Combine(_root, "Proyecto Vídeo 🎬");
        string outside = Path.Combine(_root, "fuera común");
        var expected = new List<(string Id, string Relative, bool Inside)>();
        Project project = ModelSamples.ValidProject();
        project.Media.Clear();

        for (int i = 0; i < 24; i++)
        {
            string folder = random.Next(4) switch
            {
                0 => "",
                1 => "medios",
                2 => Path.Combine("medios", Name(random), Name(random)),
                _ => LongFolder(random),
            };
            bool inside = random.Next(5) != 0;
            string file = $"{Name(random)} {i}.mp4";
            string relative = Path.Combine(folder, file);
            string full = Path.Combine(inside ? projectA : outside, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, [1, 2, 3]);
            string id = "m" + i;
            project.Media.Add(new MediaRef { Id = id, Path = full, Name = file, Duration = ModelSamples.Sec(60), HasVideo = true, HasAudio = true, FrameRate = Rational.Fps30, Width = 1920, Height = 1080, SampleRate = 48000, AudioChannels = 2 });
            expected.Add((id, relative, inside));
        }

        Assert.Contains(expected, e => Path.Combine(projectA, e.Relative).Length > 260);
        foreach (Clip clip in project.Sequences.SelectMany(s => s.VideoTracks.Concat(s.AudioTracks)).SelectMany(t => t.Clips))
        {
            clip.MediaId = "m0";
        }

        // LUT y máscara elegidas con el proyecto sin guardar: en memoria van absolutas, como las deja LutReference.ForFile.
        string lut = Path.Combine(projectA, "luts", $"{Name(random)}.cube");
        string matte = Path.Combine(projectA, "máscaras", $"{Name(random)} 🎭.mp4");
        foreach (string file in (string[])[lut, matte])
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "x");
        }

        Clip c2 = project.Sequences[0].VideoTracks[0].Clips[1];
        c2.Effects.Add(new EffectInstance { Id = "fx-lut", Type = BuiltInEffects.Color, Options = { [BuiltInEffects.ColorOptions.Lut] = FileRef(lut) } });
        new UndoStack(project).Execute(Edits.SetImageMask("s1", "c3", FileRef(matte), Time.Zero));

        string saved = Path.Combine(projectA, "corto ñ.rodaje");
        ProjectSerializer.Save(ProjectFilePaths.ForSaving(project, projectA), saved);
        string json = File.ReadAllText(saved, Encoding.UTF8);
        Assert.DoesNotContain(_root.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.Ordinal);

        // Se mueve la carpeta entera (con los medios de fuera al mismo nivel, que siguen en su sitio).
        string projectB = Path.Combine(_root, "Movido Vi\u0301deo 𝄞");
        Directory.Move(projectA, projectB);
        Project opened = ProjectSerializer.Load(Path.Combine(projectB, "corto ñ.rodaje"));
        ProjectFilePaths.MakeAbsolute(opened, projectB);

        foreach ((string id, string relative, bool inside) in expected)
        {
            string path = opened.Media.Single(m => m.Id == id).Path;
            Assert.Equal(Path.Combine(inside ? projectB : outside, relative), path);
            Assert.True(File.Exists(path), path);
        }

        string movedLut = Path.Combine(projectB, "luts", Path.GetFileName(lut));
        string movedMatte = Path.Combine(projectB, "máscaras", Path.GetFileName(matte));
        Sequence sequence = opened.Sequences[0];
        string lutRef = sequence.VideoTracks[0].Clips[1].Effects.Single(e => e.Id == "fx-lut").Options[BuiltInEffects.ColorOptions.Lut];
        string matteRef = sequence.VideoTracks[1].Clips[0].Effects.Single(e => e.Type == BuiltInEffects.Mask).Options[BuiltInEffects.MaskOptions.Matte];
        Assert.Equal("file:luts/" + Path.GetFileName(lut), lutRef);
        Assert.Equal("file:máscaras/" + Path.GetFileName(matte), matteRef);
        Assert.Equal(movedLut, Resolve(lutRef, projectB));
        Assert.Equal(movedMatte, Resolve(matteRef, projectB));
        Assert.True(File.Exists(movedLut));
        Assert.True(File.Exists(movedMatte));
    }

    [Fact]
    public void Las_rutas_se_guardan_relativas_sin_normalizar_el_texto()
    {
        var random = new Random(Seed + 1);
        string projectDirectory = Path.Combine(_root, "Proyecto");
        for (int i = 0; i < 200; i++)
        {
            string relative = Path.Combine(Name(random), Name(random) + ".mov");
            var project = new Project { Media = [new MediaRef { Id = "m", Path = Path.Combine(projectDirectory, relative) }] };

            Project copy = ProjectFilePaths.ForSaving(project, projectDirectory);

            // NFC y NFD, sustitutos y espacios llegan al archivo tal cual: sin normalizar, NTFS no encontraría el medio.
            Assert.Equal(relative, copy.Media[0].Path);
            Assert.Equal(project.Media[0].Path, copy.Media[0].ResolvePath(projectDirectory));
            Assert.Equal(Path.Combine(projectDirectory, relative), project.Media[0].Path);
        }
    }

    [Fact]
    public void NFC_y_NFD_son_carpetas_distintas_y_la_ruta_relativa_sube_hasta_divergir()
    {
        string nfc = Path.Combine(_root, "Vídeos", "Proyecto");
        string nfd = Path.Combine(_root, "Vi\u0301deos", "Proyecto", "toma.mp4");
        var project = new Project { Media = [new MediaRef { Id = "m", Path = nfd }] };

        Project copy = ProjectFilePaths.ForSaving(project, nfc);

        Assert.Equal(Path.Combine("..", "..", "Vi\u0301deos", "Proyecto", "toma.mp4"), copy.Media[0].Path);
        Assert.Equal(nfd, copy.Media[0].ResolvePath(nfc));
    }

    [Fact]
    public void Guardar_en_otra_carpeta_recalcula_las_referencias_relativas_que_hay_en_memoria()
    {
        // Elegida con el proyecto ya guardado en A: LutReference.ForFile la deja relativa a A.
        string folderA = Path.Combine(_root, "A");
        string folderB = Path.Combine(_root, "B", "dentro");
        Project project = WithLut("file:luts/mi look.cube");

        Project copy = ProjectFilePaths.ForSaving(project, folderB, currentDirectory: folderA);

        Assert.Equal("file:../../A/luts/mi look.cube", Lut(copy));
        Assert.Equal("file:luts/mi look.cube", Lut(project));

        // Sin la carpeta de antes no se sabe a qué es relativa: queda como estaba.
        Assert.Equal("file:luts/mi look.cube", Lut(ProjectFilePaths.ForSaving(project, folderB)));
    }

    [Fact]
    public void Abrir_deja_las_referencias_como_vienen_y_guardar_en_otra_carpeta_las_recalcula()
    {
        // Guardar y abrir tiene que devolver el mismo proyecto en memoria (lo comprueba el fuzz de la sesión).
        string folderA = Path.Combine(_root, "A");
        string folderB = Path.Combine(_root, "B");
        Project project = WithLut("file:luts/mi look.cube");

        ProjectFilePaths.MakeAbsolute(project, folderA);

        Assert.Equal("file:luts/mi look.cube", Lut(project));
        Assert.Equal("file:luts/mi look.cube", Lut(ProjectFilePaths.ForSaving(project, folderA, folderA)));
        Assert.Equal("file:../A/luts/mi look.cube", Lut(ProjectFilePaths.ForSaving(project, folderB, folderA)));
    }

    [Fact]
    public void La_imagen_del_barrido_por_luminancia_tambien_se_vuelve_relativa()
    {
        string folder = Path.Combine(_root, "P");
        string image = Path.Combine(folder, "barridos", "estrella ✨.png");
        Project project = WithLut("rodaje-filtros@1.0.0/calido");
        project.Sequences[0].VideoTracks[0].Transitions.Add(new TransitionInstance
        {
            Id = "t1",
            Type = BuiltInTransitions.Luma,
            Options = { [BuiltInTransitions.LumaOptions.Image] = FileRef(image) },
        });

        Project copy = ProjectFilePaths.ForSaving(project, folder);

        Assert.Equal("file:barridos/estrella ✨.png", copy.Sequences[0].VideoTracks[0].Transitions[0].Options[BuiltInTransitions.LumaOptions.Image]);
        Assert.Equal("rodaje-filtros@1.0.0/calido", Lut(copy));
    }

    [Fact]
    public void Solo_se_tocan_las_opciones_que_admiten_un_archivo()
    {
        string folder = Path.Combine(_root, "P");
        Project project = WithLut(FileRef(Path.Combine(folder, "a.cube")));
        EffectInstance color = project.Sequences[0].VideoTracks[0].Clips[0].Effects[0];
        color.Options["otra"] = FileRef(Path.Combine(folder, "b.cube"));
        project.Sequences[0].VideoTracks[0].Clips[0].Effects.Add(new EffectInstance
        {
            Id = "otro",
            Type = "complemento.desconocido",
            Options = { ["lut"] = FileRef(Path.Combine(folder, "c.cube")) },
        });

        Project copy = ProjectFilePaths.ForSaving(project, folder);

        Assert.Equal("file:a.cube", Lut(copy));
        Assert.Equal(FileRef(Path.Combine(folder, "b.cube")), copy.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Options["otra"]);
        Assert.Equal(FileRef(Path.Combine(folder, "c.cube")), copy.Sequences[0].VideoTracks[0].Clips[0].Effects.Single(e => e.Id == "otro").Options["lut"]);
    }

    [WindowsOnlyFact("las unidades, las rutas UNC y el «\\» como separador son de Windows.")]
    public void En_Windows_otra_unidad_y_UNC_quedan_absolutas_y_lo_demas_relativo()
    {
        const string ProjectDirectory = @"Q:\Proyectos\Uno";
        string longName = string.Concat(Enumerable.Repeat("carpeta larga ñ ", 20));
        var project = WithLut("file:R:/LUTs/cine.cube");
        project.Media =
        [
            new MediaRef { Id = "otra-unidad", Path = @"R:\Videos\a.mp4" },
            new MediaRef { Id = "unc", Path = @"\\servidor\medios\c.mp4" },
            new MediaRef { Id = "mayusculas", Path = @"q:\PROYECTOS\uno\Clip.mp4" },
            new MediaRef { Id = "hermana", Path = @"Q:\Proyectos\Dos\b.mp4" },
            new MediaRef { Id = "larga", Path = $@"Q:\Proyectos\Uno\{longName}\{longName}\toma 🎬.mp4" },
        ];

        Project copy = ProjectFilePaths.ForSaving(project, ProjectDirectory);

        Assert.Equal(@"R:\Videos\a.mp4", copy.Media[0].Path);
        Assert.Equal(@"\\servidor\medios\c.mp4", copy.Media[1].Path);
        Assert.Equal("Clip.mp4", copy.Media[2].Path);
        Assert.Equal(@"..\Dos\b.mp4", copy.Media[3].Path);
        Assert.Equal($@"{longName}\{longName}\toma 🎬.mp4", copy.Media[4].Path);
        Assert.Equal("file:R:/LUTs/cine.cube", Lut(copy));

        // La carpeta se mueve a otra unidad: lo relativo la sigue, lo absoluto no cambia.
        ProjectFilePaths.MakeAbsolute(copy, @"Z:\Otra\Uno");
        Assert.Equal(@"R:\Videos\a.mp4", copy.Media[0].Path);
        Assert.Equal(@"\\servidor\medios\c.mp4", copy.Media[1].Path);
        Assert.Equal(@"Z:\Otra\Uno\Clip.mp4", copy.Media[2].Path);
        Assert.Equal(@"Z:\Otra\Dos\b.mp4", copy.Media[3].Path);
        Assert.True(copy.Media[4].Path.Length > 260);
        Assert.Equal("file:R:/LUTs/cine.cube", Lut(copy));
    }

    [WindowsOnlyFact("las rutas UNC son de Windows.")]
    public void En_Windows_un_proyecto_en_red_guarda_relativo_dentro_del_mismo_recurso()
    {
        const string ProjectDirectory = @"\\servidor\equipo\Proyectos\Uno";
        var project = WithLut(@"file://servidor/equipo/LUTs/cine.cube");
        project.Media =
        [
            new MediaRef { Id = "mismo", Path = @"\\servidor\equipo\Medios\a.mp4" },
            new MediaRef { Id = "otro-recurso", Path = @"\\servidor\otro\b.mp4" },
            new MediaRef { Id = "local", Path = @"C:\Videos\c.mp4" },
        ];

        Project copy = ProjectFilePaths.ForSaving(project, ProjectDirectory);

        Assert.Equal(@"..\..\Medios\a.mp4", copy.Media[0].Path);
        Assert.Equal(@"\\servidor\otro\b.mp4", copy.Media[1].Path);
        Assert.Equal(@"C:\Videos\c.mp4", copy.Media[2].Path);
        Assert.Equal("file:../../LUTs/cine.cube", Lut(copy));
        Assert.Equal(@"\\servidor\equipo\Medios\a.mp4", copy.Media[0].ResolvePath(ProjectDirectory));
    }

    [Fact]
    public void Los_nombres_reservados_de_Windows_se_reconocen_con_extension_y_sin_importar_mayusculas()
    {
        foreach (string reserved in (string[])["CON", "con", "aux.mp4", "Aux.Final.mov", "NUL .txt", "COM1.mp4", "lpt9", "COM\u00B9.mp4", "CONIN$", "conout$.log"])
        {
            Assert.True(WindowsFileNames.IsReserved(reserved), reserved);
            Assert.False(WindowsFileNames.IsReserved(WindowsFileNames.AvoidReserved(reserved)), reserved);
        }

        foreach (string allowed in (string[])["CONSOLA.mp4", "console", "aux1.mp4", "COM10.mp4", "LPT.mp4", "_CON.mp4", "mi aux.mp4", "", ".con", "Exportación.mp4"])
        {
            Assert.False(WindowsFileNames.IsReserved(allowed), allowed);
            Assert.Equal(allowed, WindowsFileNames.AvoidReserved(allowed));
        }

        Assert.Equal("_aux.mp4", WindowsFileNames.AvoidReserved("aux.mp4"));
    }

    private static string Name(Random random)
    {
        string[] pool = OperatingSystem.IsWindows() ? Parts : [.. Parts, .. UnixOnlyParts];
        string name = pool[random.Next(pool.Length)];
        return random.Next(3) == 0 ? name + " " + pool[random.Next(pool.Length)].Trim() : name;
    }

    /// <summary>Carpetas anidadas que, con la del proyecto, pasan de 260 caracteres.</summary>
    private static string LongFolder(Random random) =>
        Path.Combine([.. Enumerable.Range(0, 4).Select(i => $"{i} {Name(random).Trim()} " + new string('x', 70))]);

    private static string Resolve(string reference, string projectDirectory) =>
        Path.GetFullPath(reference["file:".Length..].Replace('/', Path.DirectorySeparatorChar), projectDirectory);

    private static string FileRef(string path) => "file:" + path.Replace(Path.DirectorySeparatorChar, '/');

    private static Project WithLut(string reference)
    {
        Project project = ModelSamples.ValidProject();
        project.Sequences[0].VideoTracks[0].Clips[0].Effects.Insert(0, new EffectInstance
        {
            Id = "fx-lut",
            Type = BuiltInEffects.Color,
            Options = { [BuiltInEffects.ColorOptions.Lut] = reference },
        });
        return project;
    }

    private static string Lut(Project project) =>
        project.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Options[BuiltInEffects.ColorOptions.Lut];
}
