using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Rodaje.App.Services;
using Rodaje.App.Services.Titles;
using Rodaje.App.Services.Tracking;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Properties;
using Rodaje.Core.Timing;
using Xunit.Sdk;

namespace Rodaje.App.Tests.Fuzz;

/// <summary>
/// Secuencias aleatorias de acciones sobre <see cref="EditorSession"/>, sin ventana. El proyecto inicial y las ediciones
/// de la línea de tiempo salen de <see cref="RandomTimeline"/> (los mismos ayudantes que las propiedades de Core); encima
/// van las acciones de la sesión: importar, cortar en el cursor, tres puntos, grupos, multicámara, compuestos, efectos,
/// máscaras, seguimiento aplicado, títulos, transiciones, mezclador, gestos, deshacer, rehacer, saltos del historial,
/// guardar y abrir.
/// </summary>
/// <remarks>
/// En cada paso comprueba: ninguna excepción; el proyecto válido (<see cref="ModelValidator"/>); el reproductor con la
/// misma copia que la sesión; deshacer y rehacer exactos byte a byte (el proyecto serializado de cada posición del
/// historial se guarda y se compara al volver a ella); una acción rechazada no cambia nada; y las acciones que no deben
/// tocar la sincronía no desfasan clips enlazados. La semilla reproduce la secuencia entera.
/// </remarks>
internal sealed class SessionFuzzer
{
    /// <summary>Las acciones y su peso en el sorteo.</summary>
    private enum Act
    {
        Timeline,
        Gesture,
        Import,
        Append,
        Seek,
        Select,
        SplitAtPlayhead,
        SplitAll,
        DeleteSide,
        DeleteSelection,
        Nudge,
        TrimEdge,
        Group,
        Ungroup,
        Color,
        CloseGaps,
        Resync,
        Link,
        Detach,
        Attributes,
        Speed,
        Freeze,
        Reverse,
        ThreePoint,
        SourceAction,
        Multicam,
        Compound,
        Decompose,
        EnterCompound,
        LeaveCompound,
        Effect,
        Parameter,
        Mask,
        Tracking,
        Title,
        Transition,
        Mixer,
        TimelineMode,
        Undo,
        Redo,
        Jump,
        Save,
        Reopen,
    }

    private static readonly (Act Act, int Weight)[] Weights =
    [
        (Act.Timeline, 30), (Act.Gesture, 3), (Act.Import, 2), (Act.Append, 3), (Act.Seek, 8), (Act.Select, 10),
        (Act.SplitAtPlayhead, 5), (Act.SplitAll, 2), (Act.DeleteSide, 2), (Act.DeleteSelection, 3), (Act.Nudge, 3), (Act.TrimEdge, 4),
        (Act.Group, 2), (Act.Ungroup, 1), (Act.Color, 1), (Act.CloseGaps, 2), (Act.Resync, 2), (Act.Link, 2), (Act.Detach, 1),
        (Act.Attributes, 2), (Act.Speed, 3), (Act.Freeze, 1), (Act.Reverse, 1), (Act.ThreePoint, 4), (Act.SourceAction, 2),
        (Act.Multicam, 3), (Act.Compound, 2), (Act.Decompose, 1), (Act.EnterCompound, 2), (Act.LeaveCompound, 2), (Act.Effect, 4),
        (Act.Parameter, 4), (Act.Mask, 4), (Act.Tracking, 3), (Act.Title, 3), (Act.Transition, 4), (Act.Mixer, 4), (Act.TimelineMode, 2),
        (Act.Undo, 8), (Act.Redo, 5), (Act.Jump, 2), (Act.Save, 1), (Act.Reopen, 1),
    ];

    private static readonly Act[] Table = [.. Weights.SelectMany(w => Enumerable.Repeat(w.Act, w.Weight))];

    /// <summary>Las de <see cref="RandomTimeline"/> que pueden desfasar enlazados a propósito: las que excluye la propiedad 11 de Core y volver a sincronizar (ver <see cref="SyncChangingActs"/>).</summary>
    private static readonly HashSet<string> SyncChangingOps = new(StringComparer.Ordinal)
    {
        "LinkClips", "SetTrackState", "SetSpeed", "RateStretch", "FreezeFrame", "SetCurve", "SetReverse", "ApplySpeedPreset", "Resync",
    };

    /// <summary>
    /// Acciones que pueden desfasar enlazados a propósito: enlazar, velocidad, pegar atributos (velocidad), deshacer y saltos.
    /// La multicámara también: sus desfases son inventados, no los del sonido, así que cambiar al medio del audio enlazado
    /// mide un desfase que en una multicámara de verdad no habría. Y volver a sincronizar: alinea el clip con su referencia,
    /// y en un enlace de tres clips ya desfasados puede alejarlo del tercero.
    /// </summary>
    private static readonly HashSet<Act> SyncChangingActs =
    [
        Act.Multicam, Act.Resync, Act.Link, Act.Attributes, Act.Speed, Act.Freeze, Act.Reverse, Act.Undo, Act.Redo, Act.Jump, Act.Reopen, Act.Gesture,
        Act.TimelineMode, Act.EnterCompound, Act.LeaveCompound, Act.Decompose,
    ];

    private readonly int _seed;
    private readonly Random _random;
    private readonly string _directory;
    private readonly string _projectPath;
    private readonly FuzzPlayback _playback = new();
    private readonly FakeProber _prober = new();
    private readonly FakeDialogs _dialogs = new() { Choice = UnsavedChangesChoice.Discard };
    private readonly FuzzIds _ids = new();
    private readonly StringBuilder _log = new();

    /// <summary>Lo que se quitó del principio de <see cref="_log"/>: con los dos, el recorrido entero.</summary>
    private readonly StringBuilder _dropped = new();
    private readonly EditorSession _session;

    /// <summary>El proyecto serializado en cada posición del historial: <c>_states[i]</c> es el de <c>i</c> pasos aplicados.</summary>
    private List<string> _states = [];
    private int _imported;
    private bool _historyMoved;
    private int _step;

    /// <summary>Dónde va (<see cref="Mark"/>), publicado para leerlo desde el hilo que vigila si la prueba se cuelga.</summary>
    private Position? _position;

    /// <summary>Sube en cada <see cref="Mark"/>: lo que vigila <see cref="UiThread"/> para ver que el fuzz avanza.</summary>
    private long _progress;

    /// <summary>Pasos terminados y el más lento, publicados juntos para el mensaje de un cuelgue.</summary>
    private Totals _totals = new(0, 0, 0);

    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Action<int, string>? _onMark;
    private int _steps;

    private SessionFuzzer(int seed, string directory, Action<int, string>? onMark)
    {
        _onMark = onMark;
        _seed = seed;
        _random = new Random(seed);
        _directory = directory;
        _projectPath = Path.Combine(directory, "fuzz" + ProjectSerializer.FileExtension);
        _session = new EditorSession(_playback, _prober, _dialogs, _ids);
    }

