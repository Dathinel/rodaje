using Rodaje.Media;
using Rodaje.Media.Gpu;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using static Rodaje.Visor.Win32;

namespace Rodaje.Visor;

internal static unsafe class Program
{
    private const string Caption = "Rodaje visor";

    [STAThread]
    private static int Main(string[] args)
    {
        VisorArguments arguments;
        try
        {
            arguments = VisorArguments.Parse(args);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message, automated: args.Contains("--prueba", StringComparer.OrdinalIgnoreCase), code: 2, logPath: null);
        }

        bool automated = arguments.TestDuration is not null;
        try
        {
            // El diálogo de abrir es un objeto COM de apartamento: el hilo de la ventana tiene que ser STA.
            CoInitializeEx(null, (uint)COINIT.COINIT_APARTMENTTHREADED);
            if (!FfmpegLibrary.TryInitialize(out string? ffmpegFailure))
            {
                return Fail(ffmpegFailure!, automated, code: 1, arguments.LogPath);
            }

            using GpuDevice? gpu = GpuDevice.TryCreate(out string? gpuFailure);
            if (gpu is null)
            {
                return Fail("El visor necesita una GPU con Direct3D 11. " + gpuFailure, automated, code: 1, arguments.LogPath);
            }

            using var window = new VisorWindow(arguments, gpu);
            window.Show();
            if (arguments.Files.Count > 0 && !window.Open(arguments.Files, autoplay: true) && automated)
            {
                return 1;
            }

            if (arguments.Files.Count == 0 && !automated)
            {
                window.PromptForFiles();
            }

            if (automated)
            {
                window.StartAutomation(new AutomatedRun(arguments.TestDuration!.Value, arguments.LogPath));
            }

            return window.Run();
        }
#pragma warning disable CA1031 // Último recurso: el error se explica en vez de cerrar la ventana sin aviso.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Fail($"El visor se detuvo por un error inesperado:\n\n{ex.Message}\n\n{ex.GetType().FullName}", automated, code: 3, arguments.LogPath);
        }
    }

    private static int Fail(string message, bool automated, int code, string? logPath)
    {
        if (automated)
        {
            string report = "RESULTADO: FALLÓ. " + message + Environment.NewLine;
            Console.Out.Write(report);
            Console.Out.Flush();
            if (logPath is not null)
            {
                File.WriteAllText(logPath, report, new System.Text.UTF8Encoding(false));
            }

            return code;
        }

        fixed (char* text = message)
        fixed (char* caption = Caption)
        {
            _ = MessageBoxW(HWND.NULL, text, caption, MB_OK | MB_ICONERROR);
        }

        return code;
    }
}
