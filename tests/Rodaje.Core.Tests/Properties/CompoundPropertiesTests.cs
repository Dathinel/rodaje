using System.Diagnostics;
using CsCheck;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Xunit.Abstractions;
using static Rodaje.Core.Tests.Properties.V1Support;

namespace Rodaje.Core.Tests.Properties;

/// <summary>
/// Clips compuestos (formato 6) sobre proyectos aleatorios: crear y descomponer es la identidad, lo que se ve no cambia al
/// crear, y el validador rechaza exactamente los ciclos y los anidamientos de más de 16 niveles.
/// </summary>
/// <remarks>Para reproducir un fallo, pasar la semilla que reporta CsCheck en <c>seed</c> o en <c>CsCheck_Seed</c>.</remarks>
public class CompoundPropertiesTests(ITestOutputHelper output)
{
    private const string Seq = BuiltProject.SequenceId;

    /// <summary>Una selección: qué bits de la lista de clips entran y qué pistas se ocultan o silencian (sin bloqueos, para que la mayoría se pueda crear).</summary>
    private static readonly Gen<(ProjectSpec Project, long Pick, int Flags)> GenScenario =
        Gen.Select(RandomTimeline.GenProject, Gen.Long, Gen.Int[0, 63], (p, pick, flags) => (p with { LockBits = 0x5555 }, pick, flags));

    /// <summary>
    /// Crear un compuesto con clips al azar y descomponerlo deja el proyecto igual (salvo el orden de las transiciones en la
    /// lista); deshacer la creación lo deja igual byte a byte. Se saltan las selecciones con una transición que cruza su borde
    /// (crear la quita, como dice el formato) y las que tapan clips no elegidos en la pista del compuesto.
    /// </summary>
    [Fact]
    public void CrearYDescomponer_EsLaIdentidad()
    {
        int created = 0, tried = 0;
        GenScenario.Sample(
            s =>
            {
                tried++;
                if (RunCreateDecompose(s.Project, s.Pick, flags: 0, checkVisible: false))
                {
                    created++;
                }
            },
            writeLine: output.WriteLine,
            iter: 3000,
            print: s => $"{s.Project}\nSelección {s.Pick}");
        output.WriteLine($"{created} de {tried} selecciones crearon un compuesto");
        Assert.True(created * 5 >= tried, $"Muy pocas selecciones llegan a crear un compuesto: {created} de {tried}");
    }

    /// <summary>
    /// Lo que se ve y se oye (clips de pistas no ocultas ni mudas, desplegando los compuestos con
    /// <see cref="CompoundOps.Flatten(Project, Sequence)"/>) es lo mismo antes y después de crear el compuesto.
    /// </summary>
    /// <remarks>Semilla que lo muestra: <c>2wMq_kCYfmj2</c>.</remarks>
    [Fact(Skip = "Defecto: el compuesto va en la pista más baja tocada y las anidadas nuevas no copian Hidden ni Muted; si esa pista está oculta (o muda) desaparece lo elegido de pistas visibles, y al revés lo oculto pasa a verse. Semilla 2wMq_kCYfmj2.")]
    public void CrearCompuesto_NoCambiaLoQueSeVe() =>
        GenScenario.Sample(
            s => { _ = RunCreateDecompose(s.Project, s.Pick, s.Flags, checkVisible: true); },
            writeLine: output.WriteLine,
            iter: 3000,
            print: s => $"{s.Project}\nSelección {s.Pick}, pistas {s.Flags}");

    /// <summary>Lo mismo que <see cref="CrearCompuesto_NoCambiaLoQueSeVe"/>, con todas las pistas visibles y con sonido.</summary>
    [Fact]
    public void CrearCompuesto_SinPistasOcultas_NoCambiaLoQueSeVe() =>
        GenScenario.Sample(
            s => { _ = RunCreateDecompose(s.Project, s.Pick, flags: 0, checkVisible: true); },
            writeLine: output.WriteLine,
            iter: 3000,
            print: s => $"{s.Project}\nSelección {s.Pick}");

