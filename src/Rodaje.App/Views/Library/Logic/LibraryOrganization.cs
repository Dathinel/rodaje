namespace Rodaje.App.Views.Library;

/// <summary>Una carpeta de la biblioteca: solo organiza la vista, nunca mueve archivos en el disco.</summary>
public sealed class LibraryFolder
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Carpeta que la contiene; <c>null</c> en la raíz («Todos los medios»).</summary>
    public string? ParentId { get; set; }
}

/// <summary>
/// Carpetas y favoritos de la biblioteca de un proyecto, sin WinUI. Un medio sin carpeta (o con una que ya no existe)
/// está en la raíz. Los ids de medio que ya no están en el proyecto se toleran: la vista solo muestra los que existen.
/// </summary>
public sealed partial class LibraryOrganization
{
    /// <summary>Nombre que recibe una carpeta recién creada (con un número si ya hay otra igual al lado).</summary>
    public const string DefaultFolderName = "Carpeta nueva";

    /// <summary>Largo máximo de un nombre de carpeta, para que quepa en la ficha.</summary>
    public const int MaxNameLength = 80;

    private readonly List<LibraryFolder> _folders = [];

    /// <summary>Índice de <see cref="_folders"/> por id: buscar en la lista hacía cúbica la carga de un archivo con miles de carpetas.</summary>
    private readonly Dictionary<string, LibraryFolder> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _mediaFolders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _favorites = new(StringComparer.Ordinal);

    public IReadOnlyList<LibraryFolder> Folders => _folders;

    /// <summary>Medio → carpeta, solo de los que no están en la raíz.</summary>
    public IReadOnlyDictionary<string, string> MediaFolders => _mediaFolders;

    public IReadOnlyCollection<string> Favorites => _favorites;

    /// <summary>Sube con cada cambio; la vista lo usa para saber si hay que guardar.</summary>
    public int Revision { get; private set; }

    public LibraryFolder? FindFolder(string? folderId)
    {
        if (folderId is null)
        {
            return null;
        }

        // El id de una carpeta tiene setter: si alguien lo cambió, el índice ya no lo encuentra y se busca en la lista.
        return _byId.TryGetValue(folderId, out LibraryFolder? folder) && folder.Id == folderId ? folder : _folders.Find(f => f.Id == folderId);
    }

    /// <summary>Las carpetas que cuelgan directamente de <paramref name="parentId"/>, por nombre.</summary>
    public IReadOnlyList<LibraryFolder> Children(string? parentId)
    {
        string? parent = FindFolder(parentId)?.Id;
        return [.. _folders.Where(f => ParentOf(f) == parent).OrderBy(f => SearchText.Fold(f.Name), StringComparer.Ordinal).ThenBy(f => f.Id, StringComparer.Ordinal)];
    }

    /// <summary>De la raíz a la carpeta (incluida); vacía para la raíz o una carpeta que no existe.</summary>
    public IReadOnlyList<LibraryFolder> PathTo(string? folderId)
    {
        var path = new List<LibraryFolder>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (LibraryFolder? folder = FindFolder(folderId); folder is not null && seen.Add(folder.Id); folder = FindFolder(folder.ParentId))
        {
            path.Add(folder);
        }

        path.Reverse();
        return path;
    }

    /// <summary>Todas las carpetas en orden de árbol, con su profundidad (0 en la raíz), para el menú «Mover a».</summary>
    /// <remarks>
    /// Agrupa los hijos una sola vez y recorre sin recursión: pedir los hijos de cada carpeta era cuadrático y una cadena
    /// de miles de carpetas anidadas (un archivo dañado) desbordaba la pila del hilo de la interfaz.
    /// </remarks>
    public IReadOnlyList<(LibraryFolder Folder, int Depth)> Tree()
    {
        var root = new List<LibraryFolder>();
        var children = new Dictionary<string, List<LibraryFolder>>(StringComparer.Ordinal);
        foreach (LibraryFolder folder in _folders)
        {
            if (ParentOf(folder) is { } parent)
            {
                if (!children.TryGetValue(parent, out List<LibraryFolder>? list))
                {
                    children[parent] = list = [];
                }

                list.Add(folder);
            }
            else
            {
                root.Add(folder);
            }
        }

        // El mismo orden que Children: por nombre sin tildes ni mayúsculas y luego por id.
        static List<LibraryFolder> Sorted(List<LibraryFolder> list) =>
            [.. list.OrderBy(f => SearchText.Fold(f.Name), StringComparer.Ordinal).ThenBy(f => f.Id, StringComparer.Ordinal)];

        var result = new List<(LibraryFolder, int)>();
        var stack = new Stack<(LibraryFolder Folder, int Depth)>();
        foreach (LibraryFolder folder in Enumerable.Reverse(Sorted(root)))
        {
            stack.Push((folder, 0));
        }

        while (stack.TryPop(out (LibraryFolder Folder, int Depth) item))
        {
            result.Add(item);
            if (children.TryGetValue(item.Folder.Id, out List<LibraryFolder>? list))
            {
                foreach (LibraryFolder child in Enumerable.Reverse(Sorted(list)))
                {
                    stack.Push((child, item.Depth + 1));
                }
            }
        }

        return result;
    }

