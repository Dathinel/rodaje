using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.VulkanVideo;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== Prototipo Vulkan Video (Rodaje - D-066 Fase 0b) ===");

        int segundos = 20;
        bool forzarSubida = false;
        string? gpuFilter = Environment.GetEnvironmentVariable("RODAJE_VK_GPU");
        string salidaJson = "resultado.json";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--segundos" && i + 1 < args.Length && int.TryParse(args[i + 1], out int s))
            {
                segundos = s;
                i++;
            }
            else if (args[i] == "--forzar-subida")
            {
                forzarSubida = true;
            }
            else if (args[i] == "--gpu" && i + 1 < args.Length)
            {
                gpuFilter = args[i + 1];
                i++;
            }
            else if (args[i] == "--salida" && i + 1 < args.Length)
            {
                salidaJson = args[i + 1];
                i++;
            }
            else if (args[i] == "--ayuda" || args[i] == "-h")
            {
                Console.WriteLine("Uso: vulkan-video [--segundos N] [--forzar-subida] [--gpu <nombre>] [--salida <ruta>]");
                return 0;
            }
        }

        Console.WriteLine($"Duración solicitada: {segundos} s");
        if (forzarSubida) Console.WriteLine("Modo forzado: respaldo por subida de cuadros software.");

        // 1. Inicializar FFmpeg
        string ffmpegDir;
        try
        {
            ffmpegDir = FfmpegInterop.Initialize();
            Console.WriteLine($"FFmpeg inicializado desde: {ffmpegDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error al inicializar FFmpeg: {ex.Message}");
            return 1;
        }

        // 2. Generar muestras si no existen
        string h264Path;
        string hevcPath;
        try
        {
            (h264Path, hevcPath) = VideoGenerator.EnsureSamplesExist(ffmpegDir);
            Console.WriteLine($"Muestras preparadas:\n  H.264: {h264Path}\n  HEVC:  {hevcPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error preparando muestras de video: {ex.Message}");
            return 1;
        }

        // 3. Crear contexto Vulkan y dispositivo FFmpeg
        VulkanContext vk;
        try
        {
            vk = new VulkanContext(gpuFilter, forzarSubida);
            Console.WriteLine($"GPU seleccionada: {vk.GpuName} (Driver: {vk.DriverVersion})");
            Console.WriteLine($"Soporte Vulkan Video en GPU: {(vk.SupportsVulkanVideo ? "Sí" : "No")}");
            Console.WriteLine($"Familia cola gráfica: {vk.GfxQueueFamily}, cola decodificación: {vk.DecodeQueueFamily}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error inicializando Vulkan: {ex.Message}");
            return 1;
        }

        string camino = (vk.SupportsVulkanVideo && !forzarSubida) ? "vulkan-video" : "subida";
        Console.WriteLine($"Camino de decodificación activo: \"{camino}\"");

        // 4. Ejecutar decodificación, muestreo y verificación
        var runner = new DecoderRunner(vk, forzarSubida);
        try
        {
            Console.WriteLine($"Iniciando ejecución de pruebas por {segundos} s...");
            runner.Run(h264Path, hevcPath, segundos);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error durante la ejecución del decodificador: {ex.Message}\n{ex.StackTrace}");
            vk.Dispose();
            return 1;
        }

        // 5. Limpieza de recursos Vulkan
        int liveVkImages = 0;
        int liveAvFrames = 0;
        try
        {
            vk.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Advertencia al liberar recursos Vulkan: {ex.Message}");
        }

        // 6. Imprimir resumen en consola
        Console.WriteLine("\n--- Resumen de Ejecución ---");
        Console.WriteLine($"GPU:                         {vk.GpuName}");
        Console.WriteLine($"Driver:                      {vk.DriverVersion}");
        Console.WriteLine($"Camino:                      {camino}");
        Console.WriteLine($"Cuadros H.264 (NV12):        {runner.Nv12Frames}");
        Console.WriteLine($"Cuadros HEVC (P010):         {runner.P010Frames}");
        Console.WriteLine($"Cuadros totales:             {runner.TotalFrames}");
        Console.WriteLine($"Rendimiento medio:           {runner.Fps:F1} fps");
        Console.WriteLine($"Latencia por cuadro:         p50 = {runner.P50Ms:F2} ms, p95 = {runner.P95Ms:F2} ms");
        Console.WriteLine($"Transferencias CPU normales: {runner.NormalCpuTransfers}");
        Console.WriteLine($"Memoria proceso (MB):        inicio = {runner.StartMemoryMb:F1}, mitad = {runner.MidMemoryMb:F1}, final = {runner.EndMemoryMb:F1}");
        Console.WriteLine($"Recursos vivos al final:     VkImage = {liveVkImages}, AVFrame = {liveAvFrames}");
        Console.WriteLine($"Errores de validación:       {vk.ValidationErrors}");
        Console.WriteLine($"Verificación contra CPU:     {runner.VerifiedFramesCount} cuadros comparados");
        Console.WriteLine($"Error medio (MAE / 255):     {runner.AvgMae255:F5} (umbral <= {DecoderRunner.MaeThreshold:F5})");
        Console.WriteLine($"PSNR medio:                  {runner.AvgPsnrDb:F2} dB");
        Console.WriteLine($"Estado de verificación MAE:  {(runner.ThresholdPassed ? "SUPERADO" : "FALLIDO")}");
        Console.WriteLine($"Comparación exacta NV12:     comparados = {runner.Nv12ExactStats.CuadrosComparados}, idénticos = {runner.Nv12ExactStats.CuadrosIdenticos}, bytes distintos = {runner.Nv12ExactStats.BytesDistintos}, dif máx = {runner.Nv12ExactStats.DiferenciaMaxima}");
        Console.WriteLine($"Comparación exacta P010:     comparados = {runner.P010ExactStats.CuadrosComparados}, idénticos = {runner.P010ExactStats.CuadrosIdenticos}, bytes distintos = {runner.P010ExactStats.BytesDistintos}, dif máx = {runner.P010ExactStats.DiferenciaMaxima}");
        Console.WriteLine($"Estado comparación exacta:   {(runner.ExactComparisonPassed ? "SUPERADO" : "FALLIDO")}");

        // 7. Escribir resultado.json
        var exactObj = new ComparacionExactaInfo
        {
            Nv12 = runner.Nv12ExactStats,
            P010 = runner.P010ExactStats
        };

        var resultadoObj = new ResultadoReporte
        {
            Gpu = vk.GpuName,
            Driver = vk.DriverVersion,
            Camino = camino,
            ExtensionesVideo = new ExtensionesVideoInfo
            {
                Presentes = vk.PresentVideoExtensions,
                Ausentes = vk.AbsentVideoExtensions
            },
            CuadrosDecodificados = new CuadrosDecodificadosInfo
            {
                Nv12 = runner.Nv12Frames,
                P010 = runner.P010Frames,
                Total = runner.TotalFrames
            },
            Fps = Math.Round(runner.Fps, 2),
            MsPorCuadro = new MsPorCuadroInfo
            {
                P50 = Math.Round(runner.P50Ms, 3),
                P95 = Math.Round(runner.P95Ms, 3)
            },
            TransferenciasCpuCaminoNormal = runner.NormalCpuTransfers,
            MemoriaMb = new MemoriaMbInfo
            {
                Inicio = Math.Round(runner.StartMemoryMb, 2),
                Mitad = Math.Round(runner.MidMemoryMb, 2),
                Final = Math.Round(runner.EndMemoryMb, 2)
            },
            RecursosVivos = new RecursosVivosInfo
            {
                VkImages = liveVkImages,
                AvFrames = liveAvFrames
            },
            ErroresValidacion = vk.ValidationErrors,
            Verificacion = new VerificacionInfo
            {
                CuadrosVerificados = runner.VerifiedFramesCount,
                ErrorMedio255 = Math.Round(runner.AvgMae255, 5),
                UmbralError255 = Math.Round(DecoderRunner.MaeThreshold, 5),
                PsnrMedioDb = Math.Round(runner.AvgPsnrDb, 2),
                SuperoUmbral = runner.ThresholdPassed,
                Exacta = exactObj
            },
            ComparacionExacta = exactObj
        };

        try
        {
            string json = JsonSerializer.Serialize(resultadoObj, ResultadoJsonContext.Default.ResultadoReporte);
            File.WriteAllText(salidaJson, json);
            Console.WriteLine($"Resultado guardado exitosamente en: {Path.GetFullPath(salidaJson)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error guardando {salidaJson}: {ex.Message}");
            return 1;
        }

        bool exito = runner.ThresholdPassed && runner.ExactComparisonPassed && (liveVkImages == 0) && (liveAvFrames == 0) && (runner.TotalFrames > 0);
        Console.WriteLine($"\nCódigo de salida: {(exito ? 0 : 1)}");
        return exito ? 0 : 1;
    }
}
