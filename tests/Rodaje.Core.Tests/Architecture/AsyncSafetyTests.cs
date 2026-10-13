using System.Text;
using System.Text.RegularExpressions;

namespace Rodaje.Core.Tests.Architecture;

/// <summary>
/// Prueba de arquitectura sobre el texto de <c>src/**/*.cs</c>: que no vuelvan las excepciones perdidas ni los bloqueos
/// del hilo de la interfaz que encontró la auditoría (async void sin red, tareas sueltas, continuaciones que no miran el
/// fallo, esperas que bloquean, callbacks de DispatcherQueue sin red o encolados en bucle, hash e I/O grandes en el código
/// de la interfaz). Lee los fuentes como texto (sin compilar la app, que es de Windows), sin comentarios ni cadenas. Cada
/// excepción está en una lista explícita con su motivo y con cuántas apariciones cubre: una aparición más es un problema y
/// una menos deja la entrada vieja.
/// </summary>
public sealed partial class AsyncSafetyTests
{
    // Archivos que pueden llamar a DispatcherQueue.TryEnqueue sin la red de DispatcherQueueLater (RunLater, RunAfterEvent).
    private static readonly Dictionary<string, (int Count, string Reason)> RawTryEnqueueAllowed = new(StringComparer.Ordinal)
    {
        ["Rodaje.App/Views/Inspector/DispatcherQueueLater.cs"] = (2, "es la red: encola la acción ya envuelta por UiDeferral.Run (informe y aviso)."),
    };

    // async void que no son manejadores de eventos.
    // («archivo#Método» → motivo). Hoy ninguno: UiTask.Run observa con una continuación.
    private static readonly Dictionary<string, string> AsyncVoidAllowed = new(StringComparer.Ordinal);

    // Manejadores async void con algún await fuera de un try/catch (Exception) («archivo#Método» → motivo). Lo que se
    // escapa de ellos lo atrapa todavía GuardedSynchronizationContext (informe y aviso), pero sin el mensaje propio del
    // manejador: cada uno se arregla metiendo el await en el try y saliendo de la lista.
    private static readonly Dictionary<string, string> UncaughtAwaitAllowed = new(StringComparer.Ordinal)
    {
        ["Rodaje.App/Shell/TitleBar.xaml.cs#OnSaveTemplateClick"] = "DialogGate.ShowAsync antes del try (por arreglar).",
        ["Rodaje.App/Views/Plugins/PluginsView.xaml.cs#OnInstallClick"] = "DownloadModelsAsync antes del try (por arreglar).",
        ["Rodaje.App/Views/Export/ExportView.xaml.cs#OnSavePresetClick"] = "DialogGate.ShowAsync antes del try (por arreglar).",
        ["Rodaje.App/Views/Export/ExportView.Versions.cs#OnVersionsClick"] = "DialogGate.ShowAsync antes del try (por arreglar).",
        ["Rodaje.App/Views/Inspector/Properties/PropertiesPanel.xaml.cs#OnSavePresetClick"] = "DialogGate.ShowAsync antes del try (por arreglar).",
        ["Rodaje.App/Views/Inspector/Filters/FiltersPanel.Presets.cs#OnSavePresetClick"] = "DialogGate.ShowAsync antes del try (por arreglar).",
        ["Rodaje.App/Views/Inspector/Text/TextPanel.xaml.cs#OnSaveStyleClick"] = "DialogGate.ShowAsync antes del try (por arreglar).",
    };

    // Tareas descartadas con «_ =» en Rodaje.App: archivo → apariciones y motivo.
    private static readonly Dictionary<string, (int Count, string Reason)> DiscardAllowed = new(StringComparer.Ordinal)
    {
        ["Rodaje.App/Services/Autosave/AutosaveService.cs"] = (1, "EnqueueQuiet: los fallos de disco se atrapan dentro; uno inesperado queda en la fila sin observar (por arreglar)."),
    };

    // Esperas bloqueantes en el código de la interfaz (Views/, Shell/, App.xaml.cs): (archivo, texto) → apariciones y motivo.
    private static readonly Dictionary<(string File, string Pattern), (int Count, string Reason)> BlockingWaitAllowed = new()
    {
        [("Rodaje.App/Views/Player/PlayerEngine.Transitions.cs", ".Wait(TimeSpan")] = (1, "corre en el hilo del motor, no en el de la interfaz."),
        [("Rodaje.App/Views/Player/PlayerEngine.Looks.cs", ".Wait(TimeSpan")] = (1, "corre en el hilo del motor, no en el de la interfaz."),
        [("Rodaje.App/Shell/MainWindow.xaml.cs", ".Wait(TimeSpan")] = (1, "al cerrar: como mucho 3 s, y el cierre de los módulos no vuelve al hilo de la interfaz (ConfigureAwait(false))."),
    };

    // I/O o hash de archivos en el código de la interfaz: (archivo, texto) → apariciones y motivo.
    private static readonly Dictionary<(string File, string Pattern), (int Count, string Reason)> UiFileWorkAllowed = new()
    {
        [("Rodaje.App/Views/Analysis/StabilizationStore.cs", "File.ReadAllBytes(")] = (1, "un análisis en caché (KB), leído desde el análisis o el render, no desde la interfaz."),
        [("Rodaje.App/Views/Analysis/ClipVideoSource.cs", "SourceFingerprint.Compute(")] = (1, "corre en el análisis, fuera del hilo de la interfaz (como mucho 2 MiB)."),
    };

    // TryEnqueue dentro de un bucle: archivo → apariciones y motivo.
    private static readonly Dictionary<string, (int Count, string Reason)> EnqueueInLoopAllowed = new(StringComparer.Ordinal)
    {
        ["Rodaje.App/Shell/MeasurementRun.cs"] = (1, "solo con -Medir: mide la latencia de la cola con un aviso cada 50 ms."),
    };

