using System.Runtime.CompilerServices;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.Render.Text;

namespace Rodaje.App.Views.Player;

/// <summary>Un cuadro compuesto bajado a la CPU, RGBA de 8 bits fila tras fila.</summary>
internal sealed record FrameCapture(byte[] Rgba, int Width, int Height);

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
    private TaskCompletionSource<FrameCapture>? _pendingCapture;
    private int _captureBypass;
    private int _viewBypass;
    private float _compareSplit = -1;
    private StrongBox<MaskPreview>? _maskView;

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

        Wake();
    }

    /// <summary>
    /// El próximo cuadro compuesto en RGBA de 8 bits (a la resolución del compositor), para la autoprueba: lo que dibuja
    /// la GPU no sale en una captura de XAML.
    /// </summary>
    /// <param name="bypass">Efectos que se dejan fuera en ese cuadro (el cuentagotas del croma mira la imagen sin él).</param>
    public Task<FrameCapture> CaptureFrameAsync(PreviewBypass bypass = PreviewBypass.None)
    {
        var capture = new TaskCompletionSource<FrameCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _captureBypass, (int)bypass);
        Interlocked.Exchange(ref _pendingCapture, capture)?.TrySetCanceled();
        _dirty = true;
        Wake();
        return capture.Task;
    }

    private void CompleteCapture()
    {
        if (_compositor is { } compositor && Interlocked.Exchange(ref _pendingCapture, null) is { } capture)
        {
            capture.TrySetResult(new FrameCapture(compositor.ReadbackRgba(), compositor.Width, compositor.Height));
            if (Volatile.Read(ref _captureBypass) != 0)
            {
                // El cuadro sin efectos no se queda en pantalla: se vuelve a componer enseguida.
                Volatile.Write(ref _captureBypass, 0);
                _dirty = true;
            }
        }
    }

    /// <summary>Antes/después: la fracción del ancho que se ve sin color (nula, sin comparar). Desde cualquier hilo.</summary>
    public void SetCompare(float? split)
    {
        Volatile.Write(ref _compareSplit, split is { } s and > 0 ? Math.Clamp(s, 0, 1) : -1);
        _dirty = true;
        Wake();
    }

    /// <summary>Efectos que la vista previa deja fuera (el recorte mientras se ajusta). Desde cualquier hilo.</summary>
    public void SetBypass(PreviewBypass bypass)
    {
        Volatile.Write(ref _viewBypass, (int)bypass);
        _dirty = true;
        Wake();
    }

    /// <summary>Solo vista previa: una máscara en gris o superpuesta mientras se edita (nula, como se exporta). Desde cualquier hilo.</summary>
    public void SetMaskView(MaskPreview? view)
    {
        Volatile.Write(ref _maskView, view is { } v ? new StrongBox<MaskPreview>(v) : null);
        _dirty = true;
        Wake();
    }

    private void ApplyViewOptions(Compositor compositor)
    {
        compositor.MaskView = Volatile.Read(ref _maskView)?.Value;
        float split = Volatile.Read(ref _compareSplit);
        compositor.CompareSplit = split > 0 ? split : null;
        int capture = _pendingCapture is null ? 0 : Volatile.Read(ref _captureBypass);
        compositor.Bypass = (PreviewBypass)(Volatile.Read(ref _viewBypass) | capture);
        compositor.ShowQualifierMask = Volatile.Read(ref _showQualifierMask) != 0 && capture == 0;
    }

    private void CreateTitles()
    {
        _titles = new TitleRasterizer(_gpu!);
        _titles.Changed += (_, _) =>
        {
            Interlocked.Exchange(ref _titleReady, 1);
            Wake();
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

        bool videoChanged = PublishVideoBoxes(frame);
        if (count == 0 && current.Length == 0)
        {
            if (videoChanged)
            {
                TitleBoxesChanged?.Invoke();
            }

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
            if (videoChanged)
            {
                TitleBoxesChanged?.Invoke();
            }

            return;
        }

        Volatile.Write(ref _titleBoxes, boxes);
        TitleBoxesChanged?.Invoke();
    }

    /// <summary>Las capas de video del último cuadro (con su tamaño en pantalla, que se ajusta al cuadro), de abajo arriba.</summary>
    public IReadOnlyList<TitleBox> VideoBoxes => Volatile.Read(ref _videoBoxes);

    private TitleBox[] _videoBoxes = [];

    private bool PublishVideoBoxes(CompositionFrame frame)
    {
        var boxes = new List<TitleBox>(frame.Layers.Count);
        foreach (LayerFrame layer in frame.Layers)
        {
            if (layer.Generated is not null)
            {
                continue;
            }

            (int width, int height) = layer.Nested is not null ? (frame.Width, frame.Height)
                : layer.DisplayWidth > 0 && layer.DisplayHeight > 0 ? (layer.DisplayWidth, layer.DisplayHeight)
                : layer.Frame.Payload is Rodaje.Media.Gpu.D3D11TexturePayload payload
                    ? new SourceGeometry(layer.SampleAspectRatio, layer.Rotation).DisplaySize(payload.Width, payload.Height)
                    : (0, 0);
            if (width > 0 && height > 0)
            {
                boxes.Add(new TitleBox(layer.ClipId, layer.Transform, width, height) { Fitted = true });
            }
        }

        TitleBox[] current = _videoBoxes;
        if (boxes.Count == current.Length && boxes.SequenceEqual(current))
        {
            return false;
        }

        Volatile.Write(ref _videoBoxes, [.. boxes]);
        return true;
    }
}