    private static bool RunCreateDecompose(ProjectSpec spec, long pick, int flags, bool checkVisible)
    {
        BuiltProject built = RandomTimeline.Build(spec);
        Project project = built.Project;
        Sequence outer = built.Sequence;
        int index = 0;
        foreach (Track track in outer.AllTracks)
        {
            track.Locked = false;
            bool off = ((flags >> index++) & 1) == 1;
            if (track.Kind == TrackKind.Video)
            {
                track.Hidden = off;
            }
            else
            {
                track.Muted = off;
            }
        }

        List<Clip> all = [.. outer.AllTracks.SelectMany(t => t.Clips)];
        string[] chosen = [.. all.Where((_, i) => ((pick >> (i % 64)) & 1) == 1).Select(c => c.Id)];
        if (chosen.Length == 0 || CrossesTransition(outer, chosen))
        {
            return false;
        }

        string before = ProjectSerializer.Serialize(project);
        string canonicalBefore = Canonical(project);
        List<string> visibleBefore = Visible(project, outer);
        var stack = new UndoStack(project);
        SequenceEditCommand create = Edits.CreateCompound(Seq, chosen, "Compuesto", built.Ids);
        try
        {
            stack.Execute(create);
        }
        catch (EditException)
        {
            Assert.Equal(before, ProjectSerializer.Serialize(project));
            return false;
        }

        Assert.Empty(ModelValidator.Validate(project));
        string afterCreate = ProjectSerializer.Serialize(project);
        Assert.Equal(afterCreate, ProjectSerializer.Serialize(ProjectSerializer.Deserialize(afterCreate)));
        string nestedId = create.CreatedIds[^1];
        Assert.All(create.CreatedIds.SkipLast(1), id => Assert.Equal(nestedId, outer.GetClip(id).Clip.SequenceId));
        if (checkVisible)
        {
            Assert.Equal(visibleBefore, Visible(project, outer));
        }

        stack.Undo();
        Assert.Equal(before, ProjectSerializer.Serialize(project));
        stack.Redo();
        Assert.Equal(afterCreate, ProjectSerializer.Serialize(project));

        stack.Execute(Edits.DecomposeCompound(Seq, create.CreatedIds[0], built.Ids));
        Assert.Empty(ModelValidator.Validate(project));
        Assert.Null(project.FindSequence(nestedId));
        Assert.Equal(canonicalBefore, Canonical(project));
        return true;
    }

    /// <summary>Si una transición une un clip elegido (o enlazado con uno) con otro que no lo es.</summary>
    private static bool CrossesTransition(Sequence sequence, string[] chosen)
    {
        var ids = chosen.SelectMany(id => sequence.GetLinkGroup(id)).Select(m => m.Clip.Id).ToHashSet(StringComparer.Ordinal);
        return sequence.AllTracks.SelectMany(t => t.Transitions).Any(t => ids.Contains(t.From) != ids.Contains(t.To));
    }

    /// <summary>Lo que se ve y se oye: cada clip activo de pista visible (video) o con sonido (audio), con su tramo, ordenado.</summary>
    private static List<string> Visible(Project project, Sequence sequence)
    {
        Sequence flat = CompoundOps.Flatten(project, sequence);
        IEnumerable<string> Of(IEnumerable<Track> tracks, string kind) => tracks.SelectMany(t => t.Clips.Where(c => c.Enabled).Select(c =>
            Inv($"{kind} {c.Start.Ticks}-{c.End.Ticks} {c.MediaId}{c.Generator?.Text} {c.SourceIn.Ticks}-{c.SourceOut.Ticks}")));
        return [.. Of(flat.VideoTracks.Where(t => !t.Hidden), "v").Concat(Of(flat.AudioTracks.Where(t => !t.Muted), "a")).Order(StringComparer.Ordinal)];
    }

    /// <summary>Un grafo de secuencias: <c>Edges[i]</c> son las secuencias a las que apuntan los compuestos de la secuencia <c>i</c>.</summary>
    internal sealed record GraphSpec(int Count, int[][] Edges)
    {
        public override string ToString() =>
            $"{Count} secuencias: " + string.Join(" ", Edges.Select((e, i) => Inv($"{i}->[{string.Join(",", e)}]")));
    }

    /// <summary>
    /// Grafos pequeños con salida de 0 a 2 por secuencia: a veces una cadena larga (para cruzar los 16 niveles), a veces
    /// solo aristas hacia adelante (sin ciclos) y a veces cualquiera.
    /// </summary>
    private static readonly Gen<GraphSpec> GenGraph =
        Gen.Int[2, 22].SelectMany(n => Gen.Select(
            Gen.Int[0, 2],
            Gen.Int[0, n - 1].Array[0, 2].Array[n],
            (mode, raw) =>
            {
                int[][] edges = new int[n][];
                for (int i = 0; i < n; i++)
                {
                    IEnumerable<int> targets = raw[i];
                    if (mode == 0)
                    {
                        // Cadena 0 -> 1 -> … -> n-1, con algún salto hacia adelante.
                        targets = targets.Where(t => t > i).Take(1).Concat(i + 1 < n ? [i + 1] : []);
                    }
                    else if (mode == 1)
                    {
                        targets = targets.Where(t => t > i);
                    }

                    edges[i] = [.. targets.Distinct()];
                }

                return new GraphSpec(n, edges);
            }));

