using System.Globalization;
using Rodaje.Export;

namespace Rodaje.App.Views.Export;

/// <summary>Textos del diálogo y del progreso, en español (coma decimal, aunque la app use la cultura invariante).</summary>
public static class ExportText
{
    /// <summary>Un número con coma decimal, hasta <paramref name="decimals"/> decimales y el signo menos tipográfico («1,78», «−14»).</summary>
    public static string Number(double value, int decimals = 1)
    {
        string format = decimals <= 0 ? "0" : "0." + new string('#', decimals);
        return value.ToString(format, CultureInfo.InvariantCulture).Replace('.', ',').Replace('-', '−');
    }

    /// <summary>«850 KB», «245 MB», «1,4 GB» (en potencias de 1000, como el Explorador las redondea a la vista).</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        < 1_000_000 => Number(Math.Max(1, bytes / 1000.0), 0) + " KB",
        < 1_000_000_000 => Number(bytes / 1e6, bytes < 10_000_000 ? 1 : 0) + " MB",
        _ => Number(bytes / 1e9, bytes < 100_000_000_000 ? 1 : 0) + " GB",
    };

    /// <summary>«45 s», «3 min 05 s», «1 h 02 min».</summary>
    public static string Duration(TimeSpan time)
    {
        long seconds = (long)Math.Max(0, Math.Round(time.TotalSeconds));
        return seconds switch
        {
            < 60 => $"{seconds} s",
            < 3600 => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} min {seconds % 60:00} s"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600} h {seconds / 60 % 60:00} min"),
        };
    }

    /// <summary>Qué está haciendo el trabajo, para la línea de estado.</summary>
    public static string State(ExportProgress progress) => progress.State switch
    {
        ExportJobState.Queued => "En espera",
        ExportJobState.Preparing => "Preparando",
        ExportJobState.MeasuringLoudness => "Midiendo la sonoridad",
        ExportJobState.MixingAudio => "Mezclando el audio",
        ExportJobState.Encoding when progress.PassCount > 1 => $"Pasada {progress.Pass} de {progress.PassCount}",
        ExportJobState.Encoding => "Exportando",
        ExportJobState.Finalizing => "Cerrando el archivo",
        ExportJobState.Completed => "Terminado",
        ExportJobState.Failed => "Falló",
        _ => "Cancelado",
    };

    /// <summary>«42 % · 238 fps · quedan 1 min 05 s», o solo el estado cuando no hay cifras.</summary>
    public static string Status(ExportProgress progress)
    {
        if (progress.State is not ExportJobState.Encoding)
        {
            return progress.State == ExportJobState.Completed ? $"Terminado en {Duration(progress.Elapsed)}" : State(progress);
        }

        string text = $"{Percent(progress)} · {Number(progress.FramesPerSecond, 0)} fps";
        if (progress.Remaining is { } remaining)
        {
            text += $" · quedan {Duration(remaining)}";
        }

        return progress.PassCount > 1 ? $"{State(progress)} · {text}" : text;
    }

    public static string Percent(ExportProgress progress) => $"{(int)Math.Floor(progress.Fraction * 100)} %";

    public static string Performance(ExportPerformance performance) => performance == ExportPerformance.Fastest
        ? "Alto rendimiento (menor tiempo)"
        : "Bajo rendimiento (la app sigue fluida)";

    public static string PerformanceDetail(ExportPerformance performance) => performance == ExportPerformance.Fastest
        ? "Usa todo el equipo; la vista previa se pausa mientras exporta."
        : "Exporta con prioridad baja para que puedas seguir editando sin tirones.";
}
