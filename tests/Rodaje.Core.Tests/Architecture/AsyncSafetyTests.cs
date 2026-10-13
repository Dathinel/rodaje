using System.Text;
using System.Text.RegularExpressions;

namespace Rodaje.Core.Tests.Architecture;

/// <summary>
/// Prueba de arquitectura sobre el texto de <c>src/**/*.cs</c>: que no vuelvan las excepciones perdidas ni los bloqueos
/// del hilo de la interfaz que encontró la auditoría (async void sin red, tareas sueltas, esperas que bloquean, callbacks
/// de DispatcherQueue sin red o encolados en bucle, hash e I/O grandes en el código de la interfaz). Lee los fuentes como
/// texto (sin compilar la app, que es de Windows), sin comentarios ni cadenas. Cada excepción está en una lista explícita
/// con su motivo; una entrada nueva necesita uno.
/// </summary>
public sealed partial class AsyncSafetyTests
{
    // Archivos de src/Rodaje.App/ (ruta con «/») que pueden llamar a DispatcherQueue.TryEnqueue sin la red de GuardedDispatch.
    private static readonly Dictionary<string, string> RawTryEnqueueAllowed = new(StringComparer.Ordinal)
    {
        ["GuardedDispatch.cs"] = "es la red: envuelve el callback.",
        ["App.xaml.cs"] = "el aviso de un error ya informado no puede pasar por la red (informar volvería a avisar sin fin); atrapa lo suyo.",
        ["Views/Inspector/DispatcherQueueLater.cs"] = "encola la acción ya envuelta por UiDeferral.Run (informe y aviso).",
    };

    // async void que no son manejadores de eventos.
    // («archivo#Método» → motivo). Hoy ninguno: UiTask.Run observa con una continuación.
    private static readonly Dictionary<string, string> AsyncVoidAllowed = new(StringComparer.Ordinal);

    // Esperas bloqueantes en el código de la interfaz (Views/, Shell/, App.xaml.cs): (archivo, texto) → motivo.
    private static readonly Dictionary<(string File, string Pattern), string> BlockingWaitAllowed = new()
    {
        [("Rodaje.App/Views/Player/PlayerEngine.Transitions.cs", ".Wait(TimeSpan")] = "corre en el hilo del motor, no en el de la interfaz.",
        [("Rodaje.App/Views/Player/PlayerEngine.Looks.cs", ".Wait(TimeSpan")] = "corre en el hilo del motor, no en el de la interfaz.",
        [("Rodaje.App/Shell/MainWindow.xaml.cs", ".Wait(TimeSpan")] = "al cerrar: como mucho 3 s, y el cierre de los módulos no vuelve al hilo de la interfaz (ConfigureAwait(false)).",
        [("Rodaje.App/Views/Export/ExportView.StreamCopy.cs", "task.Result")] = "en la continuación y después de IsCompletedSuccessfully: la tarea ya terminó.",
    };

    // I/O o hash de archivos en el código de la interfaz: (archivo, texto) → motivo.
    private static readonly Dictionary<(string File, string Pattern), string> UiFileWorkAllowed = new()
    {
        [("Rodaje.App/Views/Analysis/StabilizationStore.cs", "File.ReadAllBytes(")] = "un análisis en caché (KB), leído desde el análisis o el render, no desde la interfaz.",
        [("Rodaje.App/Views/Analysis/ClipVideoSource.cs", "SourceFingerprint.Compute(")] = "corre en el análisis, fuera del hilo de la interfaz (como mucho 2 MiB).",
    };

    // TryEnqueue dentro de un bucle: archivo → motivo.
    private static readonly Dictionary<string, string> EnqueueInLoopAllowed = new(StringComparer.Ordinal)
    {
        ["Rodaje.App/Shell/MeasurementRun.cs"] = "solo con -Medir: mide la latencia de la cola con un aviso cada 50 ms.",
    };

    private static readonly string[] BlockingPatterns =
        [".GetAwaiter().GetResult()", ".Wait()", ".Wait(TimeSpan", "Task.WaitAll(", "Task.WaitAny(", "task.Result", "Task.Result", ".AsTask().Result"];

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

