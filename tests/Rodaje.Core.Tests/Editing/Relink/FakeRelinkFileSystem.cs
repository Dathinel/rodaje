using Rodaje.Core.Editing;

namespace Rodaje.Core.Tests.Editing.Relink;

/// <summary>Un disco en memoria para la búsqueda: cada archivo con sus datos de medio y su contenido inicial.</summary>
internal sealed class FakeRelinkFileSystem : IRelinkFileSystem
{
    private readonly Dictionary<string, (RelinkProbe? Probe, string Head)> _files = new(StringComparer.Ordinal);

    public List<string> Probed { get; } = [];

    public int Fingerprints { get; private set; }

    public FakeRelinkFileSystem Add(string path, RelinkProbe? probe, string head = "")
    {
        _files[path] = (probe, head.Length > 0 ? head : path);
        return this;
    }

    public IEnumerable<RelinkFile> EnumerateFiles(string folder, CancellationToken cancellationToken) =>
        _files.Keys.Where(p => p.StartsWith(folder, StringComparison.Ordinal)).Select(p => new RelinkFile(p, 1000));

    public RelinkProbe? Probe(string path)
    {
        Probed.Add(path);
        return _files[path].Probe;
    }

    public string? Fingerprint(string path, int bytes)
    {
        Fingerprints++;
        return _files[path].Head;
    }
}
