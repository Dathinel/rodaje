using System.Diagnostics;
using System.Runtime.CompilerServices;
using AotInterop;

// Uso: AotInterop <modo> [archivo] [--ffmpeg <carpeta con las DLL>]
// Modos: version, decodificar, compartir, d3d, audio-xaudio2, audio-wasapi, audio-naudio, medir, todo.

var reloj = Stopwatch.StartNew();
var argumentos = args.ToList();
string? carpetaFfmpeg = null;
int i = argumentos.IndexOf("--ffmpeg");
if (i >= 0 && i + 1 < argumentos.Count)
{
    carpetaFfmpeg = argumentos[i + 1];
    argumentos.RemoveRange(i, 2);
}
string modo = argumentos.Count > 0 ? argumentos[0] : "todo";
string? archivo = argumentos.Count > 1 ? argumentos[1] : null;

Console.WriteLine($"Modo: {(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "Native AOT")}, variante {Variante.Nombre}, gráficos {Variante.Graficos}");

try
{
    switch (modo)
    {
        case "version":
            Ffmpeg.Iniciar(carpetaFfmpeg);
            Console.WriteLine($"FFmpeg {Ffmpeg.Version()} cargado en {reloj.Elapsed.TotalMilliseconds:0.0} ms desde Main");
            break;
        case "decodificar":
            Ffmpeg.Iniciar(carpetaFfmpeg);
            Ffmpeg.DecodificarVideo(Requerido(archivo), Ffmpeg.ModoHw.FfmpegCreaDispositivo, 5);
            Ffmpeg.DecodificarVideo(Requerido(archivo), Ffmpeg.ModoHw.Software, 5);
            Ffmpeg.DecodificarAudio(Requerido(archivo));
            break;
        case "compartir":
            Ffmpeg.Iniciar(carpetaFfmpeg);
            Ffmpeg.CompartirDispositivo(Requerido(archivo));
            break;
        case "d3d":
            Gpu.Probar();
            break;
        case "audio-xaudio2":
            Ffmpeg.Iniciar(carpetaFfmpeg);
            AudioXAudio2.Reproducir(Ffmpeg.MuestrasOTono(archivo));
            break;
        case "audio-wasapi":
            Ffmpeg.Iniciar(carpetaFfmpeg);
            AudioWasapi.Reproducir(Ffmpeg.MuestrasOTono(archivo));
            break;
        case "audio-naudio":
#if VARIANTE_NAUDIO
            AudioNAudio.Reproducir(Tono.Generar(48000, 2.0));
            break;
#else
            Console.WriteLine("Esta variante no incluye NAudio; publica con -p:Variante=NAudio.");
            return 2;
#endif
        case "medir":
            Ffmpeg.Iniciar(carpetaFfmpeg);
            Ffmpeg.Medir(Requerido(archivo));
            break;
        case "todo":
            Ffmpeg.Iniciar(carpetaFfmpeg);
            Console.WriteLine($"FFmpeg {Ffmpeg.Version()}");
            Gpu.Probar();
            if (archivo is not null)
            {
                Ffmpeg.DecodificarVideo(archivo, Ffmpeg.ModoHw.FfmpegCreaDispositivo, 5);
                Ffmpeg.DecodificarVideo(archivo, Ffmpeg.ModoHw.Software, 5);
                Ffmpeg.CompartirDispositivo(archivo);
                Ffmpeg.DecodificarAudio(archivo);
            }
            break;
        default:
            Console.WriteLine($"Modo desconocido: {modo}");
            return 2;
    }
}
catch (Exception e)
{
    Console.WriteLine($"FALLO: {e.GetType().Name}: {e.Message}");
    Console.WriteLine(e.StackTrace);
    return 1;
}

Console.WriteLine($"Listo ({reloj.ElapsedMilliseconds} ms)");
return 0;

static string Requerido(string? archivo) =>
    archivo ?? throw new ArgumentException("Este modo necesita la ruta de un archivo de video.");