    /// <summary>Carpeta del medio, o <c>null</c> si está en la raíz (también si su carpeta ya no existe).</summary>
    public string? FolderOf(string mediaId) =>
        _mediaFolders.TryGetValue(mediaId, out string? folder) && FindFolder(folder) is not null ? folder : null;

    /// <summary>Crea una carpeta dentro de <paramref name="parentId"/> con un nombre libre a su lado.</summary>
    public LibraryFolder CreateFolder(string? parentId, string? name = null)
    {
        string? parent = FindFolder(parentId)?.Id;
        string baseName = Clean(name) is { Length: > 0 } clean ? clean : DefaultFolderName;
        string unique = baseName;
        for (int n = 2; NameTaken(parent, unique, exceptId: null); n++)
        {
            unique = $"{baseName} {n}";
        }

        var folder = new LibraryFolder { Id = NewId(), Name = unique, ParentId = parent };
        AddFolder(folder);
        Revision++;
        return folder;
    }

    /// <summary>Por qué no se puede usar el nombre, o <c>null</c> si se puede.</summary>
    public string? NameProblem(string folderId, string? name)
    {
        LibraryFolder? folder = FindFolder(folderId);
        if (folder is null)
        {
            return "La carpeta ya no existe.";
        }

        string clean = Clean(name);
        if (clean.Length == 0)
        {
            return "Escribe un nombre.";
        }

        return NameTaken(ParentOf(folder), clean, folder.Id) ? $"Ya hay una carpeta «{clean}» aquí." : null;
    }

    /// <summary>Cambia el nombre si es válido (<see cref="NameProblem"/>); devuelve si cambió.</summary>
    public bool Rename(string folderId, string? name)
    {
        if (NameProblem(folderId, name) is not null)
        {
            return false;
        }

        LibraryFolder folder = FindFolder(folderId)!;
        string clean = Clean(name);
        if (folder.Name == clean)
        {
            return false;
        }

        folder.Name = clean;
        Revision++;
        return true;
    }

    /// <summary>Borra la carpeta; sus medios y subcarpetas pasan a la carpeta que la contenía (ningún medio se pierde).</summary>
    public bool DeleteFolder(string folderId)
    {
        if (FindFolder(folderId) is not { } folder)
        {
            return false;
        }

        string? parent = ParentOf(folder);
        foreach (LibraryFolder child in _folders.Where(f => f.ParentId == folder.Id))
        {
            child.ParentId = parent;
            child.Name = FreeName(parent, child.Name, child.Id);
        }

        foreach (string mediaId in _mediaFolders.Where(p => p.Value == folder.Id).Select(p => p.Key).ToList())
        {
            SetFolder(mediaId, parent);
        }

        _folders.Remove(folder);
        _byId.Remove(folder.Id);
        Revision++;
        return true;
    }

    /// <summary>Mueve una carpeta dentro de otra (o a la raíz); no deja meterla dentro de sí misma.</summary>
    public bool MoveFolder(string folderId, string? targetParentId)
    {
        if (FindFolder(folderId) is not { } folder)
        {
            return false;
        }

        string? target = FindFolder(targetParentId)?.Id;
        if (target == ParentOf(folder) || (target is not null && PathTo(target).Any(f => f.Id == folder.Id)))
        {
            return false;
        }

        folder.ParentId = target;
        folder.Name = FreeName(target, folder.Name, folder.Id);
        Revision++;
        return true;
    }

