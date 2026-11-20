using System.Reflection;
using Dathinel.Sala;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// Lo que la ventana tiene de la colaboración: la sala, dónde está <c>dathinel-red</c>, la clave de este equipo y el último
/// nombre que usó la persona. Sin WinUI para poder probarlo; la ventana le pasa cómo volver al hilo de la interfaz.
/// </summary>
public sealed partial class CollabHost : IDisposable
{
    public const string RedFileName = "dathinel-red.exe";
    public const string RedVariable = "RODAJE_RED";

    private readonly string _folder;

    public CollabHost(string dataRoot, Action<Action> post, string? appVersion = null, string? redPath = null, ICollabPayloadHandler? handler = null)
    {
        _folder = Path.Combine(dataRoot, "colaboracion");
        try
        {
            Directory.CreateDirectory(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin carpeta de datos, abrir una sala dirá que no pudo leer la clave del equipo; el resto de Rodaje no se entera.
        }

        Room = new RoomService(
            new CollabEnvironment(redPath ?? FindRed() ?? Path.Combine(AppContext.BaseDirectory, RedFileName), Path.Combine(_folder, "identidad.bin"), appVersion ?? CurrentVersion()),
            handler,
            post);
        Room.StateChanged += OnRoomState;
        Presence = new CollabPresence(Room);
    }

    public RoomService Room { get; }

    /// <summary>Los cabezales de los demás y las pistas reservadas, para dibujarlos en la línea de tiempo.</summary>
    public CollabPresence Presence { get; }

    private EditorSession? _session;
    private string[] _selection = [];
    private string? _selectionTrack;

    /// <summary>Engancha la sesión del editor: su cabezal y su selección son lo que ven los demás, y las pistas de lo seleccionado se reservan.</summary>
    public void BindSession(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        session.TransportChanged += (_, _) => PublishLocal();
        session.SelectionChanged += (_, _) =>
        {
            RefreshSelection();
            PublishLocal();
        };
    }

    private void RefreshSelection()
    {
        if (_session is not { } session)
        {
            return;
        }

        _selection = [.. session.SelectedClipIds.Take(CollabPresence.MaxSelected)];
        HashSet<string> ids = [.. _selection];
        List<string> tracks = [];
        foreach (Track track in session.Sequence.VideoTracks.Concat(session.Sequence.AudioTracks))
        {
            if (track.Clips.Any(c => ids.Contains(c.Id)))
            {
                tracks.Add(track.Id);
            }
        }

        _selectionTrack = tracks.Count > 0 ? tracks[0] : null;
        Presence.WantTracks(tracks);
    }

    private void PublishLocal()
    {
        if (_session is { } session && Room.State.InRoom)
        {
            Presence.SetLocal(session.ProgramPosition.ToSeconds(), _selection, _selectionTrack);
        }
    }

    /// <summary>De dónde saca el anfitrión la fotografía del proyecto para quien entra; la pone la ventana.</summary>
    public ICollabProjectSource? ProjectSource { get; set; }

    private ProjectShareHost? _share;

    public void Dispose()
    {
        Room.StateChanged -= OnRoomState;
        Presence.Dispose();
        _share?.Dispose();
        _share = null;
    }

    private void OnRoomState(RoomState state)
    {
        if (state is { Phase: RoomPhase.Hosting, IsHost: true } && _share is null && ProjectSource is { } source)
        {
            _share = new ProjectShareHost(Room, source, Room.RoomFolder);
        }
        else if (state.Phase is RoomPhase.Closed or RoomPhase.Ended && _share is not null)
        {
            _share.Dispose();
            _share = null;
        }
    }

    /// <summary>El permiso que se propone al aprobar a quien pide entrar (el que se eligió al invitar).</summary>
    public Rol DefaultRole { get; set; } = Rol.Editar;

    /// <summary>La versión de Rodaje sin el commit (<c>1.0.4+abc123</c> → <c>1.0.4</c>): solo entran las iguales.</summary>
    public static string CurrentVersion()
    {
        string v = typeof(CollabHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(CollabHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        int plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? v[..plus] : v;
    }

    /// <summary>Dónde está <c>dathinel-red</c>: la variable, junto a Rodaje, en <c>modulos\red</c> o, en desarrollo, en el repositorio.</summary>
    public static string? FindRed()
    {
        if (Environment.GetEnvironmentVariable(RedVariable) is { Length: > 0 } fromEnv && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        string exe = OperatingSystem.IsWindows() ? RedFileName : "dathinel-red";
        string here = AppContext.BaseDirectory;
        foreach (string candidate in new[] { Path.Combine(here, exe), Path.Combine(here, "modulos", "red", exe) })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        for (DirectoryInfo? d = new(here); d is not null; d = d.Parent)
        {
            if (!File.Exists(Path.Combine(d.FullName, "Rodaje.slnx")))
            {
                continue;
            }

            foreach (string profile in new[] { "release", "debug" })
            {
                string dev = Path.Combine(d.FullName, "externo", "sala", "red", "target", profile, exe);
                if (File.Exists(dev))
                {
                    return dev;
                }
            }
        }

        return null;
    }

    /// <summary>La carpeta de la sala de un proyecto: <c>Mi proyecto.sala</c> junto a <c>Mi proyecto.rodaje</c>.</summary>
    public static string RoomFolderFor(string projectFile) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectFile))!, Path.GetFileNameWithoutExtension(projectFile) + ".sala");

    /// <summary>El último nombre que usó la persona en una sala, para no pedírselo cada vez.</summary>
    public string Alias
    {
        get
        {
            try
            {
                string path = Path.Combine(_folder, "nombre.txt");
                return File.Exists(path) ? CollabNames.NormalizeAlias(File.ReadAllText(path)) ?? "" : "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "";
            }
        }

        set
        {
            if (CollabNames.NormalizeAlias(value) is not { } alias)
            {
                return;
            }

            try
            {
                AppDataPaths.WriteAllTextAtomic(Path.Combine(_folder, "nombre.txt"), alias);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No recordar el nombre no impide colaborar.
            }
        }
    }

    /// <summary>Un nombre para proponer la primera vez: el del usuario de Windows.</summary>
    public static string SuggestedAlias() => CollabNames.NormalizeAlias(Environment.UserName) ?? "";
}
