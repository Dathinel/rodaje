using System.Diagnostics.CodeAnalysis;

namespace Rodaje.Paquetes.Content;

/// <summary>
/// El contenido de los paquetes instalados, por referencia <c>paquete@versión/elemento</c>. Las LUT se cargan fuera del
/// hilo que las pide y quedan en una caché con tope de memoria; los preajustes (pocos KB) se leen al pedirlos y se
/// guardan. Si la versión instalada no es la de la referencia, se usa la instalada.
/// </summary>
/// <remarks>
/// La app lo envuelve en su <c>IEffectContent</c> (el motor no conoce este proyecto); ver «Contenido en la app» en
/// <c>docs/formato-paquete.md</c>.
/// </remarks>
public sealed class PackageContentStore : IDisposable
{
    /// <summary>Tope de memoria de las LUT en caché: unas 40 de 65³.</summary>
    public const long DefaultLutBudgetBytes = 128L * 1024 * 1024;

    /// <summary>Prefijo de una opción que nombra otro elemento del mismo paquete (<c>./calido</c>).</summary>
    public const string SamePackagePrefix = "./";

    private readonly Lock _gate = new();
    private readonly long _lutBudget;
    private readonly Dictionary<string, LutSlot> _luts = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();
    private readonly Dictionary<string, (object? Value, string? Problem)> _presets = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _loads = [];
    private PackageScan _scan;
    private long _lutBytes;
    private int _generation;
    private bool _disposed;