    /// <summary>Pone los medios en la carpeta (<c>null</c>: la raíz); devuelve cuántos cambiaron de lugar.</summary>
    public int MoveMedia(IEnumerable<string> mediaIds, string? folderId)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);
        string? target = FindFolder(folderId)?.Id;
        int moved = 0;
        foreach (string mediaId in mediaIds.Distinct(StringComparer.Ordinal))
        {
            if (FolderOf(mediaId) != target)
            {
                SetFolder(mediaId, target);
                moved++;
            }
        }

        if (moved > 0)
        {
            Revision++;
        }

        return moved;
    }

    public bool IsFavorite(string mediaId) => _favorites.Contains(mediaId);

    public void SetFavorite(string mediaId, bool favorite)
    {
        if (favorite ? _favorites.Add(mediaId) : _favorites.Remove(mediaId))
        {
            _rejected.Remove(mediaId);
            Revision++;
        }
    }

    /// <summary>Marca o desmarca; devuelve el estado nuevo.</summary>
    public bool ToggleFavorite(string mediaId)
    {
        bool favorite = !IsFavorite(mediaId);
        SetFavorite(mediaId, favorite);
        return favorite;
    }

    /// <summary>Olvida los medios que ya no están en el proyecto y las entradas que apuntan a carpetas borradas.</summary>
    public void Prune(IEnumerable<string> existingMediaIds)
    {
        ArgumentNullException.ThrowIfNull(existingMediaIds);
        var existing = new HashSet<string>(existingMediaIds, StringComparer.Ordinal);
        int removed = _favorites.RemoveWhere(id => !existing.Contains(id)) + PruneMarks(existing);
        foreach (string mediaId in _mediaFolders.Keys.ToList())
        {
            if (!existing.Contains(mediaId) || FindFolder(_mediaFolders[mediaId]) is null)
            {
                _mediaFolders.Remove(mediaId);
                removed++;
            }
        }

        if (removed > 0)
        {
            Revision++;
        }
    }

    /// <summary>Arma la organización desde lo leído del disco, descartando lo que no tiene sentido.</summary>
    /// <remarks>
    /// Se descartan carpetas sin id o repetidas; un padre que no existe pasa a la raíz, igual que la primera carpeta de un
    /// ciclo, y los nombres vacíos o repetidos al lado reciben uno libre.
    /// </remarks>
    public static LibraryOrganization From(IEnumerable<LibraryFolder>? folders, IReadOnlyDictionary<string, string>? mediaFolders, IEnumerable<string>? favorites)
    {
        var organization = new LibraryOrganization();
        foreach (LibraryFolder folder in folders ?? [])
        {
            if (folder is null || string.IsNullOrWhiteSpace(folder.Id) || organization._byId.ContainsKey(folder.Id))
            {
                continue;
            }

            organization.AddFolder(new LibraryFolder { Id = folder.Id, Name = Clean(folder.Name), ParentId = folder.ParentId });
        }

        foreach (LibraryFolder folder in organization._folders)
        {
            if (organization.FindFolder(folder.ParentId) is null)
            {
                folder.ParentId = null;
            }
        }

        organization.BreakCycles();
        organization.AssignFreeNames();

        foreach ((string mediaId, string folderId) in mediaFolders ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrEmpty(mediaId) && organization.FindFolder(folderId) is not null)
            {
                organization._mediaFolders[mediaId] = folderId;
            }
        }

        foreach (string mediaId in favorites ?? [])
        {
            if (!string.IsNullOrEmpty(mediaId))
            {
                organization._favorites.Add(mediaId);
            }
        }

        return organization;
    }

    /// <summary>Sin espacios a los lados ni caracteres de control, y como mucho <see cref="MaxNameLength"/> caracteres.</summary>
    public static string Clean(string? name)
    {
        string text = new([.. (name ?? "").Where(c => !char.IsControl(c))]);
        text = text.Trim();
        return text.Length > MaxNameLength ? text[..MaxNameLength].TrimEnd() : text;
    }

    private string? ParentOf(LibraryFolder folder) => FindFolder(folder.ParentId)?.Id;

    private void AddFolder(LibraryFolder folder)
    {
        _folders.Add(folder);
        _byId[folder.Id] = folder;
    }

    /// <summary>
    /// Un ciclo (a dentro de b y b dentro de a) no tiene raíz: la primera de sus carpetas en el orden del archivo sube a la
    /// raíz y las demás, y las que colgaban del ciclo, quedan donde estaban. Cada carpeta se recorre una sola vez: subir por
    /// la cadena desde cada carpeta era cúbico con miles de carpetas encadenadas (la app se colgaba al abrir el proyecto).
    /// </summary>
    private void BreakCycles()
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < _folders.Count; i++)
        {
            order[_folders[i].Id] = i;
        }

        // 1: en el camino que se está recorriendo; 2: ya se sabe que llega a la raíz.
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<LibraryFolder>();
        foreach (LibraryFolder start in _folders)
        {
            path.Clear();
            LibraryFolder? up = start;
            while (up is not null && !state.ContainsKey(up.Id))
            {
                state[up.Id] = 1;
                path.Add(up);
                up = FindFolder(up.ParentId);
            }

            if (up is not null && state[up.Id] == 1)
            {
                // Cerró un ciclo: desde «up» hasta el final del camino.
                int first = path.FindIndex(f => f.Id == up.Id);
                LibraryFolder cut = path.Skip(first).MinBy(f => order[f.Id])!;
                cut.ParentId = null;
            }

            foreach (LibraryFolder folder in path)
            {
                state[folder.Id] = 2;
            }
        }
    }

    /// <summary>
    /// Nombres vacíos o repetidos al lado reciben uno libre, en orden y comparando con el nombre actual de las demás (como
    /// <see cref="FreeName"/>), pero con un recuento por carpeta en vez de recorrer todas las carpetas por cada intento.
    /// </summary>
    private void AssignFreeNames()
    {
        var taken = new Dictionary<(string? Parent, string Folded), int>();
        foreach (LibraryFolder folder in _folders)
        {
            Count(taken, (folder.ParentId, SearchText.Fold(folder.Name)), +1);
        }

        var nextNumber = new Dictionary<(string? Parent, string Base), int>();
        foreach (LibraryFolder folder in _folders)
        {
            Count(taken, (folder.ParentId, SearchText.Fold(folder.Name)), -1);
            string name = folder.Name.Length == 0 ? DefaultFolderName : folder.Name;
            string unique = name;
            if (taken.ContainsKey((folder.ParentId, SearchText.Fold(unique))))
            {
                // Se sigue desde el último número que se dio a este nombre aquí: cien mil carpetas iguales no son cuadráticas.
                int n = nextNumber.GetValueOrDefault((folder.ParentId, name), 2);
                do
                {
                    unique = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name} {n}");
                    n++;
                }
                while (taken.ContainsKey((folder.ParentId, SearchText.Fold(unique))));

                nextNumber[(folder.ParentId, name)] = n;
            }

            folder.Name = unique;
            Count(taken, (folder.ParentId, SearchText.Fold(unique)), +1);
        }

        static void Count(Dictionary<(string?, string), int> counts, (string?, string) key, int delta)
        {
            int value = counts.GetValueOrDefault(key) + delta;
            if (value > 0)
            {
                counts[key] = value;
            }
            else
            {
                counts.Remove(key);
            }
        }
    }

    private void SetFolder(string mediaId, string? folderId)
    {
        if (folderId is null)
        {
            _mediaFolders.Remove(mediaId);
        }
        else
        {
            _mediaFolders[mediaId] = folderId;
        }
    }

    private bool NameTaken(string? parentId, string name, string? exceptId)
    {
        string folded = SearchText.Fold(name);
        return _folders.Any(f => f.Id != exceptId && ParentOf(f) == parentId && SearchText.Fold(f.Name) == folded);
    }

    /// <summary>El nombre, o el nombre con un número, que no choca con otra carpeta en <paramref name="parentId"/>.</summary>
    private string FreeName(string? parentId, string name, string exceptId)
    {
        string unique = name;
        for (int n = 2; NameTaken(parentId, unique, exceptId); n++)
        {
            unique = $"{name} {n}";
        }

        return unique;
    }

    /// <summary>
    /// Un id que no se repite aunque se borren carpetas: «folder-N» volvía a dar el de una carpeta borrada, y lo que aún
    /// apuntaba a ella (un diálogo abierto, la organización de una copia) caía en la carpeta nueva.
    /// </summary>
    private string NewId()
    {
        while (true)
        {
            string id = "folder-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
            if (FindFolder(id) is null)
            {
                return id;
            }
        }
    }
}
