// Prueba de humo de Native AOT para FotoToma.Core.
//
// Este programa se publica compilado de antemano (Native AOT) y recorre la API pública del núcleo
// tal como la usará el editor: crea un proyecto, importa medios, hace una serie de ediciones con
// deshacer y rehacer, guarda y abre el proyecto en una carpeta con espacios y tildes, exporta el
// esquema JSON y evalúa un parámetro animado. Si algo del núcleo dependiera de reflexión que el
// compilador AOT recorta, aquí fallaría en tiempo de ejecución aunque las pruebas normales (que corren
// con JIT) pasen.
//
// Termina con código 0 si todo salió bien y con 1 y un mensaje claro si algo falló.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FotoToma.Core.Commands;
using FotoToma.Core.Editing;
using FotoToma.Core.Model;
using FotoToma.Core.Serialization;
using FotoToma.Core.Timing;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // Sin consola (salida redirigida en algunos lanzadores): no importa.
}

var reloj = Stopwatch.StartNew();
int paso = 0;

void Avance(string texto) => Console.WriteLine($"  [{++paso,2}] {texto}");

void Exigir(bool condicion, string queSeEsperaba)
{
    if (!condicion)
    {
        throw new SmokeFailure(queSeEsperaba);
    }
}

static Time S(double segundos) => Time.FromSeconds(segundos);

