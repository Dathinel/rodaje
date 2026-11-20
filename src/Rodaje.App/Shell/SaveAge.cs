using System.Globalization;

namespace Rodaje.App.Shell;

/// <summary>
/// El aviso discreto de la barra de estado: cuánto hace que el proyecto se guardó o que el autoguardado hizo una copia.
/// No mira el reloj: recibe cuánto pasó, así se prueba sin esperas. Solo lo usa la ventana principal.
/// </summary>
public static class SaveAge
{
    /// <summary>
    /// «Guardado hace 3 min» si hay guardado; si no, «Copia automática hace 1 min»; vacío si no hubo ni uno ni otro.
    /// Con cambios sin guardar y un guardado más viejo que la copia, se nombra la copia: es lo que se recuperaría.
    /// </summary>
    public static string Format(TimeSpan? sinceSave, TimeSpan? sinceCopy, bool dirty)
    {
        if (sinceSave is { } save && (!dirty || sinceCopy is null || save <= sinceCopy))
        {
            return "Guardado " + Ago(save);
        }

        return sinceCopy is { } copy ? "Copia automática " + Ago(copy) : "";
    }

    /// <summary>«hace un momento» (menos de un minuto), «hace N min» hasta 90 min, «hace N h» y, desde un día, «hace N días».</summary>
    public static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        double minutes = Math.Floor(elapsed.TotalMinutes);
        if (minutes < 1)
        {
            return "hace un momento";
        }

        if (minutes <= 90)
        {
            return string.Create(CultureInfo.InvariantCulture, $"hace {minutes:0} min");
        }

        double hours = Math.Floor(elapsed.TotalHours);
        if (hours < 24)
        {
            return string.Create(CultureInfo.InvariantCulture, $"hace {hours:0} h");
        }

        double days = Math.Floor(elapsed.TotalDays);
        return days == 1 ? "hace 1 día" : string.Create(CultureInfo.InvariantCulture, $"hace {days:0} días");
    }
}
