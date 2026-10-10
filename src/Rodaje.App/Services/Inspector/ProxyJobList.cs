using System.Globalization;
using Rodaje.Media.Proxies;

namespace Rodaje.App.Services.Inspector;

/// <summary>Una fila de la pestaña Trabajos.</summary>
/// <param name="SourcePath">Original del proxy; identifica la fila.</param>
/// <param name="Title">Nombre del archivo.</param>
/// <param name="Status">Estado en palabras («En cola», «Generando · 42 % · 3,1x», «Listo»…).</param>
/// <param name="Progress">Avance de 0 a 1.</param>
/// <param name="IsActive">En cola o generándose: se puede cancelar.</param>
/// <param name="IsError">Terminó con un error.</param>
public sealed record JobRow(string SourcePath, string Title, string Status, double Progress, bool IsActive, bool IsError);

/// <summary>
/// Los trabajos de proxies a partir de los estados que publica <see cref="MediaSourceResolver"/>: aparece una fila
/// cuando un proxy entra en cola y queda con su resultado (listo, cancelado o fallido) hasta que se limpia.
/// </summary>
public sealed class ProxyJobList
{
    private readonly List<JobRow> _rows = [];

    public IReadOnlyList<JobRow> Rows => _rows;

    public int ActiveCount => _rows.Count(r => r.IsActive);

    /// <summary>Incorpora un estado; <c>true</c> si cambió alguna fila.</summary>
    public bool Apply(ProxyStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        int index = _rows.FindIndex(r => string.Equals(r.SourcePath, status.SourcePath, StringComparison.OrdinalIgnoreCase));
        bool tracked = index >= 0;
        JobRow? row = status.State switch
        {
            ProxyState.Queued => Row(status, "En cola", 0, active: true),
            ProxyState.Generating => Row(status, GeneratingText(status), status.Progress, active: true),
            ProxyState.Failed => Row(status, "Falló: " + (status.Message ?? "error desconocido"), 0, error: true),
            ProxyState.Ready when tracked => Row(status, "Listo", 1),
            ProxyState.Unavailable when tracked => Row(status, status.Message ?? "No se pueden generar proxies", 0, error: true),
            _ when tracked && _rows[index].IsActive => Row(status, "Cancelado", 0),
            _ => null,
        };

        if (row is null || (tracked && row == _rows[index]))
        {
            return false;
        }

        if (tracked)
        {
            _rows[index] = row;
        }
        else
        {
            _rows.Add(row);
        }

        return true;
    }

    /// <summary>Quita las filas terminadas; <c>true</c> si quitó alguna.</summary>
    public bool ClearFinished() => _rows.RemoveAll(r => !r.IsActive) > 0;

    private static JobRow Row(ProxyStatus status, string text, double progress, bool active = false, bool error = false) =>
        new(status.SourcePath, Path.GetFileName(status.SourcePath), text, Math.Clamp(progress, 0, 1), active, error);

    private static string GeneratingText(ProxyStatus status)
    {
        string text = "Generando · " + Math.Round(status.Progress * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
        if (status.Detail is { Speed: > 0 } detail)
        {
            text += " · " + detail.Speed.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + "x";
        }

        return text;
    }
}