namespace AotInterop
{
    internal static class Variante
    {
#if VARIANTE_CARGADO
        public const string Nombre = "Cargado (FFmpeg.AutoGen.Bindings.DynamicallyLoaded)";
#elif VARIANTE_ENLAZADO
        public const string Nombre = "Enlazado (FFmpeg.AutoGen.Bindings.DynamicallyLinked)";
#elif VARIANTE_AUTOGEN
        public const string Nombre = "AutoGen (FFmpeg.AutoGen)";
#elif VARIANTE_NAUDIO
        public const string Nombre = "NAudio (DynamicallyLoaded + NAudio.Wasapi)";
#endif
#if GRAFICOS_TERRAFX
        public const string Graficos = "TerraFX.Interop.Windows";
#else
        public const string Graficos = "Vortice";
#endif
    }

    internal sealed record Lectura(ushort[] Y, string Descripcion, bool MismoDispositivo)
    {
        // NV12 trae el plano Y en 8 bits; P010 en 16 bits con el valor en los 10 bits altos.
        public static unsafe ushort[] CopiarPlanoY(byte* datos, uint paso, bool p010, int ancho, int alto)
        {
            var y = new ushort[ancho * alto];
            for (int fila = 0; fila < alto; fila++)
            {
                byte* origen = datos + (long)fila * paso;
                for (int x = 0; x < ancho; x++)
                    y[fila * ancho + x] = p010 ? (ushort)(((ushort*)origen)[x] >> 6) : origen[x];
            }
            return y;
        }
    }

    internal static class Tono
    {
        public static float[] Generar(int frecuenciaMuestreo, double segundos, double hz = 1000)
        {
            int cuadros = (int)(frecuenciaMuestreo * segundos);
            var datos = new float[cuadros * 2];
            for (int n = 0; n < cuadros; n++)
            {
                float v = (float)(0.2 * Math.Sin(2 * Math.PI * hz * n / frecuenciaMuestreo));
                datos[2 * n] = v;
                datos[2 * n + 1] = v;
            }
            return datos;
        }
    }

    // Registro de posición del reloj de audio contra el Stopwatch, común a los tres backends.
    internal sealed class RegistroReloj(string backend, int frecuenciaMuestreo)
    {
        private readonly List<(double Pared, double Audio)> puntos = [];

        public void Anotar(double paredSegundos, double audioSegundos)
        {
            puntos.Add((paredSegundos, audioSegundos));
            if (puntos.Count % 4 == 1)
                Console.WriteLine($"  [{backend}] t={paredSegundos * 1000,8:0.0} ms  pos={audioSegundos * 1000,8:0.000} ms  dif={(paredSegundos - audioSegundos) * 1000,7:0.00} ms");
        }

        public void Resumir()
        {
            var activos = puntos.Where(p => p.Audio > 0).ToList();
            if (activos.Count < 3)
            {
                Console.WriteLine($"  [{backend}] muy pocos puntos para resumir");
                return;
            }
            // Pendiente por mínimos cuadrados: 1,000 significa que el reloj de audio avanza igual que el de pared.
            double mx = activos.Average(p => p.Pared), my = activos.Average(p => p.Audio);
            double sxy = activos.Sum(p => (p.Pared - mx) * (p.Audio - my)), sxx = activos.Sum(p => (p.Pared - mx) * (p.Pared - mx));
            double pendiente = sxy / sxx, corte = my - pendiente * mx;
            double residuoMax = activos.Max(p => Math.Abs(p.Audio - (pendiente * p.Pared + corte)));
            double arranque = activos[0].Pared;
            Console.WriteLine($"  [{backend}] puntos={puntos.Count}, primer avance a {arranque * 1000:0.0} ms, pendiente={pendiente:0.00000}, " +
                $"retraso medio={(mx - my) * 1000:0.0} ms, residuo máx={residuoMax * 1000:0.000} ms ({residuoMax * frecuenciaMuestreo:0} muestras)");
        }
    }
}
