using System.Text;
using Dathinel.Sala;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// Las cargas ordenadas que llegan antes de que empiece la edición (mientras el invitado recibe el proyecto y busca sus
/// medios). Está acotada en bytes: al desbordarse se declara perdida y ya no guarda ni suelta nada; quien la usa tiene
/// que pedir una copia inicial nueva, que ya trae esos cambios, y nunca seguir con lo que quedó. No es segura entre
/// hilos: quien la tiene la toca siempre dentro de su candado.
/// </summary>
internal sealed class EarlyPayloadQueue(long maxBytes = EarlyPayloadQueue.DefaultMaxBytes)
{
    /// <summary>Colchón de 16 MiB de cargas: la copia inicial (64 MiB) trae ya todo lo confirmado, esto es solo el trasiego.</summary>
    internal const long DefaultMaxBytes = 16L << 20;

    private readonly List<Ordenado> _waiting = [];
    private long _bytes;
    private bool _lost;

    /// <summary>Se perdió al menos una carga desde el último <see cref="Drain"/>: lo retenido ya no sirve.</summary>
    public bool Lost => _lost;

    public int Count => _waiting.Count;

    /// <summary>Guarda la carga si cabe; al pasar del tope de bytes tira todo lo retenido y queda en perdida.</summary>
    public void Add(Ordenado ordered)
    {
        if (_lost)
        {
            // Tras perder una carga, nada de lo que siga sirve: la copia inicial nueva es la única salida.
            return;
        }

        long size = Encoding.UTF8.GetByteCount(ordered.Datos.GetRawText());
        if (_bytes + size > maxBytes)
        {
            _lost = true;
            _waiting.Clear();
            _bytes = 0;
            return;
        }

        _waiting.Add(ordered);
        _bytes += size;
    }

    /// <summary>
    /// Lo retenido, en orden, para entregarlo al empezar; <see langword="null"/> si se perdió algo y hay que pedir otra
    /// copia inicial. Siempre deja la cola vacía y limpia para la siguiente ronda.
    /// </summary>
    public IReadOnlyList<Ordenado>? Drain()
    {
        if (_lost)
        {
            _lost = false;
            _waiting.Clear();
            _bytes = 0;
            return null;
        }

        Ordenado[] replay = [.. _waiting];
        _waiting.Clear();
        _bytes = 0;
        return replay;
    }
}