    // Continuaciones sueltas (descartadas con «_ =» o pasadas a UiTask.Run) que no leen la excepción de su tarea:
    // archivo → apariciones y motivo.
    private static readonly Dictionary<string, (int Count, string Reason)> UnobservedContinuationAllowed = new(StringComparer.Ordinal)
    {
        ["Rodaje.App/Services/Crashes/UiTask.cs"] = (1, "es la red: la continuación pasa la tarea a Observe, que lee Exception."),
        ["Rodaje.App/Services/Modules/ModuleConnection.cs"] = (1, "Exited solo termina con TrySetResult (el código de salida): nunca falla."),
    };

    private static readonly string[] BlockingPatterns =
        [".GetAwaiter().GetResult()", ".Wait()", ".Wait(TimeSpan", "Task.WaitAll(", "Task.WaitAny(", ".AsTask().Result"];

    // Esperas que relanzan el fallo de la tarea: aun permitidas, van dentro de un try/catch (Exception).
    private static readonly string[] RethrowingWaits = [".GetAwaiter().GetResult()", ".Wait()", ".Wait(TimeSpan", "Task.WaitAll("];

    // Etiqueta de las lecturas de «.Result» de una tarea (en BlockingWaitAllowed).
    private const string ResultPattern = ".Result";

    private static readonly string[] UiFilePatterns =
        ["ComputeHash(", "IncrementalHash", "HashDataAsync(", "File.ReadAllBytes(", "File.Copy(", "ZipFile.", "SourceFingerprint.Compute("];