    public PackageContentStore(PackageScan scan, long lutBudgetBytes = DefaultLutBudgetBytes)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lutBudgetBytes);
        _scan = scan;
        _lutBudget = lutBudgetBytes;
    }

    /// <summary>
    /// Terminó de cargar una LUT o cambió lo instalado: en pausa hay que volver a componer. Llega en un hilo cualquiera.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>Bytes de las LUT que hay en memoria.</summary>
    public long LutBytes
    {
        get
        {
            lock (_gate)
            {
                return _lutBytes;
            }
        }
    }

    /// <summary>Tras instalar o desinstalar: olvida lo cargado (también lo que faltaba) y avisa con <see cref="Changed"/>.</summary>
    public void Reload(PackageScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        lock (_gate)
        {
            _scan = scan;
            _generation++;
            _luts.Clear();
            _recent.Clear();
            _lutBytes = 0;
            _presets.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// La LUT si ya está en memoria. Si no, empieza a cargarla en el grupo de hilos y devuelve <c>false</c>; al terminar
    /// avisa con <see cref="Changed"/>. Nunca lee el disco ni espera: se puede llamar desde el hilo de render.
    /// </summary>
    public bool TryGetLut(string reference, [NotNullWhen(true)] out LutTable? lut)
    {
        lut = null;
        if (string.IsNullOrEmpty(reference))
        {
            return false;
        }

        string? path;
        int generation;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_luts.TryGetValue(reference, out LutSlot? slot))
            {
                if (slot.Table is not null)
                {
                    _recent.Remove(slot.Node);
                    _recent.AddFirst(slot.Node);
                    lut = slot.Table;
                    return true;
                }

                return false;
            }

            slot = new LutSlot(new LinkedListNode<string>(reference));
            _luts[reference] = slot;
            path = Locate(reference, ItemKind.Lut, out string? problem)?.FilePath;
            if (path is null)
            {
                slot.Problem = problem;
                return false;
            }

            generation = _generation;
        }

        Task load = Task.Run(() => LoadLut(reference, path, generation));
        lock (_gate)
        {
            if (!load.IsCompleted)
            {
                _loads.Add(load);
            }
        }

        _ = load.ContinueWith(t => { lock (_gate) { _loads.Remove(t); } }, TaskScheduler.Default);
        return false;
    }

    /// <summary>Por qué una LUT no está: falta el paquete o el archivo está dañado. Nulo si está o se está cargando.</summary>
    public string? LutProblem(string reference)
    {
        lock (_gate)
        {
            return _luts.TryGetValue(reference, out LutSlot? slot) ? slot.Problem : null;
        }
    }

    /// <summary>Espera a las LUT que se están cargando (pruebas y cierre ordenado).</summary>
    public Task WhenLoaded()
    {
        lock (_gate)
        {
            return Task.WhenAll([.. _loads]);
        }
    }

    /// <summary>Una transición de paquete, o nulo si falta o está dañada (entonces se ve como fundido cruzado).</summary>
    /// <remarks>Lee el archivo la primera vez: se llama al armar el plan, no por cuadro.</remarks>
    public TransitionPreset? FindTransition(string reference) =>
        Find(reference, ItemKind.Transition, (stream, _) => ContentReaders.ReadTransition(stream));

    /// <summary>Un filtro, con las opciones <c>./elemento</c> ya convertidas en referencias completas.</summary>
    public FilterPreset? FindFilter(string reference) =>
        Find(reference, ItemKind.Filter, (stream, owner) =>
        {
            FilterPreset filter = ContentReaders.ReadFilter(stream);
            return filter with { Effects = Resolve(filter.Effects, owner) };
        });

    /// <summary>Un efecto animado, con las opciones <c>./elemento</c> ya convertidas en referencias completas.</summary>
    public EffectPreset? FindEffect(string reference) =>
        Find(reference, ItemKind.Effect, (stream, owner) =>
        {
            EffectPreset effect = ContentReaders.ReadEffect(stream);
            return effect with { Effects = Resolve(effect.Effects, owner) };
        });

    /// <summary>Por qué un filtro, efecto o transición no se pudo usar; nulo si no se pidió o se leyó bien.</summary>
    public string? PresetProblem(string reference)
    {
        lock (_gate)
        {
            return _presets.TryGetValue(reference, out (object? Value, string? Problem) entry) ? entry.Problem : null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _luts.Clear();
            _recent.Clear();
            _presets.Clear();
            _lutBytes = 0;
        }
    }

    /// <summary>Busca el elemento de una referencia en lo instalado. Hay que tener <see cref="_gate"/>.</summary>
    private PackageItemEntry? Locate(string reference, ItemKind kind, out string? problem)
    {
        problem = null;
        if (!PackageReference.TryParse(reference, out PackageReference? parsed))
        {
            problem = $"«{SafeText.Truncate(SafeText.Clean(reference), 80)}» no es una referencia paquete@versión/elemento.";
            return null;
        }

        if (_scan.Find(parsed.PackageId) is not { } package || package.Manifest.Kind != PackageKind.Content)
        {
            problem = $"Falta el paquete «{parsed.PackageId}» ({parsed.Version}).";
            return null;
        }

        PackageItem? item = package.Manifest.Items.FirstOrDefault(i => string.Equals(i.Id, parsed.ItemId, StringComparison.Ordinal));
        if (item is null)
        {
            problem = $"El paquete «{parsed.PackageId}» no trae «{parsed.ItemId}».";
            return null;
        }

        if (item.Kind != kind || item.File is null)
        {
            problem = $"«{parsed.ItemId}» de «{parsed.PackageId}» no es {KindName(kind)}.";
            return null;
        }

        return new PackageItemEntry(package, item);
    }

    private void LoadLut(string reference, string path, int generation)
    {
        LutTable? table = null;
        string? problem = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            table = ContentReaders.ReadCube(stream);
        }
        catch (PackageContentException ex)
        {
            problem = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"No se pudo leer la LUT: {ex.Message}";
        }

        lock (_gate)
        {
            if (_disposed || generation != _generation || !_luts.TryGetValue(reference, out LutSlot? slot) || slot.Table is not null)
            {
                return;
            }

            if (table is null)
            {
                slot.Problem = problem;
                return;
            }

            slot.Table = table;
            _recent.AddFirst(slot.Node);
            _lutBytes += SizeOf(table);
            while (_lutBytes > _lutBudget && _recent.Last is { } oldest && oldest != slot.Node)
            {
                _recent.RemoveLast();
                if (_luts.Remove(oldest.Value, out LutSlot? evicted) && evicted.Table is not null)
                {
                    _lutBytes -= SizeOf(evicted.Table);
                }
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private T? Find<T>(string reference, ItemKind kind, Func<Stream, PackageManifest, T> read)
        where T : class
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        PackageItemEntry? entry;
        int generation;
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_presets.TryGetValue(reference, out (object? Value, string? Problem) cached))
            {
                return cached.Value as T;
            }

            entry = Locate(reference, kind, out string? problem);
            if (entry is null)
            {
                _presets[reference] = (null, problem);
                return null;
            }

            generation = _generation;
        }

        T? value = null;
        string? error = null;
        try
        {
            using var stream = new FileStream(entry.FilePath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            value = read(stream, entry.Package.Manifest);
        }
        catch (PackageContentException ex)
        {
            error = $"«{entry.Item.Name}»: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"No se pudo leer «{entry.Item.Name}»: {ex.Message}";
        }

        lock (_gate)
        {
            if (!_disposed && generation == _generation)
            {
                _presets[reference] = (value, error);
            }
        }

        return value;
    }

    private static List<PresetEffect> Resolve(IReadOnlyList<PresetEffect> effects, PackageManifest owner)
    {
        var resolved = new List<PresetEffect>(effects.Count);
        foreach (PresetEffect effect in effects)
        {
            if (!effect.Options.Values.Any(v => v.StartsWith(SamePackagePrefix, StringComparison.Ordinal)))
            {
                resolved.Add(effect);
                continue;
            }

            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string key, string value) in effect.Options)
            {
                options[key] = value.StartsWith(SamePackagePrefix, StringComparison.Ordinal)
                    && value[SamePackagePrefix.Length..] is { } item && Identifiers.IsValid(item)
                    ? new PackageReference(owner.Id, owner.Version, item).ToString()
                    : value;
            }

            resolved.Add(effect with { Options = options });
        }

        return resolved;
    }

    private static long SizeOf(LutTable table) => (long)table.Rgb.Length * sizeof(float);

    private static string KindName(ItemKind kind) => kind switch
    {
        ItemKind.Lut => "una LUT",
        ItemKind.Transition => "una transición",
        ItemKind.Filter => "un filtro",
        ItemKind.Effect => "un efecto",
        _ => kind.ToString(),
    };

    private sealed class LutSlot(LinkedListNode<string> node)
    {
        public LinkedListNode<string> Node { get; } = node;

        public LutTable? Table { get; set; }

        public string? Problem { get; set; }
    }
}
