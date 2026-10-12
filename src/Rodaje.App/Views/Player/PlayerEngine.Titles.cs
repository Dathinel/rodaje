using Rodaje.Playback.Composition;
using Rodaje.Render.Text;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Títulos del reproductor: un <see cref="TitleRasterizer"/> sobre el dispositivo del hilo de render, conectado a la
/// sesión (precarga) y al compositor; las fuentes de los paquetes; las que faltan, para el aviso de la pestaña Texto; y
/// las cajas de los títulos dibujados, para las asas del visor.
/// </summary>
internal sealed partial class PlayerEngine
{
    private TitleRasterizer? _titles;
    private int _titleReady;
    private IReadOnlyList<string>? _pendingFonts;
    private IReadOnlyList<MissingFont> _missingFonts = [];
    private TitleBox[] _titleBoxes = [];

    /// <summary>Cambió la lista de fuentes que faltan. Llega desde el hilo de render.</summary>
    public event Action? MissingFontsChanged;

    /// <summary>Cambiaron las cajas de los títulos dibujados. Llega desde el hilo de render.</summary>
    public event Action? TitleBoxesChanged;

    /// <summary>Las fuentes que piden los títulos de la secuencia y no están (se dibujan con el respaldo).</summary>
    public IReadOnlyList<MissingFont> MissingFonts => Volatile.Read(ref _missingFonts);

    /// <summary>Los títulos del último cuadro compuesto, de abajo arriba.</summary>
    public IReadOnlyList<TitleBox> TitleBoxes => Volatile.Read(ref _titleBoxes);

    /// <summary>Los archivos de fuente de los paquetes instalados; reemplazan a los anteriores. Desde cualquier hilo.</summary>
    public void SetTitleFonts(IReadOnlyList<string> fontFiles)
    {
        ArgumentNullException.ThrowIfNull(fontFiles);
        lock (_pendingLock)
        {
            _pendingFonts = fontFiles;
        }

        _wake.Set();
    }

    private void CreateTitles()
    {
        _titles = new TitleRasterizer(_gpu!);
        _titles.Changed += (_, _) =>
        {
            Interlocked.Exchange(ref _titleReady, 1);
            _wake.Set();
        };
    }

    /// <summary>Fuentes nuevas y títulos que terminaron de rasterizarse: en pausa hay que volver a componer.</summary>
    private void ApplyTitleChanges()
    {
        IReadOnlyList<string>? fonts;
        lock (_pendingLock)
        {
            fonts = _pendingFonts;
            _pendingFonts = null;
        }

        if (fonts is not null && _titles is not null)
        {
            // La colección es nueva cada vez: un paquete desinstalado deja de aportar sus fuentes.
            _titles.Dispose();
            CreateTitles();
            IReadOnlyList<string> rejected = _titles!.RegisterFonts(fonts);
            if (rejected.Count > 0)
            {
                _metrics.Note($"fuentes rechazadas por DirectWrite: {string.Join(", ", rejected.Select(Path.GetFileName))}");
            }

            if (_compositor is not null)
            {
                _compositor.Titles = _titles;
            }

            if (_session is not null)
            {
                Time position = _session.Position;
                bool playing = _session.IsPlaying;
                OpenSession();
                _session?.Seek(position);
                if (playing)
                {
                    _session?.Play();
                }
            }
        }

        if (Interlocked.Exchange(ref _titleReady, 0) == 1)
        {
            _dirty = true;
        }
    }

    /// <summary>La pasada que solo resuelve fuentes, tras abrir o recargar la secuencia.</summary>
    private void UpdateMissingFonts()
    {
        if (_titles is null || _session is null)
        {
            return;
        }

        IReadOnlyList<GeneratedContent> contents = _session.GeneratedContents();
        IReadOnlyList<MissingFont> missing = contents.Count == 0 ? [] : _titles.FindMissingFonts(contents);
        IReadOnlyList<MissingFont> current = Volatile.Read(ref _missingFonts);
        if (missing.Count == 0 && current.Count == 0 || missing.SequenceEqual(current))
        {
            return;
        }

        Volatile.Write(ref _missingFonts, missing);
        MissingFontsChanged?.Invoke();
    }

    /// <summary>Publica dónde quedó cada título del cuadro; sin títulos ni antes ni ahora, no hace nada.</summary>
    private void PublishTitleBoxes(CompositionFrame frame)
    {
        TitleBox[] current = _titleBoxes;
        int count = 0;
        foreach (LayerFrame layer in frame.Layers)
        {
            if (layer.Generated is not null)
            {
                count++;
            }
        }

        if (count == 0 && current.Length == 0)
        {
            return;
        }

        var boxes = new TitleBox[count];
        int i = 0;
        foreach (LayerFrame layer in frame.Layers)
        {
            if (layer.Generated is { } content)
            {
                TitleTexture? texture = _titles?.TryGet(content);
                boxes[i++] = new TitleBox(layer.ClipId, layer.Transform, texture?.LocalWidth ?? 0, texture?.LocalHeight ?? 0);
            }
        }

        if (boxes.AsSpan().SequenceEqual(current))
        {
            return;
        }

        Volatile.Write(ref _titleBoxes, boxes);
        TitleBoxesChanged?.Invoke();
    }
}