    [Fact]
    public void AsyncVoidOnlyInHandlersAndAlwaysWithTryCatch()
    {
        var problems = new List<string>();
        foreach ((string file, string code) in Sources())
        {
            foreach (Match m in AsyncVoidRegex().Matches(code))
            {
                string name = m.Groups[1].Value;
                int open = m.Index + m.Length - 1;
                int close = MatchingClose(code, open);
                string parameters = code[(open + 1)..close];
                string body = BodyAfter(code, close + 1);
                string where = $"{file}#{name}";
                bool allowed = AsyncVoidAllowed.ContainsKey(where);
                if (!allowed && !IsHandlerSignature(parameters))
                {
                    problems.Add($"{where}: async void fuera de un manejador de eventos (usa async Task y UiTask.Run).");
                }

                if (!AwaitsAreCaught(body) && !UncaughtAwaitAllowed.ContainsKey(where))
                {
                    problems.Add($"{where}: async void con un await fuera de un try/catch (Exception) (usa async Task y UiTask.Run, o atrapa y UiTask.Report).");
                }
            }

            foreach (Match m in AsyncEventLambdaRegex().Matches(code))
            {
                problems.Add($"{file}:{Line(code, m.Index)}: lambda async en un evento (async void sin red): «+= (s, e) => UiTask.Run(…)».");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void AppDoesNotDiscardTasks()
    {
        List<(string File, string Code)> all = Sources();
        HashSet<string> taskMethods = TaskReturningMethods(all.Select(s => s.Code));
        var problems = new List<string>();
        foreach ((string file, string code) in all.Where(s => s.File.StartsWith("Rodaje.App/", StringComparison.Ordinal)))
        {
            int allowed = DiscardAllowed.TryGetValue(file, out var entry) ? entry.Count : 0;
            foreach ((int index, string statement) in DiscardedTasks(code, taskMethods).Skip(allowed))
            {
                problems.Add($"{file}:{Line(code, index)}: «_ = {First(statement)}» pierde la excepción de la tarea: UiTask.Run(…).");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void LooseContinuationsObserveTheirTask()
    {
        // Una continuación que nadie espera solo observa su propio fallo (y con UiTask.Run): el de la tarea de origen se
        // pierde si la continuación no lee Exception ni lo informa con UiTask.
        var problems = new List<string>();
        foreach ((string file, string code) in Sources("Rodaje.App/"))
        {
            int found = UnobservedContinuations(code).Count;
            int allowed = UnobservedContinuationAllowed.TryGetValue(file, out var entry) ? entry.Count : 0;
            if (found > allowed)
            {
                foreach (int index in UnobservedContinuations(code).Skip(allowed))
                {
                    problems.Add($"{file}:{Line(code, index)}: continuación suelta que no mira el fallo de su tarea (lee t.Exception o informa con UiTask; o await).");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void UiCodeDoesNotBlockOnTasks()
    {
        List<(string File, string Code)> all = Sources();
        HashSet<string> taskMethods = TaskReturningMethods(all.Select(s => s.Code));
        var problems = new List<string>();
        foreach ((string file, string code) in all.Where(s => s.File.StartsWith("Rodaje.App/", StringComparison.Ordinal) && IsUiFile(s.File)))
        {
            foreach (string pattern in BlockingPatterns)
            {
                CheckCount(problems, file, code, pattern, Occurrences(code, pattern),
                    "bloquea (en el hilo de la interfaz, congela la ventana o se traba): await.");
            }

            CheckCount(problems, file, code, ResultPattern, TaskResultReads(code, taskMethods),
                "lee el resultado de una tarea y bloquea si no terminó (en el hilo de la interfaz, congela la ventana): await.");

            // Una espera permitida relanza el fallo de la tarea (AggregateException en Wait): sin try/catch salta lo que
            // sigue (al cerrar la ventana, liberar el motor y lo demás).
            List<(int Open, int Close)> guarded = BroadTryBlocks(code);
            foreach (string pattern in RethrowingWaits)
            {
                foreach (int i in Occurrences(code, pattern).Where(i => !guarded.Any(g => g.Open < i && i < g.Close)))
                {
                    problems.Add($"{file}:{Line(code, i)}: «{pattern}» relanza el fallo de la tarea: va dentro de un try/catch (Exception) que lo informe.");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        static void CheckCount(List<string> problems, string file, string code, string pattern, List<int> found, string why)
        {
            int allowed = BlockingWaitAllowed.TryGetValue((file, pattern), out var entry) ? entry.Count : 0;
            problems.AddRange(found.Skip(allowed).Select(i => $"{file}:{Line(code, i)}: «{pattern}» {why}"));
        }
    }

    [Fact]
    public void NoManualLockInAsyncCode()
    {
        // «lock» con un await adentro no compila; Monitor y ReaderWriterLockSlim sí, y se traban (otro hilo al volver).
        string[] patterns = ["Monitor.Enter(", "Monitor.TryEnter(", ".EnterWriteLock(", ".EnterReadLock(", ".EnterUpgradeableReadLock("];
        var problems = new List<string>();
        foreach ((string file, string code) in Sources())
        {
            if (!AwaitRegex().IsMatch(code))
            {
                continue;
            }

            foreach (string pattern in patterns.Where(p => code.Contains(p, StringComparison.Ordinal)))
            {
                problems.Add($"{file}: «{pattern}» en un archivo con await: un candado tomado no debe cruzar un await (SemaphoreSlim.WaitAsync).");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void UiCodeDoesNotHashOrCopyFiles()
    {
        var problems = new List<string>();
        foreach ((string file, string code) in Sources("Rodaje.App/").Where(s => IsUiFile(s.File)))
        {
            foreach (string pattern in UiFilePatterns)
            {
                int allowed = UiFileWorkAllowed.TryGetValue((file, pattern), out var entry) ? entry.Count : 0;
                foreach (int i in Occurrences(code, pattern).Skip(allowed))
                {
                    problems.Add($"{file}:{Line(code, i)}: «{pattern}» en el código de la interfaz: va a Services/ o a Logic/ y se llama con Task.Run.");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void DispatcherCallbacksAreGuardedAndNotQueuedInLoops()
    {
        var problems = new List<string>();
        foreach ((string file, string code) in Sources("Rodaje.App/"))
        {
            int rawAllowed = RawTryEnqueueAllowed.TryGetValue(file, out var raw) ? raw.Count : 0;
            foreach (Match m in RawTryEnqueueRegex().Matches(code).Skip(rawAllowed))
            {
                problems.Add($"{file}:{Line(code, m.Index)}: TryEnqueue sin red: si el callback lanza, la app se cierra (RunLater o RunAfterEvent).");
            }

            int loopAllowed = EnqueueInLoopAllowed.TryGetValue(file, out var loop) ? loop.Count : 0;
            foreach (int index in EnqueuesInLoops(code).Skip(loopAllowed))
            {
                problems.Add($"{file}:{Line(code, index)}: TryEnqueue dentro de un bucle llena la cola de la interfaz sin límite (CoalescedDispatch).");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void AllowListsPointAtExistingCode()
    {
        // Una excepción que ya no hace falta se quita de la lista, y una que cubre de más se ajusta: si no, tapa lo que
        // vuelva a aparecer ahí.
        Dictionary<string, string> sources = Sources().ToDictionary(s => s.File, s => s.Code, StringComparer.Ordinal);
        HashSet<string> taskMethods = TaskReturningMethods(sources.Values);
        var stale = new List<string>();
        string Code(string file) => sources.GetValueOrDefault(file, "");
        void Expect(string what, int count, int found)
        {
            if (found < count)
            {
                stale.Add($"{what} (cubre {count}, hay {found})");
            }
        }

        foreach ((string file, (int count, _)) in RawTryEnqueueAllowed)
        {
            Expect(file, count, RawTryEnqueueRegex().Count(Code(file)));
        }

        foreach (string where in AsyncVoidAllowed.Keys)
        {
            string[] parts = where.Split('#');
            if (!Code(parts[0]).Contains("async void " + parts[1], StringComparison.Ordinal))
            {
                stale.Add(where);
            }
        }

        foreach (((string file, string pattern), (int count, _)) in BlockingWaitAllowed)
        {
            Expect($"{file} «{pattern}»", count,
                pattern == ResultPattern ? TaskResultReads(Code(file), taskMethods).Count : Occurrences(Code(file), pattern).Count);
        }

        foreach (((string file, string pattern), (int count, _)) in UiFileWorkAllowed)
        {
            Expect($"{file} «{pattern}»", count, Occurrences(Code(file), pattern).Count);
        }

        foreach ((string file, (int count, _)) in EnqueueInLoopAllowed)
        {
            Expect(file, count, EnqueuesInLoops(Code(file)).Count);
        }

        foreach ((string file, (int count, _)) in UnobservedContinuationAllowed)
        {
            Expect(file, count, UnobservedContinuations(Code(file)).Count);
        }

        foreach ((string file, (int count, _)) in DiscardAllowed)
        {
            Expect(file, count, DiscardedTasks(Code(file), taskMethods).Count);
        }

        foreach (string where in UncaughtAwaitAllowed.Keys)
        {
            string[] parts = where.Split('#');
            string code = Code(parts[0]);
            Match m = Regex.Match(code, @"\basync\s+void\s+" + Regex.Escape(parts[1]) + @"\s*\(");
            if (!m.Success || AwaitsAreCaught(BodyAfter(code, MatchingClose(code, m.Index + m.Length - 1) + 1)))
            {
                stale.Add(where);
            }
        }

        Assert.True(stale.Count == 0, "Excepciones que ya no aplican: " + string.Join(", ", stale));
    }

    [Fact]
    public void AllowListsUseFullPathsAndGiveAReason()
    {
        IEnumerable<(string File, string Reason)> entries = RawTryEnqueueAllowed.Select(e => (e.Key, e.Value.Reason))
            .Concat(BlockingWaitAllowed.Select(e => (e.Key.File, e.Value.Reason)))
            .Concat(UiFileWorkAllowed.Select(e => (e.Key.File, e.Value.Reason)))
            .Concat(EnqueueInLoopAllowed.Select(e => (e.Key, e.Value.Reason)))
            .Concat(UnobservedContinuationAllowed.Select(e => (e.Key, e.Value.Reason)))
            .Concat(DiscardAllowed.Select(e => (e.Key, e.Value.Reason)))
            .Concat(UncaughtAwaitAllowed.Select(e => (e.Key.Split('#')[0], e.Value)))
            .Concat(AsyncVoidAllowed.Select(e => (e.Key.Split('#')[0], e.Value)));
        foreach ((string file, string reason) in entries)
        {
            Assert.StartsWith("Rodaje.", file, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(reason), file);
        }

        Assert.All(RawTryEnqueueAllowed.Values.Concat(BlockingWaitAllowed.Values).Concat(UiFileWorkAllowed.Values)
            .Concat(EnqueueInLoopAllowed.Values).Concat(UnobservedContinuationAllowed.Values).Concat(DiscardAllowed.Values), e => Assert.True(e.Count > 0));
    }

    [Fact]
    public void StripRemovesCommentsAndStrings()
    {
        string code = """
            // async void A(int x) {}
            /* _ = B.RunAsync(); */
            string s = "async void C(int y) {}" + $"{x} _ = D.RunAsync();" + @"x""y" + 'a' + '"';
            async void E(object sender, RoutedEventArgs e) { }
            """;
        string stripped = Strip(code);
        Assert.Single(AsyncVoidRegex().Matches(stripped));
        Assert.DoesNotMatch(DiscardRegex(), stripped);
        Assert.Equal(code.Length, stripped.Length);
    }

    [Fact]
    public void LoopDetectionSkipsHandlersSubscribedInTheLoop()
    {
        const string InLoop = "foreach (var x in xs) { queue.RunLater(() => Show(x)); }";
        const string InHandler = "foreach (var x in xs) { box.SelectionChanged += (_, _) => { if (ok) { queue.RunAfterEvent(() => Set(x)); } }; }";
        const string LoopInHandler = "box.Changed += (_, _) => { while (more) { queue.TryEnqueue(Next); } };";
        const string EndlessFor = "for (;;) { queue.RunLater(Next); }";
        const string FullFor = "for (int i = 0; i < n; i++) { queue.RunLater(Next); }";
        const string NoBraces = "foreach (var x in xs) queue.RunLater(() => Show(x));";
        const string NoBracesInIf = "if (ok) { while (more) queue.TryEnqueue(Next); }";
        const string HandlerInFor = "for (int i = 0; i < n; i++) { box.Changed += (_, _) => queue.RunLater(Next); }";
        const string BlockHandlerNoBraces = "foreach (var x in xs) box.Changed += (_, _) => { queue.RunLater(Next); };";
        const string AfterLoop = "foreach (var x in xs) Add(x); queue.RunLater(Next);";
        const string AfterForHeader = "for (int i = 0; i < n; i++) { } queue.RunLater(Next);";
        Assert.True(InsideLoop(InLoop, InLoop.IndexOf(".RunLater", StringComparison.Ordinal)));
        Assert.False(InsideLoop(InHandler, InHandler.IndexOf(".RunAfterEvent", StringComparison.Ordinal)));
        Assert.True(InsideLoop(LoopInHandler, LoopInHandler.IndexOf(".TryEnqueue", StringComparison.Ordinal)));
        Assert.True(InsideLoop(EndlessFor, EndlessFor.IndexOf(".RunLater", StringComparison.Ordinal)));
        Assert.True(InsideLoop(FullFor, FullFor.IndexOf(".RunLater", StringComparison.Ordinal)));
        Assert.True(InsideLoop(NoBraces, NoBraces.IndexOf(".RunLater", StringComparison.Ordinal)));
        Assert.True(InsideLoop(NoBracesInIf, NoBracesInIf.IndexOf(".TryEnqueue", StringComparison.Ordinal)));
        Assert.False(InsideLoop(HandlerInFor, HandlerInFor.IndexOf(".RunLater", StringComparison.Ordinal)));
        Assert.False(InsideLoop(BlockHandlerNoBraces, BlockHandlerNoBraces.IndexOf(".RunLater", StringComparison.Ordinal)));
        Assert.False(InsideLoop(AfterLoop, AfterLoop.IndexOf(".RunLater", StringComparison.Ordinal)));
        Assert.False(InsideLoop(AfterForHeader, AfterForHeader.IndexOf(".RunLater", StringComparison.Ordinal)));
    }

    [Fact]
    public void ContinuationCheckSeesTheOriginTask()
    {
        // La forma del defecto de la copia directa del diálogo de exportar: UiTask.Run observa la continuación, no Task.Run.
        Assert.Single(UnobservedContinuations("UiTask.Run(Task.Run(Work).ContinueWith(task => { if (task.IsCompletedSuccessfully) Show(task.Result); }, ui));"));
        Assert.Single(UnobservedContinuations("_ = process.Exited.ContinueWith(_ => OnExited(process), CancellationToken.None);"));
        Assert.Single(UnobservedContinuations("_ = x.Ready\n    .ContinueWith(_ => Wake());"));
        Assert.Empty(UnobservedContinuations("_ = current.RequestProxyAsync(path).ContinueWith(static t => _ = t.Exception, CancellationToken.None);"));
        Assert.Empty(UnobservedContinuations("UiTask.Run(ready.ContinueWith(t => { if (t.Exception is { } e) UiTask.Report(e); }));"));
        // Una cadena guardada en un campo no es suelta: la espera quien la tiene.
        Assert.Empty(UnobservedContinuations("_queue = _queue.ContinueWith(_ => work(), CancellationToken.None);"));
    }

    [Fact]
    public void DiscardCheckSeesTasksWithoutTheAsyncSuffix()
    {
        HashSet<string> methods = TaskReturningMethods(["public Task Refresh(int x) => Task.CompletedTask;", "private static ValueTask<bool> Probe() => default;", "public int Count() => 0;"]);
        Assert.True(IsDiscardedTask(" Task.Delay(100)", methods));
        Assert.True(IsDiscardedTask(" Task.WhenAll(a, b)", methods));
        Assert.True(IsDiscardedTask(" connection.CloseAsync()", methods));
        Assert.True(IsDiscardedTask(" _service.Refresh(3)", methods));
        Assert.True(IsDiscardedTask(" Probe()", methods));
        Assert.True(IsDiscardedTask(" x.DisposeAsync().AsTask()", methods));
        Assert.False(IsDiscardedTask(" list.Count()", methods));
        Assert.False(IsDiscardedTask(" dictionary.Remove(key)", methods));
    }

    [Fact]
    public void ResultCheckSeesTaskVariablesAndCalls()
    {
        HashSet<string> methods = TaskReturningMethods(["Task<int> Load() => Task.FromResult(1);"]);
        Assert.Single(TaskResultReads(Strip("var t = Task.Run(Work); int n = t.Result;"), methods));
        Assert.Single(TaskResultReads(Strip("Task<int> pending = Load(); Use(pending.Result);"), methods));
        Assert.Single(TaskResultReads(Strip("x.ContinueWith(done => Show(done.Result));"), methods));
        Assert.Single(TaskResultReads(Strip("int n = Load().Result;"), methods));
        Assert.Single(TaskResultReads(Strip("int n = ReadCountAsync(path).Result;"), methods));
        Assert.Empty(TaskResultReads(Strip("var d = Translate(); Use(d.Result.Texts);"), methods));
        Assert.Empty(TaskResultReads(Strip("int n = await Task.Run(Work); Use(outcome.Result);"), methods));
    }

    [Fact]
    public void TryCatchCheckNeedsEveryAwaitCaught()
    {
        Assert.True(AwaitsAreCaught("{ if (x) { return; } try { await A(); } catch (Exception ex) when (UiTask.Report(ex)) { } }"));
        Assert.True(AwaitsAreCaught("{ try { await A(); } catch (IOException) { } catch { } }"));
        Assert.True(AwaitsAreCaught("{ try { await A(); }\n#pragma warning disable CA1031\n catch (Exception ex)\n#pragma warning restore CA1031\n { } }"));
        Assert.True(AwaitsAreCaught("{ try { try { await A(); } finally { B(); } } catch (System.Exception) { } }"));
        Assert.False(AwaitsAreCaught("{ try { await A(); } finally { B(); } try { C(); } catch (Exception) { } }"));
        Assert.False(AwaitsAreCaught("{ try { await A(); } catch (Exception) { } await B(); }"));
        Assert.False(AwaitsAreCaught("{ try { await A(); } catch (IOException) { } }"));
        Assert.False(AwaitsAreCaught("{ Work(); }"));
        Assert.True(AwaitsAreCaught("{ try { Work(); } catch (Exception) { } }"));
    }

    // --- Lectura de los fuentes ---

    private static List<(string File, string Code)> Sources(string prefix = "")
    {
        string src = Path.Combine(RepoRoot(), "src");
        var list = new List<(string, string)>();
        foreach (string path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(src, path).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)
                || !relative.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            list.Add((relative, Strip(File.ReadAllText(path))));
        }

        Assert.NotEmpty(list);
        return list;
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No se encontró la raíz del repositorio (Rodaje.slnx).");
    }

    private static bool IsUiFile(string file) =>
        (file.StartsWith("Rodaje.App/Views/", StringComparison.Ordinal) || file.StartsWith("Rodaje.App/Shell/", StringComparison.Ordinal)
            || file == "Rodaje.App/App.xaml.cs")
        && !file.Contains("/Logic/", StringComparison.Ordinal);

    // Manejador: el último parámetro es de un tipo «…Args» (EventArgs, DragEventArgs, ContentDialogButtonClickEventArgs…).
    private static bool IsHandlerSignature(string parameters)
    {
        string last = parameters.Split(',').Last().Trim();
        int space = last.LastIndexOf(' ');
        string type = space > 0 ? last[..space].Trim() : last;
        return type.TrimEnd('?').EndsWith("Args", StringComparison.Ordinal);
    }

    // El cuerpo de un método desde después de sus parámetros: un bloque { … } o una expresión «=> …;».
    private static string BodyAfter(string code, int index)
    {
        int brace = code.IndexOf('{', index);
        int arrow = code.IndexOf("=>", index, StringComparison.Ordinal);
        if (arrow >= 0 && (brace < 0 || arrow < brace))
        {
            return Statement(code, arrow + 2);
        }

        return code[brace..(MatchingClose(code, brace) + 1)];
    }

    // Desde index hasta el «;» que cierra la instrucción (sin contar los de lambdas o llaves anidadas).
    private static string Statement(string code, int index)
    {
        int depth = 0;
        for (int i = index; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (--depth < 0)
                    {
                        return code[index..i];
                    }

                    break;
                case ';' when depth == 0:
                    return code[index..i];
            }
        }

        return code[index..];
    }

    private static int MatchingClose(string code, int open)
    {
        char opening = code[open];
        char closing = opening switch { '(' => ')', '[' => ']', _ => '}' };
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == opening)
            {
                depth++;
            }
            else if (code[i] == closing && --depth == 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Sin cierre para «{opening}» en la posición {open}.");
    }
    // Si la posición cae dentro de un bucle (for, foreach, while o do; con llaves o sin ellas, también dentro de una
    // lambda en él). Un «;» dentro de paréntesis (el encabezado de un for) no corta la instrucción.
    private static bool InsideLoop(string code, int index)
    {
        var headers = new Stack<(string Header, int Parens, int Start)>();
        int statementStart = 0;
        int parens = 0;
        for (int i = 0; i < index; i++)
        {
            switch (code[i])
            {
                case '(':
                    parens++;
                    break;
                case ')':
                    parens--;
                    break;
                case '{':
                    headers.Push((code[statementStart..i], parens, statementStart));
                    parens = 0;
                    statementStart = i + 1;
                    break;
                case '}':
                    if (headers.Count > 0)
                    {
                        // Un bloque dentro de paréntesis (una lambda pasada como argumento) sigue la instrucción de fuera.
                        (_, int outerParens, int outerStart) = headers.Pop();
                        parens = outerParens;
                        statementStart = outerParens > 0 ? outerStart : i + 1;
                    }
                    else
                    {
                        statementStart = i + 1;
                    }

                    break;
                case ';' when parens == 0:
                    statementStart = i + 1;
                    break;
            }
        }

        // De dentro hacia fuera, empezando por la instrucción en curso (un bucle sin llaves): una lambda (un manejador
        // suscrito en el bucle) corre después, no en cada vuelta.
        foreach (string text in headers.Select(h => h.Header).Prepend(code[statementStart..index]))
        {
            switch (Enclosing(text))
            {
                case Scope.Loop:
                    return true;
                case Scope.Lambda:
                    return false;
            }
        }

        return false;
    }

    private enum Scope
    {
        None,
        Loop,
        Lambda,
    }

    // Lo último que sigue abierto al final del texto: un encabezado de bucle o una flecha de lambda (lo que queda dentro de
    // paréntesis o llaves ya cerrados no cuenta).
    private static Scope Enclosing(string text)
    {
        var open = new List<(int Depth, Scope Kind)>();
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
                open.RemoveAll(e => e.Depth > depth);
            }
            else if (c == '=' && Next(text, i) == '>')
            {
                open.Add((depth, Scope.Lambda));
                i++;
            }
            else if (char.IsLetter(c) && (i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] is '_' or '.' or '@'))
                && LoopKeywordRegex().IsMatch(text.AsSpan(), i))
            {
                open.Add((depth, Scope.Loop));
            }
        }

        return open.Count == 0 ? Scope.None : open[^1].Kind;
    }

    // Los «_ = tarea» del archivo (las continuaciones las mira LooseContinuationsObserveTheirTask).
    private static List<(int Index, string Statement)> DiscardedTasks(string code, HashSet<string> taskMethods)
    {
        var found = new List<(int, string)>();
        foreach (Match m in DiscardRegex().Matches(code))
        {
            string statement = Statement(code, m.Index + m.Length);
            if (!statement.Contains(".ContinueWith(", StringComparison.Ordinal) && IsDiscardedTask(statement, taskMethods))
            {
                found.Add((m.Index, statement));
            }
        }

        return found;
    }

    private static List<int> EnqueuesInLoops(string code) =>
        [.. AnyTryEnqueueRegex().Matches(code).Select(m => m.Index).Where(i => InsideLoop(code, i))];

    private static List<int> Occurrences(string code, string pattern)
    {
        var found = new List<int>();
        for (int i = code.IndexOf(pattern, StringComparison.Ordinal); i >= 0; i = code.IndexOf(pattern, i + 1, StringComparison.Ordinal))
        {
            found.Add(i);
        }

        return found;
    }

    // --- Tareas en el texto ---

    // Los métodos de src/ declarados con Task o ValueTask (para ver tareas sin el sufijo Async).
    private static HashSet<string> TaskReturningMethods(IEnumerable<string> codes)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string code in codes)
        {
            foreach (Match m in TaskMethodRegex().Matches(code))
            {
                names.Add(m.Groups[1].Value);
            }
        }

        return names;
    }

    // Si la expresión (sin await) da una tarea: su última llamada es un «…Async(…)», un método de src/ que devuelve Task,
    // AsTask() o Task.Run, Task.Delay, Task.WhenAll, Task.WhenAny o Task.Factory.StartNew.
    private static bool IsDiscardedTask(string expression, HashSet<string> taskMethods)
    {
        if (AwaitStartRegex().IsMatch(expression) || LastCall(expression) is not { } call)
        {
            return false;
        }

        return call.Name.EndsWith("Async", StringComparison.Ordinal) || call.Name == "AsTask" || taskMethods.Contains(call.Name)
            || TaskFactoryCallRegex().IsMatch(call.Chain);
    }

    // La última llamada de la expresión fuera de paréntesis (la que da su tipo): su nombre y la cadena «a.b.Nombre».
    private static (string Name, string Chain)? LastCall(string expression)
    {
        string text = expression.TrimEnd();
        if (!text.EndsWith(')'))
        {
            return null;
        }

        int open = MatchingOpen(text, text.Length - 1);
        if (open < 0)
        {
            return null;
        }

        int end = open;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        int start = end;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '.' or '?' or '!' || char.IsWhiteSpace(text[start - 1])))
        {
            start--;
        }

        string chain = string.Concat(text[start..end].Where(c => !char.IsWhiteSpace(c)));
        int dot = chain.LastIndexOf('.');
        string name = dot >= 0 ? chain[(dot + 1)..] : chain;
        return name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') ? null : (name, chain);
    }

    private static int MatchingOpen(string code, int close)
    {
        char closing = code[close];
        char opening = closing switch { ')' => '(', ']' => '[', _ => '{' };
        int depth = 0;
        for (int i = close; i >= 0; i--)
        {
            if (code[i] == closing)
            {
                depth++;
            }
            else if (code[i] == opening && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    // Continuaciones sueltas (la última llamada de un «_ = …» o el argumento de UiTask.Run) cuyos argumentos no leen
    // Exception ni llaman a UiTask: la posición de cada «.ContinueWith(».
    private static List<int> UnobservedContinuations(string code)
    {
        var loose = new List<(int Start, int End)>();
        foreach (Match m in DiscardRegex().Matches(code))
        {
            int start = m.Index + m.Length;
            loose.Add((start, start + Statement(code, start).Length));
        }

        foreach (Match m in UiTaskRunRegex().Matches(code))
        {
            int open = m.Index + m.Length - 1;
            loose.Add((open + 1, MatchingClose(code, open)));
        }

        var found = new SortedSet<int>();
        foreach ((int start, int end) in loose)
        {
            string text = code[start..end];
            if (LastCall(text) is not { Name: "ContinueWith" })
            {
                continue;
            }

            int close = start + text.TrimEnd().Length - 1;
            int open = MatchingOpen(code, close);
            string arguments = code[(open + 1)..close];
            if (!arguments.Contains(".Exception", StringComparison.Ordinal) && !arguments.Contains("UiTask.", StringComparison.Ordinal))
            {
                found.Add(code.LastIndexOf(".ContinueWith", open, StringComparison.Ordinal));
            }
        }

        return [.. found];
    }

    // Lecturas de «.Result» de una tarea: de una variable o parámetro que es una tarea (declarado Task<…>, «var x =» una
    // tarea sin await o el parámetro de un ContinueWith) o de una llamada que da una tarea. «.AsTask().Result» va aparte.
    private static List<int> TaskResultReads(string code, HashSet<string> taskMethods)
    {
        var variables = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in TaskVariableRegex().Matches(code))
        {
            variables.Add(m.Groups[1].Value);
        }

        foreach (Match m in VarAssignmentRegex().Matches(code))
        {
            int start = m.Index + m.Length;
            if (IsDiscardedTask(Statement(code, start), taskMethods))
            {
                variables.Add(m.Groups[1].Value);
            }
        }

        foreach (Match m in ContinuationParameterRegex().Matches(code))
        {
            variables.Add(m.Groups[1].Value);
        }

        var found = new List<int>();
        foreach (Match m in ResultReadRegex().Matches(code))
        {
            int dot = m.Index + m.Length - ".Result".Length;
            if (m.Groups[1].Success)
            {
                if (variables.Contains(m.Groups[1].Value))
                {
                    found.Add(dot);
                }

                continue;
            }

            int close = code.LastIndexOf(')', dot);
            int open = MatchingOpen(code, close);
            string call = code[Math.Max(0, open - 200)..(close + 1)];
            if (open >= 0 && LastCall(call) is { } last && last.Name != "AsTask" && IsDiscardedTask(call, taskMethods))
            {
                found.Add(dot);
            }
        }

        return found;
    }

    // Si cada await del cuerpo está dentro de un try con un catch que atrapa cualquier excepción (sin tipo, Exception o
    // System.Exception, con o sin when); sin await, si hay al menos un try así.
    private static bool AwaitsAreCaught(string body)
    {
        List<(int Open, int Close)> guarded = BroadTryBlocks(body);
        List<int> awaits = [.. AwaitRegex().Matches(body).Select(m => m.Index)];
        return awaits.Count == 0 ? guarded.Count > 0 : awaits.All(a => guarded.Any(g => g.Open < a && a < g.Close));
    }

    // Los bloques try (llave de apertura y de cierre) que tienen un catch para cualquier excepción.
    private static List<(int Open, int Close)> BroadTryBlocks(string code)
    {
        var guarded = new List<(int Open, int Close)>();
        foreach (Match m in TryBlockRegex().Matches(code))
        {
            int open = m.Index + m.Length - 1;
            int close = MatchingClose(code, open);
            if (HasBroadCatch(code, close + 1))
            {
                guarded.Add((open, close));
            }
        }

        return guarded;
    }

    // Las cláusulas catch y finally que siguen a un bloque try: si alguna atrapa cualquier excepción.
    private static bool HasBroadCatch(string code, int index)
    {
        bool broad = false;
        while (true)
        {
            // Entre las cláusulas puede haber blancos y directivas (#pragma warning disable CA1031).
            while (index < code.Length && (char.IsWhiteSpace(code[index]) || code[index] == '#'))
            {
                index = code[index] == '#' ? Math.Max(code.IndexOf('\n', index), index + 1) : index + 1;
            }

            Match clause = CatchClauseRegex().Match(code, index);
            if (!clause.Success)
            {
                return broad;
            }

            index += clause.Length;
            if (clause.Groups[1].Value == "catch" && code[index - 1] == '(')
            {
                int close = MatchingClose(code, index - 1);
                string type = code[index..close].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                broad |= type is "Exception" or "System.Exception";
                index = close + 1;
                Match filter = WhenRegex().Match(code, index);
                if (filter.Success)
                {
                    index = MatchingClose(code, index + filter.Length - 1) + 1;
                }
            }
            else
            {
                broad |= clause.Groups[1].Value == "catch";
            }

            int brace = code.IndexOf('{', index);
            if (brace < 0)
            {
                return broad;
            }

            index = MatchingClose(code, brace) + 1;
        }
    }

    private static string First(string statement) => statement.Trim().Split('\n')[0].Trim();

    private static int Line(string code, int index) => 1 + code.AsSpan(0, index).Count('\n');

    /// <summary>El código con los comentarios y el contenido de cadenas y caracteres en blanco (mismo largo y líneas).</summary>
    internal static string Strip(string code)
    {
        var sb = new StringBuilder(code);
        int i = 0;
        while (i < code.Length)
        {
            char c = code[i];
            if (c == '/' && Next(code, i) == '/')
            {
                int end = code.IndexOf('\n', i);
                i = Blank(sb, i, end < 0 ? code.Length : end);
            }
            else if (c == '/' && Next(code, i) == '*')
            {
                int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = Blank(sb, i, end < 0 ? code.Length : end + 2);
            }
            else if (c == '\'')
            {
                int j = i + 1;
                while (j < code.Length && code[j] != '\'')
                {
                    j += code[j] == '\\' ? 2 : 1;
                }

                i = Blank(sb, i + 1, j) + 1;
            }
            else if (c == '"' || ((c is '@' or '$') && StringStartsAt(code, i)))
            {
                i = SkipString(code, sb, i);
            }
            else
            {
                i++;
            }
        }

        return sb.ToString();
    }

    private static bool StringStartsAt(string code, int i)
    {
        int j = i;
        while (j < code.Length && code[j] is '@' or '$')
        {
            j++;
        }

        return j > i && j < code.Length && code[j] == '"' && (i == 0 || !char.IsLetterOrDigit(code[i - 1]));
    }

    // Una cadena desde su prefijo (@, $, $@, $$…) hasta su cierre; deja las comillas y borra el contenido.
    private static int SkipString(string code, StringBuilder sb, int i)
    {
        bool verbatim = false;
        while (code[i] is '@' or '$')
        {
            verbatim |= code[i] == '@';
            i++;
        }

        int quotes = 0;
        while (i + quotes < code.Length && code[i + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            // Cadena cruda: termina con las mismas comillas.
            string fence = new('"', quotes);
            int end = code.IndexOf(fence, i + quotes, StringComparison.Ordinal);
            end = end < 0 ? code.Length : end;
            Blank(sb, i + quotes, end);
            return Math.Min(code.Length, end + quotes);
        }

        if (quotes == 2)
        {
            return i + 2; // ""
        }

        int j = i + 1;
        while (j < code.Length)
        {
            if (verbatim && code[j] == '"' && Next(code, j) == '"')
            {
                j += 2;
            }
            else if (!verbatim && code[j] == '\\')
            {
                j += 2;
            }
            else if (code[j] == '"' || (!verbatim && code[j] == '\n'))
            {
                break;
            }
            else
            {
                j++;
            }
        }

        Blank(sb, i + 1, Math.Min(j, code.Length));
        return Math.Min(code.Length, j + 1);
    }

    private static char Next(string code, int i) => i + 1 < code.Length ? code[i + 1] : '\0';

    private static int Blank(StringBuilder sb, int from, int to)
    {
        for (int k = from; k < to && k < sb.Length; k++)
        {
            if (sb[k] != '\n' && sb[k] != '\r')
            {
                sb[k] = ' ';
            }
        }

        return to;
    }

    [GeneratedRegex(@"\basync\s+void\s+(\w+)\s*\(")]
    private static partial Regex AsyncVoidRegex();

    [GeneratedRegex(@"\+=\s*async\b")]
    private static partial Regex AsyncEventLambdaRegex();

    [GeneratedRegex(@"(?<![\w.])_\s*=(?![=>])\s*")]
    private static partial Regex DiscardRegex();

    [GeneratedRegex(@"\.TryEnqueue\s*\(")]
    private static partial Regex RawTryEnqueueRegex();

    [GeneratedRegex(@"\.(TryEnqueue|RunLater|RunAfterEvent)\s*\(")]
    private static partial Regex AnyTryEnqueueRegex();

    [GeneratedRegex(@"\bawait\b")]
    private static partial Regex AwaitRegex();

    [GeneratedRegex(@"\G(?:(?:for|foreach|while)\s*\(|do\b)")]
    private static partial Regex LoopKeywordRegex();

    [GeneratedRegex(@"\b(?:Task|ValueTask)(?:<[^;{}()=]*>)?\??\s+(\w+)\s*(?:<[\w\s,]*>)?\s*\(")]
    private static partial Regex TaskMethodRegex();

    [GeneratedRegex(@"^\s*await\b")]
    private static partial Regex AwaitStartRegex();

    [GeneratedRegex(@"^(?:System\.Threading\.Tasks\.)?Task\.(?:Run|Delay|WhenAll|WhenAny|Factory\.StartNew)$")]
    private static partial Regex TaskFactoryCallRegex();

    [GeneratedRegex(@"\bUiTask\.Run\s*\(")]
    private static partial Regex UiTaskRunRegex();

    [GeneratedRegex(@"\b(?:Task|ValueTask)<[^;{}()=]*>\??\s+(\w+)\b(?!\s*[(<])")]
    private static partial Regex TaskVariableRegex();

    [GeneratedRegex(@"\bvar\s+(\w+)\s*=\s*")]
    private static partial Regex VarAssignmentRegex();

    [GeneratedRegex(@"\.ContinueWith\s*\(\s*(?:static\s+)?\(?\s*(\w+)\s*\)?\s*=>")]
    private static partial Regex ContinuationParameterRegex();

    [GeneratedRegex(@"(?:\b(\w+)|\))\s*\.\s*Result\b(?!\s*\()")]
    private static partial Regex ResultReadRegex();

    [GeneratedRegex(@"\btry\s*\{")]
    private static partial Regex TryBlockRegex();

    [GeneratedRegex(@"\G(catch|finally)\b\s*\(?")]
    private static partial Regex CatchClauseRegex();

    [GeneratedRegex(@"\G\s*when\s*\(")]
    private static partial Regex WhenRegex();
}