    /// <summary>
    /// Corre <paramref name="steps"/> acciones con la semilla; falla con la semilla, el paso y las últimas acciones. Devuelve
    /// el recorrido entero (una línea por acción), para comprobar que una semilla fija sigue llegando a lo que probaba.
    /// </summary>
    public static string Run(int seed, int steps) => Run(seed, steps, UiThread.DefaultTimeout);

    /// <summary>
    /// Como <see cref="Run(int, int)"/>, con el tope contra cuelgues: falla si un tramo (la acción, sus comprobaciones o
    /// un paso de deshacer todo) no termina en <paramref name="idle"/>, no si la semilla entera tarda. Así un runner cargado
    /// que va lento no se confunde con un cuelgue, y un cuelgue dice paso, acción y tramo, cuánto llevaba ahí y a qué ritmo
    /// iban los pasos anteriores (la semilla 9335 en la CI de Windows: 120 s «por el paso 240 de 300: Select», una acción
    /// que no espera nada y que en Linux tarda menos de un milisegundo). <paramref name="onMark"/> corre en el hilo del fuzz
    /// al empezar cada tramo, con el paso y el tramo, y <paramref name="waitSlice"/> sustituye la espera de la vigilancia
    /// (ver <see cref="UiThread.Run(Func{Task}, Func{long}, TimeSpan, Func{Task, TimeSpan, bool})"/>): con los dos se prueba
    /// la vigilancia sin depender del reloj.
    /// </summary>
    internal static string Run(int seed, int steps, TimeSpan idle, Action<int, string>? onMark = null, Func<Task, TimeSpan, bool>? waitSlice = null)
    {
        using var directory = new TempDirectory();
        string trace = "";
        SessionFuzzer? running = null;
        try
        {
            UiThread.Run(
                async () =>
                {
                    var fuzzer = new SessionFuzzer(seed, directory.Path, onMark);
                    Volatile.Write(ref running, fuzzer);
                    await fuzzer.RunAsync(steps);
                    trace = fuzzer._dropped.ToString() + fuzzer._log;
                },
                () => Volatile.Read(ref running) is { } fuzzer ? Interlocked.Read(ref fuzzer._progress) : -1,
                idle,
                waitSlice);
        }
        catch (TimeoutException ex)
        {
            // Se lee desde fuera del hilo del fuzz, así que solo lo publicado aparte.
            string where = Volatile.Read(ref running)?.Describe() ?? "antes de abrir el proyecto inicial.";
            throw new TimeoutException($"Semilla {seed}: {ex.Message} Iba por {where}", ex);
        }

        return trace;
    }

    /// <summary>
    /// Empieza un tramo: lo publica para el mensaje de un cuelgue, cuenta el avance para la vigilancia y lleva el tiempo de
    /// los pasos. <paramref name="step"/> 0 es antes del primer paso; <c>_steps + 1</c>, deshacer y rehacer todo al final.
    /// </summary>
    private void Mark(int step, Act? act, string phase)
    {
        long now = Stopwatch.GetTimestamp();
        Position? previous = _position;
        if (previous is not null && previous.Step != step && previous.Step >= 1 && previous.Step <= _steps)
        {
            // Terminó el paso anterior.
            Totals totals = _totals;
            long spent = now - previous.StepStarted;
            Volatile.Write(ref _totals, spent > totals.SlowestTicks
                ? new Totals(totals.Done + 1, previous.Step, spent)
                : totals with { Done = totals.Done + 1 });
        }

        long stepStarted = previous is not null && previous.Step == step ? previous.StepStarted : now;
        Volatile.Write(ref _position, new Position(step, act, phase, stepStarted, now));
        Interlocked.Increment(ref _progress);
        _onMark?.Invoke(step, phase);
    }

