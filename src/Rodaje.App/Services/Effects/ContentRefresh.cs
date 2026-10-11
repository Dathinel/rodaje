using Rodaje.Engine.Timeline;

namespace Rodaje.App.Services.Effects;

/// <summary>QuÃ© hace falta en el hilo de render tras un <see cref="EffectContent.Changed"/>.</summary>
public enum ContentUpdate
{
    None,

    /// <summary>TerminÃ³ de cargar una LUT: basta con volver a componer.</summary>
    Redraw,

    /// <summary>Cambiaron los paquetes: hay que rearmar el plan (las transiciones se resuelven ahÃ­) y componer.</summary>
    Rebuild,
}

/// <summary>
/// Puente entre <see cref="EffectContent.Changed"/>, que llega en un hilo del pool, y el hilo de render: el aviso solo
/// marca una bandera y despierta al hilo, que la recoge con <see cref="Take"/> sin esperar a nadie.
/// </summary>
public sealed partial class ContentRefresh : IDisposable
{
    private readonly Action _wake;
    private EffectContent? _content;
    private int _pending;
    private int _generation;

    public ContentRefresh(Action wake)
    {
        ArgumentNullException.ThrowIfNull(wake);
        _wake = wake;
    }

    /// <summary>Escucha el contenido nuevo (solo <see cref="EffectContent"/> avisa) y deja de escuchar el anterior.</summary>
    public void Attach(IEffectContent? content)
    {
        if (_content is not null)
        {
            _content.Changed -= OnChanged;
        }

        _content = content as EffectContent;
        Volatile.Write(ref _pending, 0);
        if (_content is not null)
        {
            _generation = _content.Generation;
            _content.Changed += OnChanged;
        }
    }

    /// <summary>Lo pendiente desde la llamada anterior; desde el hilo de render.</summary>
    public ContentUpdate Take()
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0 || _content is null)
        {
            return ContentUpdate.None;
        }

        int generation = _content.Generation;
        if (generation == _generation)
        {
            return ContentUpdate.Redraw;
        }

        _generation = generation;
        return ContentUpdate.Rebuild;
    }

    public void Dispose() => Attach(null);

    private void OnChanged(object? sender, EventArgs e)
    {
        Volatile.Write(ref _pending, 1);
        _wake();
    }
}