try
{
    Console.WriteLine("FotoToma - prueba de humo del núcleo compilado con Native AOT");
    // Con JIT el proceso carga coreclr.dll; el ejecutable de Native AOT lleva su propio runtime dentro.
    bool jit = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
        .Any(m => string.Equals(m.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"  Modo: {(jit ? "JIT (sin compilar de antemano; esto no prueba Native AOT)" : "Native AOT")}");

    // --- Proyecto y medios -----------------------------------------------------------------------
    var ids = new SequentialIdGenerator();
    Project project = ProjectFactory.CreateDefault("Prueba AOT ñandú", ids, Rational.Fps30);
    Sequence sequence = project.Sequences[0];
    string seqId = sequence.Id;
    string v1 = sequence.VideoTracks[0].Id;
    string a1 = sequence.AudioTracks[0].Id;
    Exigir(sequence.VideoTracks.Count == 2 && sequence.AudioTracks.Count == 2, "el proyecto nuevo trae 2 pistas de video y 2 de audio");
    Avance($"Proyecto creado con la secuencia «{sequence.Name}» a {sequence.FrameRate.ToDouble():0.##} fps.");

    var undo = new UndoStack(project);
    var states = new List<string> { ProjectSerializer.Serialize(project) };

    void Ejecutar(ICommand command)
    {
        undo.Execute(command);
        states.Add(ProjectSerializer.Serialize(project));
    }

    IReadOnlyList<string> Editar(string descripcion, Func<EditContext, IReadOnlyList<string>> operacion)
    {
        var command = new SequenceEditCommand(descripcion, seqId, operacion, ids);
        Ejecutar(command);
        return command.CreatedIds;
    }

    void EditarSinCrear(string descripcion, Action<EditContext> operacion) =>
        Ejecutar(new SequenceEditCommand(descripcion, seqId, operacion, ids));

    var mediaA = new MediaRef
    {
        Id = ids.NewId("media"),
        Path = @"C:\Videos de prueba\toma uno ñ.mp4",
        Name = "toma uno ñ.mp4",
        Duration = S(20),
        HasVideo = true,
        HasAudio = true,
        FrameRate = Rational.Fps30,
        Width = 1920,
        Height = 1080,
        SampleRate = 48000,
        AudioChannels = 2,
    };
    var mediaB = new MediaRef
    {
        Id = ids.NewId("media"),
        Path = @"medios\toma dos.mov",
        Name = "toma dos.mov",
        Duration = S(15),
        HasVideo = true,
        HasAudio = true,
        FrameRate = Rational.Ntsc30,
        Width = 3840,
        Height = 2160,
        SampleRate = 48000,
        AudioChannels = 2,
    };
    Ejecutar(new AddMediaCommand(mediaA));
    Ejecutar(new AddMediaCommand(mediaB));
    Exigir(project.Media.Count == 2, "hay 2 medios importados");
    Avance("Dos medios importados con AddMediaCommand.");

    // --- Ediciones ---------------------------------------------------------------------------------
    // a: A[0,5) en 0 con su audio enlazado
    string a = Editar("Insertar", ctx => TimelineEdits.InsertMedia(ctx, mediaA.Id, S(0), S(5), v1, S(0), PlacementMode.Strict, a1))[0];
    // b: B[2,10) al final -> [5,13)
    string b = Editar("Agregar al final", ctx => TimelineEdits.AppendMedia(ctx, mediaB.Id, S(2), S(10), v1, a1))[0];
    // c: A[5,12) al final -> [13,20)
    string c = Editar("Agregar al final", ctx => TimelineEdits.AppendMedia(ctx, mediaA.Id, S(5), S(12), v1, a1))[0];
    Exigir(sequence.FindClip(a)?.Track.Clips.Count == 3, "la pista V1 tiene 3 clips");
    Exigir(sequence.GetLinkGroup(a).Count == 2, "el clip insertado quedó enlazado con su audio");
    Avance("InsertMedia y AppendMedia con audio enlazado: 3 clips en V1 y 3 en A1.");

    // Corta b en 9 s: b [5,9) y b2 [9,13)
    IReadOnlyList<string> piezas = Editar("Cortar", ctx => TimelineEdits.Split(ctx, b, S(9)));
    string b2 = piezas[0];
    Exigir(piezas.Count == 2, "el corte partió el video y su audio");
    // Recorte simple del final de a: [0,4) y queda un hueco [4,5)
    EditarSinCrear("Recortar", ctx => TimelineEdits.Trim(ctx, a, TrimEdge.Out, S(-1)));
    Exigir(sequence.GetClip(a).Clip.End == S(4), "el recorte dejó a terminando en 4 s");
    // Cierra el hueco en todas las pistas: b [4,8), b2 [8,12), c [12,19)
    EditarSinCrear("Cerrar hueco", ctx => TimelineEdits.CloseGap(ctx, v1, S(4.5), RippleScope.AllTracks));
    Exigir(sequence.GetClip(b).Clip.Start == S(4), "cerrar el hueco corrió b a 4 s");
    Avance("Split, Trim y CloseGap.");

    // Ripple del final de b: b [4,7), b2 [7,11), c [11,18)
    EditarSinCrear("Recortar con ripple", ctx => TimelineEdits.RippleTrim(ctx, b, TrimEdge.Out, S(-1), RippleScope.Track));
    Exigir(sequence.GetClip(c).Clip.Start == S(11), "el ripple corrió c a 11 s");
    // Roll del corte b|b2 medio segundo a la derecha: b [4,7.5), b2 [7.5,11)
    EditarSinCrear("Roll", ctx => TimelineEdits.Roll(ctx, b, S(0.5)));
    Exigir(sequence.GetClip(b2).Clip.Start == S(7.5), "el roll movió el corte a 7,5 s");
    // Slip de c: mismo lugar, otra parte del archivo
    EditarSinCrear("Slip", ctx => TimelineEdits.Slip(ctx, c, S(1)));
    Exigir(sequence.GetClip(c).Clip.SourceIn == S(6), "el slip cambió la entrada de c a 6 s del archivo");
    // Slide de b2 medio segundo a la izquierda entre sus vecinos
    EditarSinCrear("Slide", ctx => TimelineEdits.Slide(ctx, b2, S(-0.5)));
    Exigir(sequence.GetClip(b2).Clip.Start == S(7), "el slide llevó b2 a 7 s");
    Exigir(sequence.Duration == S(18), "la secuencia sigue durando 18 s después de roll, slip y slide");
    Avance("RippleTrim, Roll, Slip y Slide.");

    // Pista nueva y mover c allí, a los 25 s (su audio se queda en A1 pero también se corre)
    string v3 = Editar("Nueva pista", ctx => [TimelineEdits.AddTrack(ctx, TrackKind.Video, 2, "Video 3 ñ")])[0];
    EditarSinCrear("Mover", ctx => TimelineEdits.Move(ctx, c, S(25), v3, PlacementMode.Strict));
    Exigir(sequence.GetClip(c).Track.Id == v3, "c quedó en la pista nueva");
    Exigir(sequence.GetLinkGroup(c).All(m => m.Clip.Start == S(25)), "el audio de c se movió con él");
    // Borrado con ripple de a en todas las pistas: todo se corre 4 s a la izquierda
    EditarSinCrear("Borrar con ripple", ctx => TimelineEdits.RippleDelete(ctx, [a], RippleScope.AllTracks));
    Exigir(sequence.FindClip(a) is null, "a ya no existe");
    Exigir(sequence.GetClip(b).Clip.Start == Time.Zero, "b quedó al inicio");
    Exigir(sequence.GetClip(c).Clip.Start == S(21), "c se corrió a 21 s");
    Avance("AddTrack, Move a otra pista y RippleDelete en todas las pistas.");

    // Un efecto con un parámetro animado, dentro de un grupo de deshacer
    undo.BeginGroup("Agregar opacidad animada");
    var opacity = new Parameter
    {
        Value = 1,
        Keyframes =
        [
            new Keyframe(S(2), 0, Interpolation.Linear),
            new Keyframe(S(3), 1, Interpolation.EaseInOut),
            new Keyframe(S(4), 0.5, Interpolation.Hold),
        ],
    };
    undo.Execute(new SequenceEditCommand("Agregar efecto", seqId, ctx =>
    {
        Clip clip = ctx.Sequence.GetClip(b).Clip;
        clip.Effects.Add(new EffectInstance
        {
            Id = ctx.Ids.NewId("fx"),
            Type = "fototoma.opacidad",
            Parameters = { ["opacidad"] = opacity.Clone() },
        });
        return [clip.Effects[^1].Id];
    }, ids));
    undo.Execute(new RenameProjectCommand("Prueba AOT ñandú (editado)"));
    undo.EndGroup();
    states.Add(ProjectSerializer.Serialize(project));
    Exigir(ModelValidator.Validate(project).Count == 0, "el proyecto editado es válido: " + string.Join("; ", ModelValidator.Validate(project)));
    Avance($"Efecto con keyframes y renombre agrupados en un paso; {undo.Count} pasos en el historial.");

    // --- Deshacer y rehacer todo ---------------------------------------------------------------
    Exigir(undo.Count == states.Count - 1, $"el historial tiene {states.Count - 1} pasos (tiene {undo.Count})");
    for (int i = states.Count - 2; i >= 0; i--)
    {
        Exigir(undo.Undo(), "se pudo deshacer");
        Exigir(ProjectSerializer.Serialize(project) == states[i], $"deshacer hasta el paso {i} deja el proyecto igual que antes");
    }

    Exigir(!undo.CanUndo, "ya no queda nada por deshacer");
    for (int i = 1; i < states.Count; i++)
    {
        Exigir(undo.Redo(), "se pudo rehacer");
        Exigir(ProjectSerializer.Serialize(project) == states[i], $"rehacer hasta el paso {i} deja el proyecto igual que después de editar");
    }

    Avance($"Deshacer todo ({states.Count - 1} pasos) y rehacer todo: cada estado coincide texto a texto.");

    // --- Guardar y abrir ---------------------------------------------------------------------------
    string folder = Path.Combine(Path.GetTempPath(), "FotoToma prueba AOT ñ");
    Directory.CreateDirectory(folder);
    string file = Path.Combine(folder, "proyecto de prueba ñ.ftoma");
    try
    {
        string text = ProjectSerializer.Serialize(project);
        ProjectSerializer.Save(project, file);
        ProjectSerializer.Save(project, file); // la segunda vez reemplaza el archivo y deja un .bak
        Exigir(File.Exists(file + ".bak"), "el segundo guardado dejó la copia .bak");
        Project loaded = ProjectSerializer.Load(file);
        Exigir(ProjectSerializer.Serialize(loaded) == text, "el proyecto abierto desde disco es idéntico al guardado");
        Exigir(ModelValidator.Validate(loaded).Count == 0, "el proyecto abierto es válido");
        Project parsed = ProjectSerializer.Deserialize(text);
        Exigir(ProjectSerializer.Serialize(parsed) == text, "leer el texto y volver a escribirlo da el mismo texto");
        Exigir(loaded.FindMedia(mediaA.Id)?.Path == mediaA.Path, "la ruta con tildes del medio sobrevive al guardado");
        Avance($"Guardar (dos veces) y abrir en «{folder}»: {new FileInfo(file).Length} bytes, idéntico.");
    }
    finally
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // Si el antivirus tiene el archivo abierto, se queda en la carpeta temporal.
        }
    }

    // Un archivo que no es un proyecto debe dar un error claro, no un fallo del runtime
    bool rejected = false;
    try
    {
        ProjectSerializer.Deserialize("{\"schemaVersion\": \"no es un número\"}");
    }
    catch (ProjectFormatException)
    {
        rejected = true;
    }

    Exigir(rejected, "un JSON que no es proyecto se rechaza con ProjectFormatException");
    Avance("Un archivo inválido se rechaza con ProjectFormatException.");

    // --- Esquema JSON -----------------------------------------------------------------------------
    string schema = ProjectSchema.Export();
    Exigir(!string.IsNullOrWhiteSpace(schema), "el esquema exportado no está vacío");
    using (JsonDocument doc = JsonDocument.Parse(schema))
    {
        Exigir(doc.RootElement.ValueKind == JsonValueKind.Object, "el esquema es un objeto JSON");
        Exigir(doc.RootElement.TryGetProperty("properties", out JsonElement props) && props.EnumerateObject().Any(),
            "el esquema describe las propiedades del proyecto");
    }

    Avance($"Esquema JSON exportado: {schema.Length} caracteres, JSON válido.");

    // --- Parámetro animado ------------------------------------------------------------------------
    Exigir(opacity.Evaluate(S(1)) == 0, "antes del primer keyframe vale el primero");
    Exigir(Math.Abs(opacity.Evaluate(S(2.5)) - 0.5) < 1e-12, "a mitad de una rampa lineal vale la mitad");
    Exigir(Math.Abs(opacity.Evaluate(S(3.5)) - 0.75) < 1e-12, "a mitad de un EaseInOut vale la mitad del salto");
    Exigir(opacity.Evaluate(S(10)) == 0.5, "después del último keyframe vale el último");
    Clip saved = project.Sequences[0].GetClip(b).Clip;
    Exigir(saved.Effects[0].Parameters["opacidad"].Evaluate(S(2.5)) == opacity.Evaluate(S(2.5)), "el parámetro del clip evalúa igual que el original");
    Avance("Parámetro con keyframes evaluado (lineal, EaseInOut y extremos).");

    // --- Tiempo exacto ----------------------------------------------------------------------------
    Time frame = Time.FromFrames(1, Rational.Ntsc30);
    Exigir(Time.FromFrames(30000, Rational.Ntsc30).ToFrames(Rational.Ntsc30) == 30000, "30000 cuadros a 29,97 fps vuelven a ser 30000");
    Exigir(frame + frame == Time.FromFrames(2, Rational.Ntsc30), "sumar cuadros NTSC no acumula error");
    Avance("Tiempo exacto en cuadros NTSC sin error de redondeo.");

    reloj.Stop();
    Console.WriteLine();
    Console.WriteLine($"RESULTADO: todo bien. El núcleo funciona compilado con Native AOT ({reloj.Elapsed.TotalMilliseconds:0} ms).");
    return 0;
}
catch (SmokeFailure ex)
{
    Console.WriteLine();
    Console.WriteLine($"RESULTADO: falló en el paso {paso + 1}. Se esperaba que {ex.Message}, y no fue así.");
    return 1;
}
#pragma warning disable CA1031 // La prueba de humo debe explicar cualquier fallo, sea del tipo que sea
catch (Exception ex)
#pragma warning restore CA1031
{
    Console.WriteLine();
    Console.WriteLine($"RESULTADO: falló en el paso {paso + 1} con un error inesperado ({ex.GetType().Name}):");
    Console.WriteLine($"  {ex.Message}");
    Console.WriteLine("  Si el mismo código pasa en las pruebas normales, lo más probable es que algo del núcleo");
    Console.WriteLine("  use reflexión que Native AOT recortó. Detalle técnico:");
    Console.WriteLine(ex.ToString());
    return 1;
}

/// <summary>Una comprobación de la prueba de humo que no se cumplió.</summary>
internal sealed class SmokeFailure : Exception
{
    public SmokeFailure()
    {
    }

    public SmokeFailure(string message)
        : base(message)
    {
    }

    public SmokeFailure(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