    /// <summary>«el paso 240 de 300: Select (comprobar), en ese tramo desde hace 118.2 s; 239 pasos en 1.9 s, el más lento el 40 (26 ms).»</summary>
    private string Describe()
    {
        Position? at = Volatile.Read(ref _position);
        Totals totals = Volatile.Read(ref _totals);
        long now = Stopwatch.GetTimestamp();
        string where = at switch
        {
            null or { Step: 0 } => "el proyecto inicial",
            { } p when p.Step > _steps => "deshacer y rehacer todo, al final",
            { } p => FormattableString.Invariant($"el paso {p.Step} de {_steps}: {p.Act}"),
        };
        string phase = at is null ? "" : FormattableString.Invariant($" ({at.Phase}), en ese tramo desde hace {Seconds(now - at.PhaseStarted)} s");
        string pace = totals.Done == 0
            ? "ningún paso terminado"
            : FormattableString.Invariant($"{totals.Done} pasos en {Seconds((at?.StepStarted ?? now) - _started)} s, el más lento el {totals.SlowestStep} ({totals.SlowestTicks * 1000 / Stopwatch.Frequency} ms)");
        return $"{where}{phase}; {pace}.";

        static string Seconds(long ticks) => (ticks / (double)Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>Paso (0 antes del primero), acción y tramo en curso, y desde cuándo.</summary>
    private sealed record Position(int Step, Act? Act, string Phase, long StepStarted, long PhaseStarted);

    private sealed record Totals(int Done, int SlowestStep, long SlowestTicks);

    private async Task RunAsync(int steps)
    {
        _steps = steps;
        Mark(0, null, "abrir");
        await OpenInitialAsync();
        for (_step = 1; _step <= steps; _step++)
        {
            Act act = Table[_random.Next(Table.Length)];
            Mark(_step, act, "preparar");
            string sequenceId = _session.SequenceId;
            Dictionary<(string, string), long>? syncBefore = PairOffsets();
            string before = Serialize();
            int countBefore = _session.History.Count;
            int redoBefore = _session.History.RedoCount;
            bool changesSync = SyncChangingActs.Contains(act);
            Mark(_step, act, "acción");
            _log.Append(CultureInfo.InvariantCulture, $"{_step}. {act}");
            try
            {
                changesSync |= await DoAsync(act);
            }
            catch (XunitException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Fail($"«{act}» lanzó {ex.GetType().Name}: {ex}");
            }

            _log.Append('\n');
            Mark(_step, act, "comprobar");
            SyncTransport();
            Check(act, before, countBefore, redoBefore);
            Mark(_step, act, "sincronía");
            if (!changesSync && sequenceId == _session.SequenceId && syncBefore is not null && SyncBreak(syncBefore) is { } broken)
            {
                Fail($"«{act}» desincronizó «{broken.A}» de «{broken.B}»: {broken.Before} → {broken.After} ticks (cursor {_session.EditPosition.Ticks}).\nAntes:\n{Dump(ProjectSerializer.Deserialize(before).GetSequence(sequenceId))}");
            }

            // Solo las últimas acciones interesan para reproducir: el registro no crece sin límite.
            if (_log.Length > 60_000)
            {
                _dropped.Append(_log, 0, _log.Length - 30_000);
                _log.Remove(0, _log.Length - 30_000);
            }
        }

        // Al final, deshacer todo y rehacer todo recorre los estados guardados.
        Mark(steps + 1, null, "deshacer todo");
        while (_session.Undo())
        {
            ExpectState("deshacer todo");
            Mark(steps + 1, null, "deshacer todo");
        }

        Mark(steps + 1, null, "rehacer todo");
        while (_session.Redo())
        {
            ExpectState("rehacer todo");
            Mark(steps + 1, null, "rehacer todo");
        }
    }

    private async Task OpenInitialAsync()
    {
        ProjectSpec spec = RandomSpec();
        BuiltProject built = RandomTimeline.Build(spec);
        built.Project.Name = "fuzz";
        foreach (MediaRef media in built.Project.Media)
        {
            media.Path = Path.Combine(_directory, media.Path);
        }

        ProjectSerializer.Save(built.Project, _projectPath);
        _log.Append(CultureInfo.InvariantCulture, $"Proyecto inicial: {spec}\n");
        if (!await _session.OpenProjectAsync(_projectPath))
        {
            Fail("No se pudo abrir el proyecto inicial.");
        }

        _session.Multicams = BuildMulticams();
        ResetStates();
        Check(Act.Reopen, Serialize(), 0, 0);
    }

    /// <summary>Una receta de proyecto con los mismos rangos que <c>RandomTimeline.GenProject</c>, sacada de la semilla.</summary>
    private ProjectSpec RandomSpec()
    {
        MediaSpec[] media = [.. Enumerable.Range(0, _random.Next(2, 6)).Select(_ =>
        {
            int k = _random.Next(0, 10);
            return new MediaSpec(k < 6 ? 0 : k < 8 ? 1 : 2, _random.Next(1, 601), _random.Next(0, 4));
        })];
        PlacementSpec[] placements = [.. Enumerable.Range(0, _random.Next(1, 9)).Select(_ => new PlacementSpec(
            _random.Next(0, 100), _random.Next(0, 301), _random.Next(1, 151), _random.Next(0, 100), _random.Next(0, 4), _random.Next(0, 601),
            _random.Next(2) == 0))];
        int titles = _random.Next(0, 65536);
        int speeds = _random.Next(0, 65536);
        return new ProjectSpec(_random.Next(RandomTimeline.Rates.Length), _random.Next(1, 4), _random.Next(1, 4), media, placements,
            // Casi siempre sin pistas bloqueadas: con una bloqueada casi todo se rechaza y el fuzz rinde menos.
            _random.Next(4) == 0 ? _random.Next(0, 4096) : 0xFFF, _random.Next(0, 65536), speeds % 3 == 0 ? speeds : 0,
            Titles: titles % 4 == 0 ? titles : 0);
    }

    // ------------------------------------------------------------------ comprobaciones

    private void Check(Act act, string before, int countBefore, int redoBefore)
    {
        Project project = _session.Project;
        IReadOnlyList<string> errors = ModelValidator.Validate(project);
        if (errors.Count > 0)
        {
            Fail($"«{act}» dejó el proyecto inválido: {string.Join(" | ", errors)}");
        }

        if (_session.IsInGesture)
        {
            Fail($"«{act}» dejó un gesto abierto.");
        }

        string now = Serialize();
        if (!_session.IsSourceActive && _playback.Last is { } copy && ProjectSerializer.Serialize(copy) != now)
        {
            Fail($"«{act}»: el reproductor no tiene la misma copia del proyecto que la sesión.");
        }

        if (act is Act.Save or Act.Reopen)
        {
            return;
        }

        if (act is Act.Undo or Act.Redo or Act.Jump || _historyMoved)
        {
            _historyMoved = false;
            ExpectState(act.ToString());
            return;
        }

        int count = _session.History.Count;
        bool redoDropped = _session.History.RedoCount != redoBefore;
        if (count == countBefore && now == before && !redoDropped)
        {
            return;
        }

        if (count == countBefore && countBefore == _session.History.Limit)
        {
            // Historial lleno: el paso nuevo entró y el más viejo salió.
            _states.RemoveAt(0);
            count--;
        }
        else if (count == countBefore)
        {
            // Mismo número de pasos y el proyecto cambió: o no pasó por el historial o se fundió con el paso anterior. Aquí
            // fundirse nunca es correcto: tras cada paso el fuzz deshace y rehace, y eso pone la barrera de fusión.
            Fail($"«{act}» cambió el proyecto sin agregar un paso al historial (¿se fundió con un paso viejo?).\n{FirstDifference(before, now)}");
        }
        else if (count != countBefore + 1)
        {
            Fail($"«{act}» movió el historial de {countBefore} a {count} pasos.");
        }
        else
        {
            count--;
        }

        // Una edición nueva descarta lo que se podía rehacer y anota el estado de su posición.
        _states.RemoveRange(count + 1, _states.Count - count - 1);
        _states.Add(now);

        // Deshacer exacto: lo que se acaba de hacer se deshace y se rehace byte a byte.
        Mark(_step, act, "deshacer y rehacer");
        if (_session.Undo())
        {
            ExpectState($"deshacer «{act}»");
            if (!_session.Redo())
            {
                Fail($"No se pudo rehacer «{act}».");
            }

            ExpectState($"rehacer «{act}»");
        }
    }

    private void ExpectState(string what)
    {
        int count = _session.History.Count;
        if (count >= _states.Count)
        {
            Fail($"Tras {what} el historial tiene {count} pasos y solo hay {_states.Count - 1} estados anotados.");
        }

        string now = Serialize();
        if (now != _states[count])
        {
            Fail($"Tras {what} el proyecto no es byte a byte el del paso {count}.\n{FirstDifference(_states[count], now)}");
        }
    }

    private void ResetStates() => _states = [Serialize()];

    private string Serialize() => ProjectSerializer.Serialize(_session.Project);

    private void SyncTransport() => _session.UpdateTransport(_playback.Snapshot);

    /// <summary>
    /// El desfase de cada pareja de enlazados del mismo medio, sin velocidad y en pistas sin bloquear: la diferencia entre
    /// sus «inicio − entrada». Se mide por parejas y no con <see cref="LinkSync"/>, que mide contra un clip de referencia: al
    /// quitar la referencia (borrar su pista, taparla) un desfase que ya había se mediría de otra forma y parecería nuevo.
    /// </summary>
    private Dictionary<(string, string), long>? PairOffsets()
    {
        if (_session.Project.FindSequence(_session.SequenceId) is not { } sequence)
        {
            return null;
        }

        var result = new Dictionary<(string, string), long>();
        IEnumerable<IGrouping<(string, string), Clip>> groups = sequence.AllTracks.Where(t => !t.Locked)
            .SelectMany(t => t.Clips)
            .Where(c => c.LinkId is not null && c.Generator is null && !c.IsCompound && c.Speed is null)
            .GroupBy(c => (c.LinkId!, c.MediaId));
        foreach (IGrouping<(string, string), Clip> group in groups)
        {
            List<Clip> clips = [.. group.OrderBy(c => c.Id, StringComparer.Ordinal)];
            for (int i = 0; i < clips.Count; i++)
            {
                for (int j = i + 1; j < clips.Count; j++)
                {
                    result[(clips[i].Id, clips[j].Id)] = (clips[i].Start - clips[i].SourceIn).Ticks - (clips[j].Start - clips[j].SourceIn).Ticks;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Una pareja que ya estaba y cambió a otro desfase que no es 0, o una nueva (las piezas de un corte, un clip recién puesto) con un
    /// desfase que no tenía ninguna pareja antes.
    /// </summary>
    private (string A, string B, long Before, long After)? SyncBreak(Dictionary<(string, string), long> before)
    {
        var known = new HashSet<long>(before.Values.Select(Math.Abs)) { 0 };
        foreach (((string a, string b), long offset) in PairOffsets() ?? [])
        {
            // Quedar en sincronía (desfase 0, como «Volver a sincronizar») siempre vale.
            if (offset != 0 && (before.TryGetValue((a, b), out long old) ? old != offset : !known.Contains(Math.Abs(offset))))
            {
                return (a, b, before.GetValueOrDefault((a, b)), offset);
            }
        }

        return null;
    }

    private static string FirstDifference(string expected, string actual)
    {
        int i = 0;
        while (i < expected.Length && i < actual.Length && expected[i] == actual[i])
        {
            i++;
        }

        int from = Math.Max(0, i - 60);
        return $"Primera diferencia en el carácter {i} (largos {expected.Length} y {actual.Length}):\nEsperado: …{Slice(expected, from)}…\nObtenido: …{Slice(actual, from)}…";

        static string Slice(string text, int from) => text.Substring(from, Math.Min(400, text.Length - from));
    }

    private void Fail(string message) =>
        throw new XunitException($"Fuzz de la sesión: semilla {_seed}, paso {_step}. {message}\nAcciones:\n{_log}\nSelección: [{string.Join(", ", _session.SelectedClipIds)}]; secuencia «{_session.SequenceId}»:\n{DumpActive()}");

    /// <summary>La secuencia activa, o el aviso de que ya no está (si no, el mensaje del fallo lanzaría otra excepción).</summary>
    private string DumpActive() =>
        _session.Project.FindSequence(_session.SequenceId) is { } sequence ? Dump(sequence) : "  (no está en el proyecto)\n";

    /// <summary>Las pistas y sus clips en una línea cada uno, para leer el fallo sin depurar.</summary>
    private static string Dump(Sequence sequence)
    {
        var text = new StringBuilder();
        foreach (Track track in sequence.AllTracks)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {track.Id}{(track.Locked ? " (bloqueada)" : "")}:");
            foreach (Clip clip in track.Clips)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $" [{clip.Id} {clip.MediaId} @{clip.Start.Ticks} {clip.SourceIn.Ticks}-{clip.SourceOut.Ticks}{(clip.LinkId is { } l ? " enl " + l : "")}{(clip.Speed is null ? "" : " vel")}]");
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    // ------------------------------------------------------------------ acciones

    /// <summary>Hace la acción; devuelve <c>true</c> si puede desfasar enlazados a propósito.</summary>
    private async Task<bool> DoAsync(Act act)
    {
        EditorSession s = _session;
        switch (act)
        {
            case Act.Timeline:
                return TimelineOp(gesture: false);

            case Act.Gesture:
            {
                s.BeginGesture("Gesto del fuzz");
                int n = _random.Next(1, 4);
                for (int i = 0; i < n; i++)
                {
                    TimelineOp(gesture: true);
                }

                if (_random.Next(3) == 0)
                {
                    Note(" cancelado");
                    s.CancelGesture();
                }
                else
                {
                    s.EndGesture();
                }

                return true;
            }

            case Act.Import:
            {
                string path = Path.Combine(_directory, FormattableString.Invariant($"nuevo{++_imported}.mp4"));
                switch (_random.Next(4))
                {
                    case 0:
                        _prober.Audio(path, 1 + _random.Next(20));
                        break;
                    case 1:
                        _prober.Video(path, 1 + _random.Next(20), audio: false, rate: RandomTimeline.Rates[_random.Next(RandomTimeline.Rates.Length)]);
                        break;
                    case 2:
                        // Un archivo que no se puede leer: se avisa y no entra.
                        break;
                    default:
                        _prober.Video(path, 1 + _random.Next(20), rate: RandomTimeline.Rates[_random.Next(RandomTimeline.Rates.Length)]);
                        break;
                }

                ImportResult result = await s.ImportMediaAsync([path]);
                Note($" {Path.GetFileName(path)} → {result.MediaIds.Count}");
                s.Multicams = BuildMulticams();
                return false;
            }

            case Act.Append:
            {
                MediaRef media = s.Project.Media[_random.Next(s.Project.Media.Count)];
                Time? from = _random.Next(3) == 0 ? Time.FromTicks(media.Duration.Ticks / (1 + _random.Next(4))) : null;
                Time? to = _random.Next(3) == 0 ? Time.FromTicks(media.Duration.Ticks / (1 + _random.Next(3)) + Time.FromFrames(5, s.Sequence.FrameRate).Ticks) : null;
                Note($" {media.Id}");
                s.AppendToTimeline(media.Id, from, to);
                return false;
            }

            case Act.Seek:
            {
                Time at = RandomInstant();
                Note($" {at.Ticks}");
                s.Seek(at);
                return false;
            }

            case Act.Select:
                RandomSelection();
                return false;

            case Act.SplitAtPlayhead:
                s.SplitAtPlayhead();
                return false;

            case Act.SplitAll:
                s.SplitAllTracksAtPlayhead();
                return false;

            case Act.DeleteSide:
            {
                bool left = _random.Next(2) == 0;
                TimelineState t = s.Timeline;
                Note($" {(left ? "izquierda" : "derecha")} {t.RippleScope} magnética={t.MagneticMainTrack} auto={t.AutoRipple}");
                Note((left ? s.DeleteLeftOfPlayhead() : s.DeleteRightOfPlayhead()) ? " → ok" : " → nada");
                return false;
            }

            case Act.DeleteSelection:
                s.DeleteSelection();
                return false;

            case Act.Nudge:
            {
                int frames = _random.Next(-10, 11);
                Note($" {frames}");
                s.NudgeSelection(frames);
                return false;
            }

            case Act.TrimEdge:
            {
                if (RandomClip() is { } clip)
                {
                    var edge = new SelectedEdge(clip.Id, _random.Next(2) == 0 ? TrimEdge.In : TrimEdge.Out, _random.Next(2) == 0);
                    int frames = _random.Next(-8, 9);
                    Note($" {edge} {frames}");
                    s.SelectedEdge = edge;
                    s.TrimSelectedEdge(frames);
                }

                return false;
            }

            case Act.Group:
                s.GroupSelection();
                return false;

            case Act.Ungroup:
                s.UngroupSelection();
                return false;

            case Act.Color:
                s.SetSelectionColor(_random.Next(3) == 0 ? null : FormattableString.Invariant($"#{_random.Next(0x1000000):X6}"));
                return false;

            case Act.CloseGaps:
                s.CloseAllGaps();
                return false;

            case Act.Resync:
            {
                IReadOnlyList<SyncOffset> offsets = LinkSync.Find(s.Sequence);
                string id = offsets.Count > 0 && _random.Next(4) != 0 ? offsets[_random.Next(offsets.Count)].ClipId : RandomClip()?.Id ?? "clip-falta";
                Note($" {id}");
                s.Resync(id, _random.Next(2) == 0 ? ResyncMode.Move : ResyncMode.Slip);
                return false;
            }

            case Act.Link:
                s.ToggleLinkSelection();
                return true;

            case Act.Detach:
                s.DetachAudioOfSelection();
                return false;

            case Act.Attributes:
                if (_random.Next(2) == 0)
                {
                    s.CopyAttributes();
                }
                else
                {
                    s.PasteAttributes((ClipAttributeKinds)_random.Next(1, 32));
                }

                return true;

            case Act.Speed:
                if (_random.Next(2) == 0)
                {
                    s.StepSpeed(_random.Next(2) == 0);
                }
                else
                {
                    Rational rate = RandomTimeline.SpeedRates[_random.Next(RandomTimeline.SpeedRates.Length)];
                    Note($" {rate}");
                    s.SetSpeed(s.SelectedClipIds, rate);
                }

                return true;

            case Act.Freeze:
                s.FreezeFrameAtPlayhead();
                return true;

            case Act.Reverse:
                s.ToggleReverse();
                return true;

            case Act.ThreePoint:
                ThreePoint();
                return false;

            case Act.SourceAction:
            {
                PrepareSource();
                var action = (ThreePointAction)_random.Next(5);
                Note($" {action}");
                s.SourceAction(action);
                return action == ThreePointAction.FitToFill;
            }

            case Act.Multicam:
            {
                if (s.Multicams.Count > 0)
                {
                    IReadOnlyList<MulticamAngle> angles = s.Multicams[_random.Next(s.Multicams.Count)];
                    int angle = _random.Next(angles.Count + 1);
                    Note($" ángulo {angle} de {angles.Count}");
                    s.SwitchAngle(angles, angle);
                }

                return false;
            }

            case Act.Compound:
                s.CreateCompoundFromSelection();
                return false;

            case Act.Decompose:
                s.DecomposeCompound(RandomClip(c => c.IsCompound)?.Id);
                return true;

            case Act.EnterCompound:
                if (RandomClip(c => c.IsCompound) is { } compound)
                {
                    Note($" {compound.Id}");
                    s.EnterCompound(compound.Id);
                }

                return true;

            case Act.LeaveCompound:
                s.LeaveCompound(_random.Next(3) - 1);
                return true;

            case Act.Effect:
                EffectOp();
                return false;

            case Act.Parameter:
                ParameterOp();
                return false;

            case Act.Mask:
                MaskOp();
                return false;

            case Act.Tracking:
                TrackingOp();
                return false;

            case Act.Title:
                TitleOp();
                return false;

            case Act.Transition:
                TransitionOp();
                return false;

            case Act.Mixer:
                MixerOp();
                return false;

            case Act.TimelineMode:
            {
                TimelineState t = s.Timeline;
                switch (_random.Next(4))
                {
                    case 0:
                        t.MagneticMainTrack = !t.MagneticMainTrack;
                        break;
                    case 1:
                        t.AutoRipple = !t.AutoRipple;
                        break;
                    case 2:
                        t.IgnoreGroups = !t.IgnoreGroups;
                        break;
                    default:
                        t.RippleMode = (RippleMode)_random.Next(Enum.GetValues<RippleMode>().Length);
                        break;
                }

                return false;
            }

            case Act.Undo:
                Note(s.Undo() ? " → hecho" : " → nada");
                return true;

            case Act.Redo:
                Note(s.Redo() ? " → hecho" : " → nada");
                return true;

            case Act.Jump:
            {
                int position = _random.Next(s.History.Count + s.History.RedoCount + 1);
                Note($" {position}");
                s.JumpToHistory(position);
                return true;
            }

            case Act.Save:
            {
                string before = Serialize();
                if (!await s.SaveAsAsync(_projectPath))
                {
                    Fail("No se pudo guardar.");
                }

                if (Serialize() != before)
                {
                    Fail("Guardar cambió el proyecto.");
                }

                return false;
            }

            case Act.Reopen:
            {
                if (!await s.SaveAsAsync(_projectPath))
                {
                    Fail("No se pudo guardar antes de abrir.");
                }

                string saved = Serialize();
                if (!await s.OpenProjectAsync(_projectPath))
                {
                    Fail("No se pudo abrir lo guardado.");
                }

                string opened = Serialize();
                if (opened != saved)
                {
                    Fail($"Guardar y abrir no devuelve el mismo proyecto.\n{FirstDifference(saved, opened)}");
                }

                s.Multicams = BuildMulticams();
                ResetStates();
                return true;
            }

            default:
                throw new InvalidOperationException(act.ToString());
        }
    }

    /// <summary>Una edición de <see cref="RandomTimeline"/> sobre la secuencia principal (deshacer y rehacer, por la sesión).</summary>
    private bool TimelineOp(bool gesture)
    {
        EditorSession s = _session;
        Sequence main = s.Project.Sequences[0];
        if (main.Id != BuiltProject.SequenceId)
        {
            return false;
        }

        var built = new BuiltProject(s.Project, main.FrameRate, _ids);
        var spec = new OpSpec(_random.Next(RandomTimeline.KindCount), _random.Next(1001), _random.Next(1001), _random.Next(1001), _random.Next(1001),
            _random.Next(-40, 41));
        ResolvedOp op = RandomTimeline.Resolve(built, spec);
        Note($" {op.Text}");
        if (op.Command is null)
        {
            // Deshacer o rehacer dentro de un gesto no se puede: solo fuera.
            if (!gesture)
            {
                _historyMoved = true;
                Note(op.Kind == OpKind.Undo ? (s.Undo() ? " → hecho" : " → nada") : (s.Redo() ? " → hecho" : " → nada"));
                return true;
            }

            return false;
        }

        bool done = gesture ? s.TryExecute(op.Command) : s.Execute(op.Command);
        Note(done ? " → ok" : " → rechazada");
        return SyncChangingOps.Contains(op.Kind.ToString());
    }

    private void ThreePoint()
    {
        EditorSession s = _session;
        PrepareSource();
        if (_random.Next(3) == 0)
        {
            s.Seek(RandomInstant());
            SyncTransport();
            s.MarkIn();
            s.Seek(RandomInstant());
            SyncTransport();
            s.MarkOut();
        }

        // La edición de tres puntos solo inserta o sobrescribe (otro modo es un error de programación: ArgumentException).
        PlacementMode mode = _random.Next(2) == 0 ? PlacementMode.Insert : PlacementMode.Overwrite;
        var fit = (FourPointFit)_random.Next(Enum.GetValues<FourPointFit>().Length);
        ThreePointOutcome outcome = s.ThreePointEdit(mode, fit);
        Note($" {mode} {fit} → {outcome}");
    }

    /// <summary>Abre un medio en el origen, marca su entrada y salida y vuelve (a veces) al programa.</summary>
    private void PrepareSource()
    {
        EditorSession s = _session;
        MediaRef media = s.Project.Media[_random.Next(s.Project.Media.Count)];
        s.OpenSource(media.Id);
        SyncTransport();
        if (_random.Next(4) != 0)
        {
            s.Seek(Time.FromTicks((long)(media.Duration.Ticks * _random.NextDouble() * 0.6)));
            SyncTransport();
            s.MarkIn();
        }

        if (_random.Next(4) != 0)
        {
            s.Seek(Time.FromTicks((long)(media.Duration.Ticks * (0.4 + _random.NextDouble() * 0.6))));
            SyncTransport();
            s.MarkOut();
        }

        s.SetSourceTracks(_random.Next(5) != 0, _random.Next(5) != 0, RandomTrack(TrackKind.Video)?.Id, RandomTrack(TrackKind.Audio)?.Id);
        if (_random.Next(2) == 0)
        {
            s.ShowProgram();
            SyncTransport();
        }

        Note($" origen {media.Id} [{s.SourceIn?.Ticks}, {s.SourceOut?.Ticks})");
    }

    private void EffectOp()
    {
        EditorSession s = _session;
        string owner = _random.Next(4) == 0 ? RandomTrack(null)?.Id ?? "pista-falta" : RandomClip()?.Id ?? "clip-falta";
        EffectRef[] effects = OwnerEffects(owner);
        switch (_random.Next(5))
        {
            case 0 when effects.Length > 0:
            {
                EffectRef effect = effects[_random.Next(effects.Length)];
                Note($" quitar {effect.Id} de {owner}");
                s.Execute(Edits.RemoveEffect(s.SequenceId, owner, effect.Id, _ids));
                break;
            }

            case 1 when effects.Length > 0:
            {
                EffectRef effect = effects[_random.Next(effects.Length)];
                Note($" mover {effect.Id} de {owner}");
                s.Execute(Edits.MoveEffect(s.SequenceId, owner, effect.Id, _random.Next(-1, effects.Length + 1), _ids));
                break;
            }

            case 2 when effects.Length > 0:
            {
                EffectRef effect = effects[_random.Next(effects.Length)];
                Note($" activar {effect.Id} de {owner}");
                s.Execute(Edits.SetEffectEnabled(s.SequenceId, owner, effect.Id, _random.Next(2) == 0, _ids));
                break;
            }

            default:
            {
                string type = EffectRegistry.All[_random.Next(EffectRegistry.All.Length)].Type;
                Note($" añadir {type} a {owner}");
                s.Execute(_random.Next(3) == 0 ? Edits.EnsureEffect(s.SequenceId, owner, type, _ids) : Edits.AddEffect(s.SequenceId, owner, type, ids: _ids));
                break;
            }
        }
    }

    private void ParameterOp()
    {
        EditorSession s = _session;
        string owner = _random.Next(5) == 0 ? RandomTrack(null)?.Id ?? "pista-falta" : RandomClip()?.Id ?? "clip-falta";
        EffectRef[] effects = OwnerEffects(owner);
        if (effects.Length == 0)
        {
            return;
        }

        EffectRef effect = effects[_random.Next(effects.Length)];
        if (EffectRegistry.Find(effect.Type) is not { Parameters.Length: > 0 } definition)
        {
            return;
        }

        ParameterDefinition parameter = definition.Parameters[_random.Next(definition.Parameters.Length)];
        double value = _random.Next(8) switch
        {
            0 => parameter.Minimum,
            1 => parameter.Maximum,
            2 => parameter.Maximum + 1 + _random.Next(100),
            3 => double.NaN,
            4 => double.PositiveInfinity,
            _ => parameter.Minimum + ((parameter.Maximum - parameter.Minimum) * _random.NextDouble()),
        };
        Time at = RandomEffectTime(owner);
        Note($" {owner}/{effect.Id}.{parameter.Name} = {value.ToString(CultureInfo.InvariantCulture)} en {at.Ticks}");
        switch (_random.Next(4))
        {
            case 0:
                s.Execute(Edits.SetKeyframe(s.SequenceId, owner, effect.Id, parameter.Name, at, value, ids: _ids));
                break;
            case 1:
                s.Execute(Edits.RemoveKeyframe(s.SequenceId, owner, effect.Id, parameter.Name, at, _ids));
                break;
            default:
                s.Execute(Edits.SetParameter(s.SequenceId, owner, effect.Id, parameter.Name, value, at, ids: _ids));
                break;
        }
    }

    private void MaskOp()
    {
        EditorSession s = _session;
        if (RandomClip(c => c.Generator is null || _random.Next(3) == 0) is not { } clip)
        {
            return;
        }

        string[] masks = [.. clip.Effects.Where(e => e.Type == BuiltInEffects.Mask).Select(e => e.Id)];
        Time at = RandomEffectTime(clip.Id);
        string mask = masks.Length > 0 ? masks[_random.Next(masks.Length)] : "mascara-falta";
        int choice = _random.Next(masks.Length == 0 ? 2 : 8);
        Note($" {choice} en {clip.Id}/{mask}");
        SequenceEditCommand command = choice switch
        {
            0 => Edits.AddMask(s.SequenceId, clip.Id, _random.Next(2) == 0 ? MaskShape.Rectangle : MaskShape.Ellipse, _random.NextDouble(), _random.NextDouble(),
                _random.NextDouble(), _random.NextDouble(), ids: _ids),
            1 => Edits.AddMaskedZone(s.SequenceId, clip.Id, _random.Next(2) == 0 ? ZoneEffect.Blur : ZoneEffect.Pixelate, _ids),
            2 => Edits.SetMaskShapeKey(s.SequenceId, clip.Id, mask, at, _ids),
            3 => Edits.RemoveMaskShapeKey(s.SequenceId, clip.Id, mask, at, _ids),
            4 => Edits.DuplicateMask(s.SequenceId, clip.Id, mask, _ids),
            5 => Edits.ConvertMaskToBezier(s.SequenceId, clip.Id, mask, at, 16.0 / 9, _ids),
            6 => Edits.ClearMaskShapeKeys(s.SequenceId, clip.Id, mask, at, _ids),
            _ => Edits.ApplyMaskTrack(s.SequenceId, clip.Id, mask, MaskKeys(clip), (MaskTrackParts)_random.Next(1, 8), ids: _ids),
        };
        s.Execute(command);
    }

    private MaskTrackKey[] MaskKeys(Clip clip)
    {
        Rational rate = _session.Sequence.FrameRate;
        int n = _random.Next(1, 8);
        return [.. Enumerable.Range(0, n).Select(i => new MaskTrackKey(clip.SourceIn + Time.FromFrames(i, rate),
            _random.NextDouble(), _random.NextDouble(), 0.1 + _random.NextDouble(), 0.1 + _random.NextDouble(), _random.Next(-180, 181)))];
    }

    /// <summary>Un seguimiento nuevo con cuadros inventados, y su aplicación (seguir, anclar, máscara, estabilizar, zonas).</summary>
    private void TrackingOp()
    {
        EditorSession s = _session;
        if (RandomClip(c => c.Generator is null && !c.IsCompound && s.Project.FindMedia(c.MediaId) is { HasVideo: true }) is not { } clip)
        {
            return;
        }

        string? trackId = clip.Effects.FirstOrDefault(e => e.Type == BuiltInEffects.Track)?.Id;
        if (trackId is null || _random.Next(4) == 0)
        {
            var kind = (PointTrackKind)_random.Next(4);
            var create = TrackEdits.Create(s.SequenceId, clip.Id, kind, TrackEdits.DefaultPoints(kind, 16.0 / 9), "Seguimiento", TrackEdits.Palette[0]);
            Note($" nuevo {kind} en {clip.Id}");
            // Crear y seguir son dos comandos: en un gesto quedan en un paso, y aplicar queda para otra vez.
            s.BeginGesture("Seguir");
            try
            {
                if (!s.Execute(create))
                {
                    return;
                }

                trackId = create.CreatedIds[0];
                clip = s.Sequence.GetClip(clip.Id).Clip;
                EffectInstance track = clip.Effects.First(e => e.Id == trackId);
                int points = PointTrack.PointsOf(PointTrack.ParseKind(track.Options.GetValueOrDefault(BuiltInEffects.TrackOptions.Kind)));
                Rational rate = s.Sequence.FrameRate;
                int frames = _random.Next(2, 12);
                Time first = clip.ToEffectTime(clip.Start);
                var samples = new PointTrack(
                    [.. Enumerable.Range(0, frames).Select(i => first + Time.FromFrames(i, rate))],
                    [.. Enumerable.Range(0, frames).Select(_ => Enumerable.Range(0, points)
                        .Select(_ => new TrackSample(new Vector2((float)_random.NextDouble(), (float)_random.NextDouble()), _random.Next(6) == 0 ? 0 : 1)).ToArray())]);
                s.Execute(TrackEdits.SetSamples(s.SequenceId, clip.Id, trackId, samples, "Seguir"));
            }
            finally
            {
                s.EndGesture();
            }

            return;
        }

        var use = (TrackUse)_random.Next(Enum.GetValues<TrackUse>().Length);
        string? target = use switch
        {
            TrackUse.FollowPosition or TrackUse.FollowAll or TrackUse.CornerPin => RandomClip()?.Id,
            TrackUse.Mask => clip.Effects.FirstOrDefault(e => e.Type == BuiltInEffects.Mask)?.Id,
            _ => null,
        };
        TrackApplication? application = TrackApply.Prepare(s.Project, s.Sequence, clip, trackId, use, s.EditPosition, target, _random.Next(2) == 0, out string problem);
        Note($" aplicar {use} a {target ?? "-"}: {(application is null ? problem : "ok")}");
        if (application is not null)
        {
            s.Execute(application.Command);
        }
    }

    private void TitleOp()
    {
        EditorSession s = _session;
        switch (_random.Next(5))
        {
            case 0:
                s.AddTitleAtPlayhead();
                break;
            case 1:
            {
                Track? track = RandomTrack(TrackKind.Video);
                s.AddTitleAt(track?.Id, RandomInstant());
                break;
            }

            case 2:
                if (RandomClip(c => c.Generator is not null) is { } title)
                {
                    TextStyle style = TitleStyles.Default();
                    style.Size = _random.Next(1, 400);
                    Note($" estilo de {title.Id}");
                    s.ApplyTitleStyle(title.Id, style);
                }

                break;
            case 3:
                if (RandomClip(c => c.Generator is not null) is { } text)
                {
                    Note($" texto de {text.Id}");
                    s.Execute(Edits.SetTitleText(s.SequenceId, text.Id, _random.Next(4) == 0 ? "" : "Hola\nmundo " + _random.Next(100), _ids));
                }

                break;
            default:
                if (_random.Next(2) == 0)
                {
                    s.AddSubtitleTrack();
                }
                else if (RandomTrack(TrackKind.Video) is { } track)
                {
                    s.SetSubtitleRole(track.Id, _random.Next(2) == 0);
                }

                break;
        }
    }

    private void TransitionOp()
    {
        EditorSession s = _session;
        List<TransitionInstance> transitions = [.. s.Sequence.AllTracks.SelectMany(t => t.Transitions)];
        switch (_random.Next(transitions.Count == 0 ? 1 : 5))
        {
            case 0:
            {
                if (RandomClip() is { } clip)
                {
                    string type = EffectRegistry.Transitions[_random.Next(EffectRegistry.Transitions.Length)].Type;
                    Time? duration = _random.Next(3) == 0 ? Time.FromFrames(_random.Next(0, 90), s.Sequence.FrameRate) : null;
                    Note($" añadir {type} tras {clip.Id}");
                    s.Execute(Edits.AddTransition(s.SequenceId, clip.Id, type, duration, _ids));
                }

                break;
            }

            case 1:
            {
                TransitionInstance t = transitions[_random.Next(transitions.Count)];
                Note($" quitar {t.Id}");
                s.SelectTransition(t.Id);
                s.Execute(Edits.RemoveTransition(s.SequenceId, t.Id, _ids));
                break;
            }

            case 2:
            {
                TransitionInstance t = transitions[_random.Next(transitions.Count)];
                Note($" duración de {t.Id}");
                s.Execute(Edits.SetTransitionDuration(s.SequenceId, t.Id, Time.FromFrames(_random.Next(-5, 120), s.Sequence.FrameRate), _ids));
                break;
            }

            case 3:
            {
                TransitionInstance t = transitions[_random.Next(transitions.Count)];
                Note($" alineación de {t.Id}");
                s.Execute(Edits.SetTransitionAlignment(s.SequenceId, t.Id, (TransitionAlignment)_random.Next(Enum.GetValues<TransitionAlignment>().Length), _ids));
                break;
            }

            default:
            {
                TransitionInstance t = transitions[_random.Next(transitions.Count)];
                string type = EffectRegistry.Transitions[_random.Next(EffectRegistry.Transitions.Length)].Type;
                Note($" tipo de {t.Id} a {type}");
                s.Execute(Edits.SetTransitionType(s.SequenceId, t.Id, type, _ids));
                break;
            }
        }
    }

    private void MixerOp()
    {
        EditorSession s = _session;
        Sequence sequence = s.Sequence;
        List<string> owners = [.. sequence.AudioTracks.Select(t => t.Id), .. sequence.AudioBuses.Select(b => b.Id)];
        string owner = owners.Count == 0 ? "pista-falta" : owners[_random.Next(owners.Count)];
        Time at = RandomInstant();
        int choice = _random.Next(9);
        Note($" {choice} en {owner}");
        string[] faders = [BuiltInEffects.AudioParameters.GainDb, BuiltInEffects.AudioParameters.Pan, BuiltInEffects.AudioParameters.Balance];
        SequenceEditCommand command = choice switch
        {
            0 => Edits.AddBus(s.SequenceId, "Bus " + _random.Next(10), ids: _ids),
            1 when sequence.AudioBuses.Count > 0 => Edits.RemoveBus(s.SequenceId, sequence.AudioBuses[_random.Next(sequence.AudioBuses.Count)].Id, _ids),
            2 => Edits.SetTrackMix(s.SequenceId, owner, solo: _random.Next(2) == 0, armed: _random.Next(3) == 0, ids: _ids),
            3 => Edits.SoloExclusive(s.SequenceId, owner, _ids),
            4 when sequence.AudioBuses.Count > 0 => Edits.SetTrackOutput(s.SequenceId, owner, _random.Next(3) == 0 ? null : sequence.AudioBuses[_random.Next(sequence.AudioBuses.Count)].Id, _ids),
            5 when sequence.AudioBuses.Count > 0 => Edits.SetSend(s.SequenceId, owner, sequence.AudioBuses[_random.Next(sequence.AudioBuses.Count)].Id, _random.Next(-70, 13), _random.Next(2) == 0, ids: _ids),
            6 => Edits.SetBusState(s.SequenceId, owner, muted: _random.Next(2) == 0, solo: _random.Next(2) == 0, ids: _ids),
            7 => Edits.SetMasterGain(s.SequenceId, _random.Next(-80, 30), "Volumen de la mezcla", _ids),
            _ => Edits.SetFader(s.SequenceId, owner, faders[_random.Next(faders.Length)], _random.Next(-120, 120), at, _ids),
        };
        s.Execute(command);
    }

    // ------------------------------------------------------------------ elegir al azar

    private void RandomSelection()
    {
        EditorSession s = _session;
        List<Clip> clips = [.. s.Sequence.AllTracks.SelectMany(t => t.Clips)];
        switch (_random.Next(6))
        {
            case 0:
                s.ClearSelection();
                Note(" ninguno");
                return;
            case 1:
                s.SelectAll();
                Note(" todo");
                return;
            default:
            {
                if (clips.Count == 0)
                {
                    return;
                }

                int n = Math.Min(clips.Count, _random.Next(1, 4));
                List<string> ids = [.. Enumerable.Range(0, n).Select(_ => clips[_random.Next(clips.Count)].Id)];
                if (_random.Next(3) == 0)
                {
                    // Con sus enlazados, como un clic sin Alt.
                    ids.AddRange(clips.Where(c => c.LinkId is not null && ids.Any(id => s.Sequence.FindClip(id)?.Clip.LinkId == c.LinkId)).Select(c => c.Id));
                }

                s.Select(ids);
                Note($" [{string.Join(", ", s.SelectedClipIds)}]");
                return;
            }
        }
    }

    private Clip? RandomClip(Func<Clip, bool>? filter = null)
    {
        List<Clip> clips = [.. _session.Sequence.AllTracks.SelectMany(t => t.Clips).Where(c => filter?.Invoke(c) ?? true)];
        return clips.Count == 0 ? null : clips[_random.Next(clips.Count)];
    }

    private Track? RandomTrack(TrackKind? kind)
    {
        List<Track> tracks = [.. _session.Sequence.AllTracks.Where(t => kind is null || t.Kind == kind)];
        return tracks.Count == 0 ? null : tracks[_random.Next(tracks.Count)];
    }

    /// <summary>Casi siempre un borde o el medio de un clip; a veces cualquier instante, incluso después del final.</summary>
    private Time RandomInstant()
    {
        Sequence sequence = _session.Sequence;
        Rational rate = sequence.FrameRate;
        if (RandomClip() is { } clip && _random.Next(4) != 0)
        {
            Time at = (_random.Next(3) switch { 0 => clip.Start, 1 => clip.End, _ => clip.Start + Time.FromTicks(clip.Duration.Ticks / 2) })
                + Time.FromFrames(_random.Next(-2, 3), rate);
            return at < Time.Zero ? Time.Zero : at;
        }

        return Time.FromFrames(_random.Next((int)Math.Min(int.MaxValue - 100, sequence.Duration.ToFrames(rate) + 30)), rate);
    }

    /// <summary>Un instante en el tiempo de los efectos del dueño (el del origen en un clip, el de la secuencia en una pista).</summary>
    private Time RandomEffectTime(string owner)
    {
        Rational rate = _session.Sequence.FrameRate;
        if (_session.Sequence.FindClip(owner) is { } found)
        {
            Clip clip = found.Clip;
            Time timeline = clip.Start + Time.FromTicks((long)(clip.Duration.Ticks * _random.NextDouble()));
            return clip.ToEffectTime(timeline).SnapToFrame(rate);
        }

        return RandomInstant();
    }

    private EffectRef[] OwnerEffects(string owner)
    {
        Sequence sequence = _session.Sequence;
        List<EffectInstance>? effects = sequence.FindClip(owner)?.Clip.Effects ?? sequence.FindTrack(owner)?.Effects;
        return effects is null ? [] : [.. effects.Select(e => new EffectRef(e.Id, e.Type))];
    }

    /// <summary>Multicámaras con los medios de imagen y sonido de la biblioteca, con desfases inventados.</summary>
    private List<IReadOnlyList<MulticamAngle>> BuildMulticams()
    {
        List<MediaRef> media = _session.Project.Media.FindAll(m => m.HasVideo);
        if (media.Count < 2)
        {
            return [];
        }

        Rational rate = _session.Sequence.FrameRate;
        return
        [
            [.. media.Select((m, i) => new MulticamAngle(m.Id, Time.FromFrames(i == 0 ? 0 : _random.Next(-20, 21), rate)))],
        ];
    }

    private void Note(string text) => _log.Append(text);

    private sealed record EffectRef(string Id, string Type);
}
