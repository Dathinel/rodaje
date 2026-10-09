using System.Diagnostics;
using System.Globalization;
using System.Text;
using Rodaje.Core.Timing;
using Rodaje.Engine;
using Rodaje.Engine.Presentation;
using Rodaje.Playback;

namespace Rodaje.Visor;

/// <summary>
/// Recorrido sin persona para <c>--prueba</c>: reproduce, busca, avanza y retrocede cuadro a cuadro, y al final
/// imprime las estadísticas. Lo que mide es objetivo; si se ve fluido lo juzga Daniel (docs/prueba-visor.md).
/// </summary>
internal sealed class AutomatedRun
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(3);
    private static readonly int[] StepPlan = [1, 1, 1, 1, 1, -1, -1];

    private readonly TimeSpan _duration;
    private readonly string? _logPath;
    private readonly List<(double At, string Name, Action<ClipPlayer> Act)> _script;
    private readonly List<string> _log = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;
    private int _nextAction;
    private int _nextStep = -1;
    private long _expectedFrame = -1;
    private long _stepStarted;
    private int _stepsOk;
    private int _stepsFailed;

    public AutomatedRun(TimeSpan duration, string? logPath)
    {
        _duration = duration;
        _logPath = logPath;
        _script =
        [
            (0.25, "seek al 50 %", p => p.Seek(Time.FromSeconds(p.Duration.ToSeconds() * 0.5))),
            (0.40, "seek al 10 %", p => p.Seek(Time.FromSeconds(p.Duration.ToSeconds() * 0.1))),
            (0.55, "pausa y pasos de cuadro", p =>
            {
                p.Pause();
                _nextStep = 0;
            }),
            (0.72, "reproducir", p => p.Play()),
            (0.80, "Shift+→ (+5 s)", p => p.SeekBy(Time.FromSeconds(5))),
        ];
    }

    public void Update(VisorWindow window)
    {
        ClipPlayer? player = window.Player;
        if (player is null)
        {
            Finish(window, null, "No hay archivo abierto.");
            return;
        }

        double fraction = _clock.Elapsed / _duration;
        if (_nextStep >= 0)
        {
            AdvanceSteps(window, player);
        }
        else if (_nextAction < _script.Count && fraction >= _script[_nextAction].At)
        {
            (_, string name, Action<ClipPlayer> act) = _script[_nextAction++];
            long before = Stopwatch.GetTimestamp();
            act(player);
            double callMs = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            Note(Invariant($"{_clock.Elapsed.TotalSeconds,5:0.00} s  {name} (posición {Timecode.Format(player.Position)}; la orden tardó {callMs:0.0} ms)"));
        }

        if (fraction >= 1)
        {
            Finish(window, player, null);
        }
    }

    private void AdvanceSteps(VisorWindow window, ClipPlayer player)
    {
        if (_expectedFrame >= 0)
        {
            FramePresentation shown = window.LastPresentation;
            bool arrived = !player.IsSeekPending && shown.IsExact && shown.Frame?.Index == _expectedFrame;
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
                Note($"        paso de cuadro FALLÓ: se esperaba el {_expectedFrame} y se mostraba el {shown.Frame?.Index.ToString(CultureInfo.InvariantCulture) ?? "ninguno"}");
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
        _expectedFrame = Math.Clamp(player.CurrentFrame + delta, player.Timing.FirstFrame, player.Timing.LastFrame);
        _stepStarted = Stopwatch.GetTimestamp();
        player.StepFrame(delta);
    }

    private void Finish(VisorWindow window, ClipPlayer? player, string? failure)
    {
        double wall = _clock.Elapsed.TotalSeconds;
        double cpu = (Process.GetCurrentProcess().TotalProcessorTime - _cpuAtStart).TotalSeconds;
        var report = new StringBuilder();
        void Line(string text) => report.AppendLine(text);

        Line($"Rodaje visor: prueba automática de {_duration.TotalSeconds:0} s");
        if (player is null)
        {
            Line("RESULTADO: FALLÓ. " + failure);
            Write(report);
            window.RequestExit(1);
            return;
        }

        ClipPlayerStats stats = player.Stats;
        PlaybackStats p = stats.Playback;
        var v = player.Video;
        (int presents, double medianMs, double maxMs, int hitches) = window.Presents.Summarize();
        Line($"Archivo: {player.Path}");
        Line(Invariant($"Video: {v.Codec} {v.Width}x{v.Height} {v.PixelFormat} ({v.BitDepth} bits) a {v.FrameRate.ToDouble():0.###} fps, {player.FrameCount} cuadros, rotación {v.Rotation}°"));
        Line(player.Info.Audio is { } a ? $"Audio del archivo: {a.Codec} {a.SampleRate} Hz, {a.Channels} canales" : "Audio del archivo: ninguno");
        Line($"Decodificación: {stats.DecodeModeText} en {window.AdapterName}");
        Line($"Salida de audio: {stats.AudioStatus}");
        Line("Recorrido:");
        foreach (string entry in _log)
        {
            Line(entry);
        }

        Line(Invariant($"Cuadros mostrados: {p.FramesShown}; perdidos: {p.FramesDropped}; refrescos tarde: {p.LateRefreshes}"));
        Line(Invariant($"Desfase A/V máximo (fuera de los seeks): {p.MaxAvOffset.ToSeconds() * 1000:0.0} ms (un cuadro dura {1000 / v.FrameRate.ToDouble():0.0} ms)"));
        Line(Invariant($"Caché: {p.CacheHits} aciertos, {p.CacheMisses} fallos; errores de decodificación: {p.DecodeErrors + stats.Decoder.DecodeErrors}"));
        Line(Invariant($"Seeks: {stats.Seeks}; hasta ver el cuadro pedido: máx {stats.MaxSeekLatency.TotalMilliseconds:0} ms, promedio {stats.AverageSeekLatency.TotalMilliseconds:0} ms"));
        Line(Invariant($"Decodificador: {stats.Decoder.DecodedFrames} cuadros decodificados, {stats.Decoder.Seeks} búsquedas de keyframe"));
        Line(Invariant($"Presentaciones en reproducción: {presents}; intervalo mediano {medianMs:0.00} ms, máximo {maxMs:0.0} ms; refrescos perdidos: {hitches}"));
        Line(Invariant($"Audio: {stats.AudioUnderruns} faltas de audio decodificado; dispositivo: {stats.Audio.Underruns} rellenos con silencio, {stats.Audio.Starvations} vaciados del buffer"));
        Line(Invariant($"CPU del proceso: {cpu / wall * 100:0.0} % de un núcleo ({cpu / wall * 100 / Environment.ProcessorCount:0.0} % de los {Environment.ProcessorCount} hilos) durante {wall:0.0} s"));

        bool ok = _stepsFailed == 0 && _stepsOk == StepPlan.Length && p.DecodeErrors == 0;
        Line(ok ? "RESULTADO: correcto (la fluidez la juzga una persona: docs/prueba-visor.md)"
                : "RESULTADO: FALLÓ (ver los pasos de cuadro y los errores de arriba)");
        Write(report);
        window.RequestExit(ok ? 0 : 1);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

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
}
