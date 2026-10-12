// Un ffmpeg.exe falso para las pruebas de FfmpegPipeSink. Acepta la línea de comandos del sink tal cual (ignora lo que
// no conoce) y toma como salida el último argumento, igual que ffmpeg. Opciones propias, que las pruebas pasan entre los
// argumentos del codificador:
//   -falso-salida <código>   código con el que sale al terminar la entrada estándar (0 si no se da);
//   -falso-colgar            al terminar la entrada estándar no sale: se queda dormido hasta que lo maten, como un
//                            ffmpeg.exe que tarda en cerrar el contenedor.
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
        Thread.Sleep(Timeout.Infinite);
    }
}

if (exitCode != 0)
{
    Console.Error.WriteLine($"Error falso de ffmpeg (código {exitCode}).");
}

return exitCode;