                if (!TryCatchRegex().IsMatch(body))
                {
                    problems.Add($"{where}: async void sin try/catch (usa async Task y UiTask.Run, o atrapa y UiTask.Report).");
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
        var problems = new List<string>();
        foreach ((string file, string code) in Sources("Rodaje.App/"))
        {
            foreach (Match m in DiscardRegex().Matches(code))
            {
                int start = m.Index + m.Length;
                string statement = Statement(code, start);
                if (statement.Contains(".ContinueWith(", StringComparison.Ordinal))
                {
                    continue;
                }

                if (DiscardedTaskRegex().IsMatch(statement))
                {
                    problems.Add($"{file}:{Line(code, m.Index)}: «_ = {First(statement)}» pierde la excepción de la tarea: UiTask.Run(…).");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void UiCodeDoesNotBlockOnTasks()
    {
        var problems = new List<string>();
        foreach ((string file, string code) in Sources("Rodaje.App/").Where(s => IsUiFile(s.File)))
        {
            foreach (string pattern in BlockingPatterns)
            {
                for (int i = code.IndexOf(pattern, StringComparison.Ordinal); i >= 0; i = code.IndexOf(pattern, i + 1, StringComparison.Ordinal))
                {
                    if (!BlockingWaitAllowed.ContainsKey((file, pattern)))
                    {
                        problems.Add($"{file}:{Line(code, i)}: «{pattern}» bloquea (en el hilo de la interfaz, congela la ventana o se traba): await.");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
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
                for (int i = code.IndexOf(pattern, StringComparison.Ordinal); i >= 0; i = code.IndexOf(pattern, i + 1, StringComparison.Ordinal))
                {
                    if (!UiFileWorkAllowed.ContainsKey((file, pattern)))
                    {
                        problems.Add($"{file}:{Line(code, i)}: «{pattern}» en el código de la interfaz: va a Services/ o a Logic/ y se llama con Task.Run.");
                    }
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
            string local = file["Rodaje.App/".Length..];
            foreach (Match m in RawTryEnqueueRegex().Matches(code))
            {
                if (!RawTryEnqueueAllowed.ContainsKey(local))
                {
                    problems.Add($"{file}:{Line(code, m.Index)}: TryEnqueue sin red: si el callback lanza, la app se cierra (TryEnqueueGuarded).");
                }
            }

            if (EnqueueInLoopAllowed.ContainsKey(file))
            {
                continue;
            }

            foreach (Match m in AnyTryEnqueueRegex().Matches(code))
            {
                if (InsideLoop(code, m.Index))
                {
                    problems.Add($"{file}:{Line(code, m.Index)}: TryEnqueue dentro de un bucle llena la cola de la interfaz sin límite (CoalescedDispatch).");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void AllowListsPointAtExistingCode()
    {
        // Una excepción que ya no hace falta se quita de la lista (si no, tapa lo que vuelva a aparecer ahí).
        Dictionary<string, string> sources = Sources().ToDictionary(s => s.File, s => s.Code, StringComparer.Ordinal);
        var stale = new List<string>();
        foreach (string file in RawTryEnqueueAllowed.Keys.Where(f => !RawTryEnqueueRegex().IsMatch(sources.GetValueOrDefault("Rodaje.App/" + f, ""))))
        {
            stale.Add(file);
        }

        foreach (string where in AsyncVoidAllowed.Keys)
        {
            string[] parts = where.Split('#');
            if (!sources.TryGetValue(parts[0], out string? code) || !code.Contains("async void " + parts[1], StringComparison.Ordinal))
            {
                stale.Add(where);
            }
        }

        foreach ((string file, string pattern) in BlockingWaitAllowed.Keys.Concat(UiFileWorkAllowed.Keys))
        {
            if (!sources.TryGetValue(file, out string? code) || !code.Contains(pattern, StringComparison.Ordinal))
            {
                stale.Add($"{file} «{pattern}»");
            }
        }

        foreach (string file in EnqueueInLoopAllowed.Keys.Where(f => !sources.ContainsKey(f)))
        {
            stale.Add(file);
        }

        Assert.True(stale.Count == 0, "Excepciones que ya no aplican: " + string.Join(", ", stale));
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

    // Si la posición cae dentro del cuerpo con llaves de un for, foreach, while o do (también dentro de una lambda en él).
    private static bool InsideLoop(string code, int index)
    {
        var headers = new Stack<string>();
        int statementStart = 0;
        for (int i = 0; i < index; i++)
        {
            switch (code[i])
            {
                case '{':
                    headers.Push(code[statementStart..i]);
                    statementStart = i + 1;
                    break;
                case '}':
                    if (headers.Count > 0)
                    {
                        headers.Pop();
                    }

                    statementStart = i + 1;
                    break;
                case ';':
                    statementStart = i + 1;
                    break;
            }
        }

        return headers.Any(h => LoopHeaderRegex().IsMatch(h));
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

    [GeneratedRegex(@"\btry\b[\s\S]*\bcatch\b")]
    private static partial Regex TryCatchRegex();

    [GeneratedRegex(@"\+=\s*async\b")]
    private static partial Regex AsyncEventLambdaRegex();

    [GeneratedRegex(@"(?<![\w.])_\s*=\s*(?!=)")]
    private static partial Regex DiscardRegex();

    // Lo descartado es una tarea: un «…Async(…)», Task.Run o Task.Factory.StartNew al principio de la expresión.
    [GeneratedRegex(@"^\s*(await\s+)?([\w.?!]*Async\s*\(|Task\.Run\s*\(|Task\.Factory\.StartNew\s*\()")]
    private static partial Regex DiscardedTaskRegex();

    [GeneratedRegex(@"\.TryEnqueue\s*\(")]
    private static partial Regex RawTryEnqueueRegex();

    [GeneratedRegex(@"\.TryEnqueue(Guarded)?\s*\(")]
    private static partial Regex AnyTryEnqueueRegex();

    [GeneratedRegex(@"\bawait\b")]
    private static partial Regex AwaitRegex();

    [GeneratedRegex(@"(^|[\s;}])(for|foreach|while)\s*\(|(^|[\s;}])do\s*$")]
    private static partial Regex LoopHeaderRegex();
}
