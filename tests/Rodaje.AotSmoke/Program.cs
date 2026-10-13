// Prueba de humo: recorre la API pública del núcleo compilado con Native AOT. Si algo dependiera de
// reflexión que el compilador recorta, falla aquí aunque las pruebas con JIT pasen.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // Sin consola (salida redirigida): no importa.
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
    Console.WriteLine("Rodaje - prueba de humo del núcleo compilado con Native AOT");
    // Con JIT, al llegar aquí ya se compiló al menos este método; Native AOT no tiene JIT y la cuenta es 0.
    // Vale en Windows y en Linux (buscar coreclr.dll fallaba en Linux, donde es libcoreclr.so). No sirve
    // RuntimeFeature.IsDynamicCodeSupported: PublishAot lo deja en false también al correr con JIT.
    bool jit = System.Runtime.JitInfo.GetCompiledMethodCount() > 0;
    Console.WriteLine($"  Modo: {(jit ? "JIT (sin compilar de antemano; esto no prueba Native AOT)" : "Native AOT")}");

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
            Type = "rodaje.opacidad",
            Parameters = { ["opacidad"] = opacity.Clone() },
        });
        return [clip.Effects[^1].Id];
    }, ids));
    undo.Execute(new RenameProjectCommand("Prueba AOT ñandú (editado)"));
    undo.EndGroup();
    states.Add(ProjectSerializer.Serialize(project));
    Exigir(ModelValidator.Validate(project).Count == 0, "el proyecto editado es válido: " + string.Join("; ", ModelValidator.Validate(project)));
    Avance($"Efecto con keyframes y renombre agrupados en un paso; {undo.Count} pasos en el historial.");

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

    string folder = Path.Combine(Path.GetTempPath(), "Rodaje prueba AOT ñ");
    Directory.CreateDirectory(folder);
    string file = Path.Combine(folder, "proyecto de prueba ñ.rodaje");
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
            // El antivirus puede tener el archivo abierto; se queda en la carpeta temporal.
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

    // Los controles del guardado (NaN, sustituto suelto) y de la apertura (número que desborda) son convertidores en
    // options.Converters sobre el contexto generado: si el código generado fijara el de double o string y se saltara el
    // de las opciones, aquí se guardaría sin error (o con la ArgumentException del escritor) o se abriría como infinito.
    {
        string high = "a" + (char)0xD83C + "b";
        string low = "a" + (char)0xDFAC;
        Project Base()
        {
            Project p = ProjectFactory.CreateDefault("Controles del guardado", new SequentialIdGenerator(), Rational.Fps30);
            p.Sequences[0].MasterEffects.Add(new EffectInstance
            {
                Id = "fx-control",
                Type = "complemento.desconocido",
                Parameters = { ["extra"] = new Parameter { Value = 7.25, Keyframes = [new Keyframe(S(1), 1) { SpatialOut = 2 }] } },
                Options = { ["nota"] = "bien" },
            });
            p.Sequences[0].VideoTracks[0].Transitions.Add(new TransitionInstance
            {
                Id = "tr-control",
                Type = "rodaje.disolver",
                From = "x",
                To = "y",
                Parameters = { ["borde"] = 0.5 },
                Options = { ["dir"] = "izq" },
            });
            return p;
        }

        int fx = Base().Sequences[0].MasterEffects.Count - 1;
        string efecto = $"$.sequences[0].masterEffects[{fx}]";
        const string transicion = "$.sequences[0].videoTracks[0].transitions[0]";
        const string noFinito = "no es un número finito";
        const string suelto = "sustituto UTF-16 suelto";
        var casos = new (string Ruta, string Problema, Action<Project> Romper)[]
        {
            ("$.sequences[0].masterGainDb", noFinito, p => p.Sequences[0].MasterGainDb = double.NaN),
            ($"{efecto}.parameters.extra.value", noFinito, p => p.Sequences[0].MasterEffects[fx].Parameters["extra"].Value = double.NaN),
            ($"{efecto}.parameters.extra.keyframes[0].spatialOut", noFinito,
                p => p.Sequences[0].MasterEffects[fx].Parameters["extra"].Keyframes[0] = new Keyframe(S(1), 1) { SpatialOut = double.PositiveInfinity }),
            ($"{transicion}.parameters.borde", noFinito, p => p.Sequences[0].VideoTracks[0].Transitions[0].Parameters["borde"] = double.NaN),
            ("$.name", suelto, p => p.Name = high),
            ($"{efecto}.options.nota", suelto, p => p.Sequences[0].MasterEffects[fx].Options["nota"] = low),
            ($"{efecto}.options (el nombre de una clave)", suelto, p => p.Sequences[0].MasterEffects[fx].Options[low] = "x"),
            ($"{efecto}.parameters (el nombre de una clave)", suelto,
                p => p.Sequences[0].MasterEffects[fx].Parameters[high] = new Parameter { Value = 1 }),
            ($"{transicion}.parameters (el nombre de una clave)", suelto, p => p.Sequences[0].VideoTracks[0].Transitions[0].Parameters[low] = 1),
        };

        string carpeta = Path.Combine(Path.GetTempPath(), "Rodaje prueba AOT controles " + Environment.ProcessId);
        Directory.CreateDirectory(carpeta);
        try
        {
            string archivo = Path.Combine(carpeta, "roto.rodaje");
            foreach ((string ruta, string problema, Action<Project> romper) in casos)
            {
                Project roto = Base();
                romper(roto);
                File.WriteAllText(archivo, "anterior");
                string mensaje;
                try
                {
                    ProjectSerializer.Save(roto, archivo);
                    mensaje = "(se guardó sin error)";
                }
                catch (ProjectFormatException ex)
                {
                    mensaje = ex.Message;
                }

                Exigir(mensaje.Contains($"«{ruta}»", StringComparison.Ordinal) && mensaje.Contains(problema, StringComparison.Ordinal),
                    $"guardar con «{ruta}» roto se rechaza con su ruta y «{problema}» (dio: {mensaje})");
                Exigir(File.ReadAllText(archivo) == "anterior", $"el guardado rechazado de «{ruta}» no toca el archivo anterior");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(carpeta, recursive: true);
            }
            catch (IOException)
            {
                // Se queda en la carpeta temporal.
            }
        }

        // La transición de control no une clips y no pasaría el validador, pero el desborde se rechaza antes, al leer el JSON:
        // si no se rechazara, el mensaje sería el del validador y la comprobación fallaría igual.
        string bueno = ProjectSerializer.Serialize(Base());
        foreach ((string ruta, string viejo) in new[]
        {
            ($"{efecto}.parameters.extra.value", "\"value\": 7.25"),
            ($"{efecto}.parameters.extra.keyframes[0].spatialOut", "\"spatialOut\": 2"),
            ($"{transicion}.parameters.borde", "\"borde\": 0.5"),
        })
        {
            foreach (string desborde in new[] { "1e400", "-1e400" })
            {
                string texto = bueno.Replace(viejo, viejo[..(viejo.IndexOf(':', StringComparison.Ordinal) + 2)] + desborde, StringComparison.Ordinal);
                Exigir(texto != bueno, $"el texto de control tiene {viejo}");
                string mensaje;
                try
                {
                    ProjectSerializer.Deserialize(texto);
                    mensaje = "(se abrió sin error)";
                }
                catch (ProjectFormatException ex)
                {
                    mensaje = ex.Message;
                }

                Exigir(mensaje.Contains($"«{ruta}»", StringComparison.Ordinal) && mensaje.Contains("desborda", StringComparison.Ordinal),
                    $"abrir con {desborde} en «{ruta}» se rechaza con su ruta (dio: {mensaje})");
            }
        }

        Avance($"Controles del guardado y la apertura: {casos.Length} valores sin guardar y 6 desbordes rechazados con su ruta.");
    }

    string schema = ProjectSchema.Export();
    Exigir(!string.IsNullOrWhiteSpace(schema), "el esquema exportado no está vacío");
    using (JsonDocument doc = JsonDocument.Parse(schema))
    {
        Exigir(doc.RootElement.ValueKind == JsonValueKind.Object, "el esquema es un objeto JSON");
        Exigir(doc.RootElement.TryGetProperty("properties", out JsonElement props) && props.EnumerateObject().Any(),
            "el esquema describe las propiedades del proyecto");
    }

    Avance($"Esquema JSON exportado: {schema.Length} caracteres, JSON válido.");

    Exigir(opacity.Evaluate(S(1)) == 0, "antes del primer keyframe vale el primero");
    Exigir(Math.Abs(opacity.Evaluate(S(2.5)) - 0.5) < 1e-12, "a mitad de una rampa lineal vale la mitad");
    Exigir(Math.Abs(opacity.Evaluate(S(3.5)) - 0.75) < 1e-12, "a mitad de un EaseInOut vale la mitad del salto");
    Exigir(opacity.Evaluate(S(10)) == 0.5, "después del último keyframe vale el último");
    Clip saved = project.Sequences[0].GetClip(b).Clip;
    Exigir(saved.Effects[0].Parameters["opacidad"].Evaluate(S(2.5)) == opacity.Evaluate(S(2.5)), "el parámetro del clip evalúa igual que el original");
    Avance("Parámetro con keyframes evaluado (lineal, EaseInOut y extremos).");

    // Formato 3: transiciones, opciones, volumen de pista y migración desde el formato 2
    {
        var ids3 = new SequentialIdGenerator();
        Project p3 = ProjectFactory.CreateDefault("Formato 3", ids3, Rational.Fps30);
        string s3 = p3.Sequences[0].Id;
        string pv = p3.Sequences[0].VideoTracks[0].Id;
        string pa = p3.Sequences[0].AudioTracks[0].Id;
        var stack3 = new UndoStack(p3);
        string initial3 = ProjectSerializer.Serialize(p3);
        stack3.Execute(new AddMediaCommand(mediaA.Clone()));
        var append = Edits.AppendMedia(s3, mediaA.Id, S(0), S(4), pv, pa, ids3);
        stack3.Execute(append);
        var append2 = Edits.AppendMedia(s3, mediaA.Id, S(8), S(9), pv, pa, ids3);
        stack3.Execute(append2);
        string left = append.CreatedIds[0];
        string right = append2.CreatedIds[0];
        var cut = Edits.AddTransition(s3, left, BuiltInTransitions.Push, S(3), ids3);
        stack3.Execute(cut);
        string transitionId = cut.CreatedIds[0];
        stack3.Execute(Edits.SetTransitionOption(s3, transitionId, BuiltInTransitions.PushOptions.Direction, "up", ids3));
        TransitionInstance transition = p3.Sequences[0].VideoTracks[0].Transitions.Single();
        Exigir(transition.Duration == S(1), "la transición se acota al clip más corto (1 s)");
        var blend = Edits.AddEffect(s3, right, BuiltInEffects.Blend, ids: ids3);
        stack3.Execute(blend);
        stack3.Execute(Edits.SetEffectOption(s3, right, blend.CreatedIds[0], BuiltInEffects.BlendOptions.Mode, "overlay", ids3));
        var volume = Edits.AddEffect(s3, pa, BuiltInEffects.Audio, ids: ids3);
        stack3.Execute(volume);
        stack3.Execute(Edits.SetKeyframe(s3, pa, volume.CreatedIds[0], BuiltInEffects.AudioParameters.GainDb, S(2), -6, ids: ids3));
        stack3.Execute(Edits.Split(s3, left, S(3.5), ids3));
        Exigir(transition.Duration == S(0.5) && transition.To == right, "cortar cerca del corte acorta la transición y la deja en su sitio");
        Exigir(ModelValidator.Validate(p3).Count == 0, "el proyecto con formato 3 es válido");
        string text3 = ProjectSerializer.Serialize(p3);
        Exigir(ProjectSerializer.Serialize(ProjectSerializer.Deserialize(text3)) == text3, "el formato 3 se lee y escribe igual");
        while (stack3.Undo())
        {
        }

        Exigir(ProjectSerializer.Serialize(p3) == initial3, "deshacer todo devuelve el proyecto inicial byte a byte");
        Exigir(EffectRegistry.Find(EffectRegistry.Find(BuiltInTransitions.Wipe)!.Code)?.Type == BuiltInTransitions.Wipe, "el registro encuentra por código");

        const string v2 = """
            {"schemaVersion":2,"id":"p","name":"v2","media":[{"id":"m","path":"a.wav","name":"a","duration":7056000000,
            "hasVideo":false,"hasAudio":true,"frameRate":null,"width":0,"height":0,"sampleRate":48000,"audioChannels":2}],
            "sequences":[{"id":"s","name":"S","frameRate":{"num":30,"den":1},"width":1920,"height":1080,"sampleRate":48000,
            "videoTracks":[],"audioTracks":[{"id":"a","kind":"audio","name":"A","muted":false,"locked":false,"hidden":false,
            "clips":[{"id":"c","mediaId":"m","sourceIn":0,"sourceOut":705600000,"start":0,"linkId":null,"enabled":true,
            "effects":[{"id":"e","type":"rodaje.volume","enabled":true,"parameters":{"gain":{"value":0.5,"keyframes":[]}}}]}]}],
            "markers":[],"regions":[]}]}
            """;
        EffectInstance migrated = ProjectSerializer.Deserialize(v2).Sequences[0].AudioTracks[0].Clips[0].Effects[0];
        Exigir(migrated.Type == BuiltInEffects.Audio
            && Math.Abs(migrated.Parameters[BuiltInEffects.AudioParameters.GainDb].Value + 6.0206) < 1e-3,
            "la ganancia lineal 0,5 del formato 2 llega como −6,02 dB");
    }

    Avance("Formato 3: transición acotada, mezcla, volumen de pista, deshacer exacto y migración desde el formato 2.");

    // Formato 5: un título con estilo copiado (enumeraciones, diccionario de curvas, registros) que se divide y se guarda
    {
        var ids5 = new SequentialIdGenerator();
        Project p5 = ProjectFactory.CreateDefault("Formato 5", ids5, Rational.Fps30);
        Track track5 = p5.Sequences[0].VideoTracks[0];
        track5.Role = TrackRoles.Subtitles;
        track5.Clips.Add(new Clip
        {
            Id = "titulo",
            SourceIn = S(-1),
            SourceOut = S(3),
            Generator = new ClipGenerator
            {
                Text = "Hola\nmundo ñ",
                Style = new TextStyle
                {
                    Align = TextAlign.Left,
                    Stroke = new StyleStroke { Width = 4, Join = StrokeJoin.Bevel },
                    In = new StyleAnimation { DurationSeconds = 0.5, Curves = { ["opacity"] = [new StyleKey(0, 0), new StyleKey(0.5, 1, Interpolation.EaseOut)] } },
                },
            },
        });
        SequenceEditCommand split5 = Edits.Split(p5.Sequences[0].Id, "titulo", S(1), ids5);
        split5.Apply(p5);
        string right5 = split5.CreatedIds[0];
        string text5 = ProjectSerializer.Serialize(p5);
        Project back5 = ProjectSerializer.Deserialize(text5);
        Clip title5 = back5.Sequences[0].GetClip(right5).Clip;
        Exigir(ProjectSerializer.Serialize(back5) == text5, "el formato 5 se lee y escribe igual");
        Exigir(!title5.Generator!.Edges.In && title5.Generator.Style!.Stroke!.Join == StrokeJoin.Bevel
            && title5.Generator.Style.In!.Curves["opacity"][1].Interp == Interpolation.EaseOut,
            "el título dividido pierde la entrada y conserva su estilo");
        Exigir(back5.Sequences[0].GetClip("titulo").Clip.SourceIn == S(-1), "el título conserva su origen negativo");
    }

    Avance("Formato 5: título con estilo copiado, dividido, guardado y leído.");

    Time frame = Time.FromFrames(1, Rational.Ntsc30);
    Exigir(Time.FromFrames(30000, Rational.Ntsc30).ToFrames(Rational.Ntsc30) == 30000, "30000 cuadros a 29,97 fps vuelven a ser 30000");
    Exigir(frame + frame == Time.FromFrames(2, Rational.Ntsc30), "sumar cuadros NTSC no acumula error");
    Avance("Tiempo exacto en cuadros NTSC sin error de redondeo.");

    // Se publica con InvariantGlobalization, como la app: si el núcleo pidiera una cultura por nombre, lanzaría aquí.
    Exigir(Rodaje.Core.Text.TextCasing.Apply("Ñandú en istmo", TextCase.Upper) == "ÑANDÚ EN ISTMO", "las mayúsculas de un título se calculan sin culturas");
    Avance("Mayúsculas de títulos sin culturas (InvariantGlobalization).");

    // Sin ICU, Normalize no separa las tildes y la cultura actual ordena por código (tarea 231).
    Exigir(Rodaje.Packages.PackageLibrary.ToIdentifier("Música épica Ñandú") == "musica-epica-nandu",
        "un nombre con tildes da un identificador sin ellas (y no «m-sica-pica-and»)");
    List<string> desordenados = ["Zeta", "ñu", "Árbol", "nube", "Ola"];
    Exigir(string.Join(",", desordenados.Order(Rodaje.Packages.SpanishText.Comparer)) == "Árbol,nube,ñu,Ola,Zeta",
        "las listas se ordenan como un diccionario español");
    Avance("Identificadores sin tildes y orden alfabético español sin ICU.");

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
