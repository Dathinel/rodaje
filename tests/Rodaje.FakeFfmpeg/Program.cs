// Un ffmpeg.exe falso para las pruebas de FfmpegPipeSink. Acepta la línea de comandos del sink tal cual (ignora lo que
// no conoce) y toma como salida el último argumento, igual que ffmpeg. Opciones propias, que las pruebas pasan entre los
// argumentos del codificador:
//   -falso-salida <código>   código con el que sale al terminar la entrada estándar (0 si no se da);
//   -falso-colgar            al terminar la entrada estándar no sale: se queda dormido hasta que lo maten, como un
//                            ffmpeg.exe que tarda en cerrar el contenedor. Para no quedar huérfano (en Linux no hay
//                            ChildProcessJob que lo mate con las pruebas), sale solo con el código 124 a los 2 minutos
//                            o en cuanto muere el proceso cuyo id trae la variable RODAJE_FALSO_PADRE (la pone
//                            FakeFfmpeg en las pruebas y el falso la hereda).
// Crea la salida al arrancar y copia en ella lo que llega por la entrada estándar, así que el temporal existe y está
// abierto mientras el proceso vive.

int exitCode = 0;
bool hang = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "-falso-salida" && i + 1 < args.Length)
    {
        exitCode = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    }
    else if (args[i] == "-falso-colgar")
    {
        hang = true;
    }
}

string? output = args.Length > 0 && !args[^1].StartsWith('-') ? args[^1] : null;
using (Stream input = Console.OpenStandardInput())
using (Stream sink = output is null ? Stream.Null : new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
{
    input.CopyTo(sink, 1024 * 1024);
    sink.Flush();
    if (hang)
    {
        HangUntilKilled();
        Console.Error.WriteLine("El ffmpeg falso deja de esperar: pasó el tope o murió el proceso de las pruebas.");
        return 124;
    }
}

if (exitCode != 0)
{
    Console.Error.WriteLine($"Error falso de ffmpeg (código {exitCode}).");
}

return exitCode;

// Duerme hasta que lo maten, pero no más de dos minutos ni más allá de la vida del proceso de las pruebas.
static void HangUntilKilled()
{
    TimeSpan limit = TimeSpan.FromMinutes(2);
    TimeSpan poll = TimeSpan.FromMilliseconds(250);
    var elapsed = System.Diagnostics.Stopwatch.StartNew();
    using System.Diagnostics.Process? parent = OpenParent(out bool parentGone);
    while (!parentGone && elapsed.Elapsed < limit)
    {
        Thread.Sleep(poll);
        parentGone = parent is not null && HasExited(parent);
    }
}

// El proceso de las pruebas, por el id de RODAJE_FALSO_PADRE; null si no viene (entonces solo vale el tope).
static System.Diagnostics.Process? OpenParent(out bool gone)
{
    gone = false;
    if (!int.TryParse(Environment.GetEnvironmentVariable("RODAJE_FALSO_PADRE"), System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out int id))
    {
        return null;
    }

    try
    {
        System.Diagnostics.Process parent = System.Diagnostics.Process.GetProcessById(id);
        gone = HasExited(parent);
        return parent;
    }
    catch (ArgumentException)
    {
        gone = true;
        return null;
    }
}

static bool HasExited(System.Diagnostics.Process process)
{
    try
    {
        return process.HasExited;
    }
    catch (InvalidOperationException)
    {
        return true;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        // Sin permiso para preguntar: no se sabe, y queda el tope.
        return false;
    }
}