    /// <summary>
    /// El validador informa un compuesto que se contiene a sí mismo o que anida demasiado exactamente cuando el grafo de
    /// secuencias tiene un ciclo o un camino de más de <see cref="ModelValidator.MaxCompoundDepth"/> compuestos; y
    /// <see cref="CompoundOps.Flatten(Project, Sequence)"/> termina siempre, también con ciclos.
    /// </summary>
    [Fact]
    public void CiclosYProfundidad_SeRechazanExactamente()
    {
        int rejected = 0, accepted = 0;
        GenGraph.Sample(
            g =>
            {
                Project project = BuildGraph(g);
                bool expected = HasCycle(g) || LongestPath(g) > ModelValidator.MaxCompoundDepth;
                bool reported = ModelValidator.Validate(project).Any(e => e.Contains("sí mismo", StringComparison.Ordinal) || e.Contains("niveles", StringComparison.Ordinal));
                Assert.Equal(expected, reported);
                if (reported)
                {
                    rejected++;
                }
                else
                {
                    accepted++;
                }

                foreach (Sequence sequence in project.Sequences)
                {
                    Sequence flat = CompoundOps.Flatten(project, sequence);
                    Assert.DoesNotContain(flat.AllTracks.SelectMany(t => t.Clips), c => c.IsCompound);
                }
            },
            writeLine: output.WriteLine,
            iter: 3000,
            print: g => g.ToString());
        output.WriteLine($"rechazados {rejected}, aceptados {accepted}");
    }

    /// <summary>Una cadena de exactamente 16 compuestos se acepta y una de 17 se rechaza.</summary>
    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void CadenaEnElLimite(int links, bool valid)
    {
        int[][] edges = [.. Enumerable.Range(0, links + 1).Select(i => i < links ? new[] { i + 1 } : [])];
        Project project = BuildGraph(new GraphSpec(links + 1, edges));
        Assert.Equal(valid, ModelValidator.Validate(project).Count == 0);
    }

    /// <summary>
    /// Un archivo de pocos kilobytes con 17 niveles de 3 secuencias, cada una con compuestos a las 3 del nivel siguiente
    /// (sin ciclos), se valida en poco tiempo.
    /// </summary>
    [Fact(Skip = "Defecto: ModelValidator.CompoundDepth recorre cada camino sin memoria; un archivo de unas decenas de KB con 17 niveles de 3 secuencias cruzadas tardaría decenas de minutos en abrir (3^16 caminos por compuesto; con 12 niveles ya son unos 9 s). Ver el PR.")]
    public async Task GrafoEnRombo_SeValidaRapido()
    {
        const int levels = 17, width = 3;
        int n = levels * width;
        int[][] edges = new int[n][];
        for (int i = 0; i < n; i++)
        {
            int level = i / width;
            edges[i] = level + 1 < levels ? [.. Enumerable.Range((level + 1) * width, width)] : [];
        }

        Project project = BuildGraph(new GraphSpec(n, edges));
        string json = ProjectSerializer.Serialize(project);
        var watch = Stopwatch.StartNew();
        Task open = Task.Run(() => Record.Exception(() => ProjectSerializer.Deserialize(json)));
        Task first = await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(first == open, $"Abrir {json.Length / 1024} KB tardó más de 10 s");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    /// <summary>Cada secuencia con un clip de medio y, detrás, un compuesto por cada arista, en su única pista de video.</summary>
    private static Project BuildGraph(GraphSpec g)
    {
        var project = new Project { Id = "p", Name = "Grafo", Media = [Media("m", 60)] };
        for (int i = 0; i < g.Count; i++)
        {
            var track = new Track { Id = Inv($"v{i}"), Kind = TrackKind.Video, Name = "Video 1" };
            track.Clips.Add(new Clip { Id = Inv($"c{i}"), MediaId = "m", SourceIn = Time.Zero, SourceOut = Time.FromSeconds(1), Start = Time.Zero });
            Time at = Time.FromSeconds(1);
            for (int k = 0; k < g.Edges[i].Length; k++)
            {
                track.Clips.Add(new Clip
                {
                    Id = Inv($"k{i}-{k}"),
                    SequenceId = Inv($"s{g.Edges[i][k]}"),
                    SourceIn = Time.Zero,
                    SourceOut = Time.FromSeconds(1),
                    Start = at,
                });
                at += Time.FromSeconds(1);
            }

            project.Sequences.Add(new Sequence { Id = Inv($"s{i}"), Name = Inv($"S{i}"), VideoTracks = [track] });
        }

        return project;
    }

    private static bool HasCycle(GraphSpec g)
    {
        int[] state = new int[g.Count];
        bool Visit(int v)
        {
            state[v] = 1;
            foreach (int w in g.Edges[v])
            {
                if (state[w] == 1 || (state[w] == 0 && Visit(w)))
                {
                    return true;
                }
            }

            state[v] = 2;
            return false;
        }

        return Enumerable.Range(0, g.Count).Any(v => state[v] == 0 && Visit(v));
    }

    /// <summary>Aristas del camino más largo (el grafo no tiene ciclos).</summary>
    private static int LongestPath(GraphSpec g)
    {
        int?[] memo = new int?[g.Count];
        int From(int v) => memo[v] ??= g.Edges[v].Length == 0 ? 0 : g.Edges[v].Max(w => From(w) + 1);
        return Enumerable.Range(0, g.Count).Max(From);
    }
}
