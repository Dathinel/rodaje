using System.Diagnostics;
using System.Globalization;
using System.Text;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using static Rodaje.Visor.Win32;
using P = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Visor;

/// <summary>
/// Recorrido sin persona para <c>--prueba</c>: reproduce la secuencia, oculta y muestra una capa, mueve, escala y
/// gira otra con las mismas teclas que una persona, deshace y rehace, busca, avanza y retrocede cuadro a cuadro, y al
/// final imprime las estadísticas. Lo que mide es objetivo; si se ve fluido lo juzga Daniel (docs/prueba-visor.md).
/// </summary>
internal sealed class AutomatedRun
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(3);
    private static readonly int[] StepPlan = [1, 1, 1, 1, 1, -1, -1];

    private readonly TimeSpan _duration;
    private readonly string? _logPath;
    private readonly List<(double At, string Name, Func<VisorWindow, Check?> Act)> _script;
    private readonly List<string> _log = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;
    private int _nextAction;
    private Check? _check;
    private int _checksOk;
    private int _checksFailed;
    private int _nextStep = -1;
    private long _expectedFrame = -1;
    private long _stepStarted;
    private int _stepsOk;
    private int _stepsFailed;
    private LayerTransform _original;

    public AutomatedRun(TimeSpan duration, string? logPath)
    {
        _duration = duration;
        _logPath = logPath;
        _script =
        [
            (0.12, "ocultar la capa de arriba (tecla de su número)", w =>
            {
                NoteStats(w, "antes de editar (arranque y reproducción continua)");
                return ToggleTop(w, visible: false);
            }),
            (0.20, "mostrarla de nuevo", w => ToggleTop(w, visible: true)),
            (0.28, "seleccionarla (Ctrl+número) y moverla: Alt+→ ×3, Alt+↓ ×2", Move),
            (0.36, "escalar (+) y girar (R)", ScaleAndRotate),
            (0.44, "deshacer ×3 (Ctrl+Z)", UndoAll),
            (0.48, "rehacer el movimiento (Ctrl+Y)", Redo),
            (0.52, "seek al 50 %", w =>
            {
                NoteStats(w, "antes del primer seek (con las ediciones)");
                return Seek(w, 0.5);
            }),
            (0.60, "seek al 10 %", w => Seek(w, 0.1)),
            (0.68, "pausa y pasos de cuadro", w =>
            {
                w.Session!.Pause();
                _nextStep = 0;
                return null;
            }),
            (0.84, "reproducir", w =>
            {
                w.Session!.Play();
                return null;
            }),
            (0.90, "Shift+→ (+5 s)", w =>
            {
                w.PressKey(VK_RIGHT, KeyModifiers.Shift);
                return null;
            }),
        ];
    }

    public void Update(VisorWindow window)
    {
        TimelineSession? session = window.Session;
        if (session is null || window.Project is null)
        {
            Finish(window, "No hay nada abierto.");
            return;
        }

        double fraction = _clock.Elapsed / _duration;
        if (_check is not null)
        {
            AdvanceCheck(window);
        }
        else if (_nextStep >= 0)
        {
            AdvanceSteps(window, session);
        }
        else if (_nextAction < _script.Count && fraction >= _script[_nextAction].At)
        {
            (_, string name, Func<VisorWindow, Check?> act) = _script[_nextAction++];
            TimelineStats so = session.Stats;
            long before = Stopwatch.GetTimestamp();
            _check = act(window);
            double callMs = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            Note(Invariant($"{_clock.Elapsed.TotalSeconds,5:0.00} s  {name} (posición {Timecode.Format(session.Position)}; la orden tardó {callMs:0.0} ms; hasta aquí {so.FramesDropped} perdidos, {so.LateRefreshes} tarde y {window.Presents.Summarize().Hitches} refrescos perdidos)"));
            if (_check is not null)
            {
                _check.Started = before;
            }
        }

        if (fraction >= 1 && _check is null && _nextStep < 0)
        {
            Finish(window, null);
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static int TopTrack(VisorWindow w) => Math.Min(w.Project!.Sequence.VideoTracks.Count, 9) - 1;

    private static Clip SelectedClip(VisorWindow w) => w.Project!.Sequence.GetClip(w.SelectedClipId()!).Clip;

    private static LayerTransform ModelTransform(Clip clip) => new(
        LayerEdits.Read(clip, P.X),
        LayerEdits.Read(clip, P.Y),
        LayerEdits.Read(clip, P.Scale),
        LayerEdits.Read(clip, P.Rotation),
        LayerEdits.Read(clip, P.Opacity));

    private static bool Same(LayerTransform a, LayerTransform b) =>
        Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6 && Math.Abs(a.Scale - b.Scale) < 1e-9
        && Math.Abs(a.RotationDegrees - b.RotationDegrees) < 1e-6 && Math.Abs(a.Opacity - b.Opacity) < 1e-9;

    /// <summary>La capa seleccionada se ve con <paramref name="expected"/> y el modelo dice lo mismo.</summary>
    private static Check ShowsTransform(VisorWindow w, LayerTransform expected, string what)
    {
        int track = w.SelectedTrack;
        string clipId = w.SelectedClipId()!;
        return new Check(what, () =>
            w.LastFrame.Layer(track) is { } shown && shown.ClipId == clipId && Same(shown.Transform, expected)
            && Same(ModelTransform(SelectedClip(w)), expected));
    }

    private static Check? Seek(VisorWindow w, double fraction)
    {
        TimelineSession session = w.Session!;
        session.Seek(Time.FromSeconds(session.Duration.ToSeconds() * fraction));
        return null;
    }

    private static Check ToggleTop(VisorWindow w, bool visible)
    {
        int top = TopTrack(w);
        w.PressKey('1' + top);
        return new Check(visible ? $"V{top + 1} vuelve a verse" : $"V{top + 1} deja de verse",
            () => (w.LastFrame.Layer(top) is not null) == visible && w.Project!.Sequence.VideoTracks[top].Hidden != visible);
    }

    private Check Move(VisorWindow w)
    {
        w.PressKey('1' + TopTrack(w), KeyModifiers.Control);
        _original = ModelTransform(SelectedClip(w));
        for (int i = 0; i < 3; i++)
        {
            w.PressKey(VK_RIGHT, KeyModifiers.Alt);
        }

        for (int i = 0; i < 2; i++)
        {
            w.PressKey(VK_DOWN, KeyModifiers.Alt);
        }

        return ShowsTransform(w, _original with { X = _original.X + 60, Y = _original.Y + 40 }, "la capa se ve desplazada 60, 40 px");
    }

    private Check ScaleAndRotate(VisorWindow w)
    {
        w.PressKey(VK_OEM_PLUS);
        w.PressKey('R');
        LayerTransform expected = _original with
        {
            X = _original.X + 60,
            Y = _original.Y + 40,
            Scale = _original.Scale * LayerEdits.ScaleFactor,
            RotationDegrees = (_original.RotationDegrees + LayerEdits.RotationStep) % 360,
        };
        return ShowsTransform(w, expected, "la capa se ve escalada y girada");
    }

    private Check UndoAll(VisorWindow w)
    {
        for (int i = 0; i < 3; i++)
        {
            w.PressKey('Z', KeyModifiers.Control);
        }

        return ShowsTransform(w, _original, "la capa vuelve a su lugar original");
    }

    private Check Redo(VisorWindow w)
    {
        w.PressKey('Y', KeyModifiers.Control);
        return ShowsTransform(w, _original with { X = _original.X + 60, Y = _original.Y + 40 }, "el movimiento vuelve");
    }

    private void NoteStats(VisorWindow w, string when)
    {
        TimelineStats stats = w.Session!.Stats;
        (int presents, _, double maxMs, int hitches) = w.Presents.Summarize();
        Note(Invariant($"        {when}: {stats.FramesDropped} perdidos, {stats.LateRefreshes} refrescos tarde; {presents} presentaciones, máximo {maxMs:0.0} ms, {hitches} refrescos perdidos"));
    }

    private void AdvanceCheck(VisorWindow window)
    {
        Check check = _check!;
        double ms = Stopwatch.GetElapsedTime(check.Started).TotalMilliseconds;
        if (check.Done())
        {
            _checksOk++;
            Note(Invariant($"        {check.What}: en pantalla a los {ms:0} ms"));
            _check = null;
        }
        else if (ms > CheckTimeout.TotalMilliseconds)
        {
            _checksFailed++;
            ShownLayer? shown = window.LastFrame.Layer(window.SelectedTrack);
            Note(Invariant($"        FALLÓ: {check.What} (tras {ms:0} ms; capa en pantalla: {shown?.Transform.ToString() ?? "ninguna"})"));
            _check = null;
        }
    }

    private void AdvanceSteps(VisorWindow window, TimelineSession session)
    {
        if (_expectedFrame >= 0)
        {
            ShownFrame shown = window.LastFrame;
            bool baseExact = !window.Project!.IsDemo || shown.Layer(0)?.FrameIndex == _expectedFrame;
            bool arrived = !session.IsSeekPending && shown.SequenceFrame == _expectedFrame && baseExact;
            bool timedOut = Stopwatch.GetElapsedTime(_stepStarted) > StepTimeout;
            if (!arrived && !timedOut)
            {
                return;
            }

            if (arrived)
            {
                _stepsOk++;
            }
            else
            {
                _stepsFailed++;
                Note(Invariant($"        paso de cuadro FALLÓ: se esperaba el {_expectedFrame} y se mostraba el {shown.SequenceFrame} (capa de fondo: {shown.Layer(0)?.FrameIndex.ToString(CultureInfo.InvariantCulture) ?? "ninguna"})"));
            }

            _expectedFrame = -1;
            _nextStep++;
        }

        if (_nextStep >= StepPlan.Length)
        {
            Note($"        pasos de cuadro: {_stepsOk} de {StepPlan.Length} llegaron al cuadro exacto");
            _nextStep = -1;
            return;
        }

        int delta = StepPlan[_nextStep];
        _expectedFrame = Math.Clamp(session.CurrentFrame + delta, 0, session.FrameCount - 1);
        _stepStarted = Stopwatch.GetTimestamp();
        window.PressKey(delta > 0 ? VK_RIGHT : VK_LEFT);
    }

    private void Finish(VisorWindow window, string? failure)
    {
        double wall = _clock.Elapsed.TotalSeconds;
        double cpu = (Process.GetCurrentProcess().TotalProcessorTime - _cpuAtStart).TotalSeconds;
        var report = new StringBuilder();
        void Line(FormattableString text) => report.AppendLine(Invariant(text));

        Line($"Rodaje visor: prueba automática de {_duration.TotalSeconds:0} s");
        TimelineSession? session = window.Session;
        VisorProject? project = window.Project;
        if (session is null || project is null)
        {
            Line($"RESULTADO: FALLÓ. {failure}");
            Write(report);
            window.RequestExit(1);
            return;
        }

        Sequence sequence = project.Sequence;
        TimelineStats stats = session.Stats;
        (int presents, double medianMs, double maxMs, int hitches) = window.Presents.Summarize();
        GpuSummary gpu = window.GpuTimes.Summarize();
        (int compositeWidth, int compositeHeight) = window.CompositeSize;
        Line($"Proyecto: {(project.IsDemo ? "demostración armada con los videos" : project.SavePath)}");
        foreach (MediaRef media in project.Project.Media)
        {
            Line($"  {media.Name}: {media.Width}x{media.Height} a {media.FrameRate?.ToDouble() ?? 0:0.###} fps, {media.Duration.ToSeconds():0.0} s{(media.HasAudio ? ", con audio" : "")}");
        }

        Line($"Secuencia: {sequence.Width}x{sequence.Height} a {sequence.FrameRate.ToDouble():0.###} fps, {session.FrameCount} cuadros, {sequence.VideoTracks.Count} pistas de video; composición a {compositeWidth}x{compositeHeight} en {window.AdapterName}");
        Line($"Salida de audio: {stats.Audio.Status}");
        Line($"Recorrido:");
        foreach (string entry in _log)
        {
            report.AppendLine(entry);
        }

        Line($"Por capa:");
        foreach (LayerStats layer in stats.Layers)
        {
            Line($"  V{layer.TrackIndex + 1}: {layer.FramesShown} mostrados, {layer.FramesDropped} perdidos, {layer.LateRefreshes} refrescos tarde; cortes {layer.Cuts} (pre-roll {layer.PreRollHits} a tiempo, {layer.PreRollMisses} tarde)");
        }

        Line($"Total: {stats.FramesShown} mostrados, {stats.FramesDropped} perdidos, {stats.LateRefreshes} refrescos tarde; pre-roll {stats.PreRollHitRate * 100:0} %");
        Line($"Composición en la GPU: mediana {gpu.MedianMs:0.00} ms, máximo {gpu.MaxMs:0.00} ms ({gpu.Samples} muestras)");
        Line($"Decodificadores abiertos: {stats.DecodersOpen} (cerrados por desuso: {stats.DecodersClosed}); búsquedas de keyframe: {stats.DecoderSeeks}; seeks: {stats.Seeks}");
        Line($"Caché: {stats.CacheBytes / (1024.0 * 1024):0} MB en {stats.CacheFrames} cuadros (tope {stats.CacheBudgetBytes / (1024.0 * 1024):0} MB), {stats.CacheEvictions} expulsados");
        Line($"Errores de decodificación: {stats.DecodeErrors} (avisos: {window.DecodeFailures}{(window.LastDecodeFailure is { } last ? "; último: " + last : "")}); audio: {stats.Audio.Underruns} faltas, {stats.Audio.Errors} errores, {stats.Audio.DecodersOpen} decodificadores");
        Line($"Presentaciones en reproducción: {presents}; intervalo mediano {medianMs:0.00} ms, máximo {maxMs:0.0} ms; refrescos perdidos: {hitches}");
        Line($"CPU del proceso: {cpu / wall * 100:0.0} % de un núcleo ({cpu / wall * 100 / Environment.ProcessorCount:0.0} % de los {Environment.ProcessorCount} hilos) durante {wall:0.0} s");

        bool saved = CheckSaveAndReload(project, Line);
        bool ok = saved && failure is null && _checksFailed == 0 && _checksOk == 6 && _stepsFailed == 0 && _stepsOk == StepPlan.Length
            && stats.DecodeErrors == 0 && window.DecodeFailures == 0;
        Line($"Comprobaciones de edición: {_checksOk} de 6; pasos de cuadro: {_stepsOk} de {StepPlan.Length}");
        report.AppendLine(ok
            ? "RESULTADO: correcto (la fluidez la juzga una persona: docs/prueba-visor.md)"
            : "RESULTADO: FALLÓ (ver las comprobaciones, los pasos de cuadro y los errores de arriba)");
        Write(report);
        window.RequestExit(ok ? 0 : 1);
    }

    /// <summary>Guarda una copia junto al registro y comprueba que se vuelve a abrir idéntica.</summary>
    private bool CheckSaveAndReload(VisorProject project, Action<FormattableString> line)
    {
        if (_logPath is null)
        {
            return true;
        }

        string path = Path.ChangeExtension(_logPath, ProjectSerializer.FileExtension);
        try
        {
            string saved = ProjectSerializer.Serialize(project.SaveCopy(path));
            bool same = saved == ProjectSerializer.Serialize(VisorProject.Load(path).Project);
            line($"Guardar y volver a abrir: {(same ? "idéntico" : "FALLÓ, el proyecto cambió")} ({path})");
            return same;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectFormatException)
        {
            line($"Guardar y volver a abrir: FALLÓ ({ex.Message})");
            return false;
        }
    }

    private void Note(string text) => _log.Add(text);

    private void Write(StringBuilder report)
    {
        string text = report.ToString();
        Console.Out.Write(text);
        Console.Out.Flush();
        if (_logPath is not null)
        {
            File.WriteAllText(_logPath, text, new UTF8Encoding(false));
        }
    }

    private sealed class Check(string what, Func<bool> done)
    {
        public string What { get; } = what;

        public long Started { get; set; }

        public bool Done() => done();
    }
}
