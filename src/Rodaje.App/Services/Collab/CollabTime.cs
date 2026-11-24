namespace Rodaje.App.Services.Collab;

/// <summary>Tiempos dichos como los diría una persona.</summary>
public static class CollabTime
{
    /// <summary>«dentro de 24 horas», «dentro de 45 minutos»: lo que falta para que pase algo.</summary>
    public static string ToUserText(this TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return "ahora";
        }

        if (remaining.TotalMinutes < 1)
        {
            return "dentro de menos de 1 minuto";
        }

        if (remaining.TotalHours < 1)
        {
            int minutes = (int)Math.Round(remaining.TotalMinutes);
            return minutes == 1 ? "dentro de 1 minuto" : $"dentro de {minutes} minutos";
        }

        if (remaining.TotalDays < 1)
        {
            int hours = (int)Math.Round(remaining.TotalHours);
            return hours == 1 ? "dentro de 1 hora" : $"dentro de {hours} horas";
        }

        int days = (int)Math.Round(remaining.TotalDays);
        return days == 1 ? "dentro de 1 día" : $"dentro de {days} días";
    }

    /// <summary>«hace 5 minutos»: lo que pasó hace un rato.</summary>
    public static string AgoText(this TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromSeconds(10))
        {
            return "hace un momento";
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return $"hace {(int)elapsed.TotalSeconds} segundos";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            int m = (int)elapsed.TotalMinutes;
            return m == 1 ? "hace 1 minuto" : $"hace {m} minutos";
        }

        int h = (int)elapsed.TotalHours;
        return h == 1 ? "hace 1 hora" : $"hace {h} horas";
    }
}
