using Rodaje.Core.Model;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Text;

/// <summary>Una fuente ya elegida para un estilo: la colección donde está, el nombre de la familia y si es la pedida.</summary>
internal readonly unsafe struct ResolvedFont(IDWriteFontCollection* collection, string family, bool missing)
{
    public IDWriteFontCollection* Collection { get; } = collection;

    public string Family { get; } = family;

    public bool Missing { get; } = missing;
}

/// <summary>
/// Fuentes de un rasterizador: la colección privada con las de los paquetes (registradas con
/// <c>IDWriteFactory5::CreateFontSetBuilder</c>, nunca instaladas en Windows) y la del sistema como respaldo.
/// </summary>
/// <remarks>Solo se usa con el candado del rasterizador tomado.</remarks>
internal sealed unsafe class TitleFonts : IDisposable
{
    /// <summary>Tope de un archivo de fuente (D-029: 20 MB por archivo).</summary>
    public const long MaxFileBytes = 20L * 1024 * 1024;

    /// <summary>La familia de último recurso, presente en todo Windows.</summary>
    public const string LastResort = "Segoe UI";

    private readonly IDWriteFactory5* _factory;
    private readonly Dictionary<string, ResolvedFont> _resolved = new(StringComparer.Ordinal);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private IDWriteFontCollection* _system;
    private IDWriteFontSetBuilder1* _builder;
    private IDWriteFontCollection1* _private;

    public TitleFonts(IDWriteFactory5* factory)
    {
        _factory = factory;
        IDWriteFontCollection* system;
        Check(factory->GetSystemFontCollection(&system, false), "GetSystemFontCollection");
        _system = system;
    }

    /// <summary>Archivos registrados en la colección privada.</summary>
    public int FileCount => _files.Count;

    /// <summary>
    /// Agrega archivos <c>.ttf</c>/<c>.otf</c> de un paquete a la colección privada. Devuelve los que DirectWrite rechazó
    /// o que pasan del tope: esa familia queda con su respaldo.
    /// </summary>
    public List<string> Register(IEnumerable<string> paths)
    {
        var rejected = new List<string>();
        if (_builder == null)
        {
            IDWriteFontSetBuilder1* builder;
            Check(_factory->CreateFontSetBuilder(&builder), "CreateFontSetBuilder");
            _builder = builder;
        }

        bool added = false;
        foreach (string path in paths)
        {
            if (_files.Contains(path))
            {
                continue;
            }

            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
            {
                rejected.Add(path);
                continue;
            }

            IDWriteFontFile* file = null;
            HRESULT hr;
            fixed (char* p = path)
            {
                hr = _factory->CreateFontFileReference(p, null, &file);
            }

            BOOL supported = false;
            DWRITE_FONT_FILE_TYPE fileType;
            DWRITE_FONT_FACE_TYPE faceType;
            uint faces;
            if (hr.SUCCEEDED)
            {
                hr = file->Analyze(&supported, &fileType, &faceType, &faces);
            }

            if (hr.SUCCEEDED && supported)
            {
                hr = _builder->AddFontFile(file);
            }

            if (file != null)
            {
                file->Release();
            }

            if (hr.FAILED || !supported)
            {
                rejected.Add(path);
                continue;
            }

            _files.Add(path);
            added = true;
        }

        if (added && OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            Rebuild();
        }

        return rejected;
    }

    /// <summary>
    /// La primera familia de <c>[pedida, respaldos…]</c> que esté en la colección privada o en la del sistema; si no hay
    /// ninguna, <see cref="LastResort"/>. <see cref="ResolvedFont.Missing"/> dice si la pedida faltaba.
    /// </summary>
    public ResolvedFont Resolve(StyleFont font)
    {
        string key = font.Family + "\u001f" + string.Join('\u001f', font.Fallback);
        if (_resolved.TryGetValue(key, out ResolvedFont known))
        {
            return known;
        }

        ResolvedFont resolved = Find(font.Family) is { } exact
            ? exact
            : font.Fallback.Select(Find).FirstOrDefault(f => f is not null) is { } fallback
                ? new ResolvedFont(fallback.Collection, fallback.Family, missing: true)
                : new ResolvedFont(_system, LastResort, missing: true);
        _resolved[key] = resolved;
        return resolved;
    }

    public void Dispose()
    {
        _resolved.Clear();
        Com.Release(ref _private);
        Com.Release(ref _builder);
        Com.Release(ref _system);
    }

    private ResolvedFont? Find(string family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return null;
        }

        if (_private != null && Contains((IDWriteFontCollection*)_private, family))
        {
            return new ResolvedFont((IDWriteFontCollection*)_private, family, false);
        }

        return Contains(_system, family) ? new ResolvedFont(_system, family, false) : null;
    }

    private static bool Contains(IDWriteFontCollection* collection, string family)
    {
        uint index;
        BOOL exists = false;
        fixed (char* name = family)
        {
            return collection->FindFamilyName(name, &index, &exists).SUCCEEDED && exists;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0")]
    private void Rebuild()
    {
        IDWriteFontSet* set = null;
        IDWriteFontCollection1* collection = null;
        try
        {
            Check(_builder->CreateFontSet(&set), "CreateFontSet");
            Check(_factory->CreateFontCollectionFromFontSet(set, &collection), "CreateFontCollectionFromFontSet");
        }
        finally
        {
            Com.Release(ref set);
        }

        Com.Release(ref _private);
        _private = collection;
        _resolved.Clear();
    }
}
