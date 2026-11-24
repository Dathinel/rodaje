using System.Text.Json;
using Dathinel.Sala;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Operations;
using Rodaje.App.Services.Settings;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// El pedido «Exportar en el anfitrión» ya comprobado: solo los ajustes, el rango, la secuencia y un nombre sugerido.
/// Nunca trae una ruta: el destino lo elige el anfitrión y el archivo queda en su equipo.
/// </summary>
public sealed record HostExportAsk(string Id, string FileName, ExportSettings Settings, ExportRange? Range, string SequenceId);

/// <summary>Lo que enseña el aviso de aprobación a mano: quién pide («Luis») y qué sale («Boda.mp4» · MP4 · H.264 · 1:23).</summary>
public sealed record HostExportApproval(string Alias, string Detail);

/// <summary>La copia confirmada del proyecto del anfitrión, con las rutas reales de sus archivos, lista para exportar.</summary>
public sealed record ConfirmedExportProject(Project Project, string? ProjectDirectory);

/// <summary>
/// Los mensajes directos con los que el invitado pide «Exportar en el anfitrión» y el anfitrión responde. Se tratan como
/// hostiles: tamaño acotado, campos conocidos, valores comprobados y nada de rutas del que pide.
/// </summary>
internal static class ExportShareMessages
{
    /// <summary>Invitado → anfitrión: «exporta esto en tu equipo».</summary>
    public const string Ask = "exportar-pedir";

    /// <summary>Anfitrión → invitado: porcentaje y fase (también sirve de «aceptado»: la primera que llega lo confirma).</summary>
    public const string Progress = "exportar-progreso";

    /// <summary>Anfitrión → invitado: terminó; el archivo quedó en el equipo del anfitrión.</summary>
    public const string Done = "exportar-listo";

    /// <summary>Anfitrión → invitado: no se pudo (rechazada, fallada o cancelada; <c>motivo</c> lo distingue).</summary>
    public const string Failed = "exportar-error";

    /// <summary>Invitado → anfitrión: cancela la que pedí.</summary>
    public const string Cancel = "exportar-cancelar";

    /// <summary>El JSON del pedido entero no puede pasar de aquí; los ajustes caben de sobra (el directo de Sala admite ~900 KiB).</summary>
    public const long MaxAskBytes = 256L << 10;

    public const int MaxNameLength = 160;
    public const int MaxSequenceLength = 128;
    public const int MaxPhaseLength = 120;
    public const int MaxTextLength = 300;

    /// <summary>Un solo pedido a la vez por miembro y una cola global acotada en el equipo del anfitrión.</summary>
    public const int MaxActive = 8;

    /// <summary>Rango máximo admitido en el pedido (una semana de metraje: por encima es un pedido roto).</summary>
    public static readonly long MaxRangeTicks = Time.FromSeconds(7L * 24 * 3600).Ticks;

    private static readonly HashSet<string> AskFields = new(StringComparer.Ordinal)
    {
        "tipo", "id", "nombre", "secuencia", "ajustes", "rango",
    };

    /// <summary>«{tipo, id, nombre, secuencia, ajustes, rango?}». El nombre se manda sugerido; el anfitrión lo limpia otra vez.</summary>
    public static JsonElement MakeAsk(string id, string sequenceId, string fileName, ExportSettings settings, ExportRange? range) =>
        ShareMessages.Make(Ask, w =>
        {
            w.WriteString("id", id);
            w.WriteString("nombre", fileName);
            w.WriteString("secuencia", sequenceId);
            w.WritePropertyName("ajustes");
            JsonSerializer.SerializeToElement(settings, ExportQueueJsonContext.Default.ExportSettings).WriteTo(w);
            if (range is { } r)
            {
                w.WriteStartObject("rango");
                w.WriteNumber("inicio", r.Start.Ticks);
                w.WriteNumber("fin", r.End.Ticks);
                w.WriteEndObject();
            }
        });

    /// <summary>«{tipo, id, fase, fraccion?, espera?}»: la fase ya viene en español para enseñarla tal cual.</summary>
    public static JsonElement MakeProgress(string id, string phase, bool waiting, double? fraction) =>
        ShareMessages.Make(Progress, w =>
        {
            w.WriteString("id", id);
            w.WriteString("fase", phase);
            if (waiting)
            {
                w.WriteBoolean("espera", true);
            }

            if (fraction is { } f)
            {
                w.WriteNumber("fraccion", f);
            }
        });

    /// <summary>«{tipo, id, nombre}»: solo el nombre del archivo, nunca la ruta del anfitrión.</summary>
    public static JsonElement MakeDone(string id, string fileName) =>
        ShareMessages.Make(Done, w =>
        {
            w.WriteString("id", id);
            w.WriteString("nombre", fileName);
        });

    /// <summary>«{tipo, id, texto, motivo}»: texto en español para la persona; motivo para el programa.</summary>
    public static JsonElement MakeError(string id, string text, string motivo) =>
        ShareMessages.Make(Failed, w =>
        {
            w.WriteString("id", id);
            w.WriteString("texto", text);
            w.WriteString("motivo", motivo);
        });

    public static JsonElement MakeCancel(string id) =>
        ShareMessages.Make(Cancel, w => w.WriteString("id", id));

    /// <summary>Si el mensaje responde a este pedido concreto.</summary>
    public static bool MatchesId(JsonElement message, string id) =>
        message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("id", out JsonElement i) && i.ValueKind == JsonValueKind.String
        && i.GetString() == id;

    /// <summary>
    /// Lee «exportar-pedir» de forma estricta: tamaño, campos conocidos, id con su formato, ajustes coherentes y sin
    /// números imposibles, rango válido. Cuando falla, <paramref name="id"/> queda relleno si se pudo leer (para
    /// contestar el error al pedido correcto) y <paramref name="problem"/> dice en español qué no valía.
    /// </summary>
    public static bool TryParseAsk(JsonElement message, out string id, out HostExportAsk? ask, out string problem)
    {
        id = "";
        ask = null;
        problem = "El pedido no se entendió.";
        if (message.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (message.TryGetProperty("id", out JsonElement idProp) && idProp.ValueKind == JsonValueKind.String)
        {
            id = idProp.GetString() ?? "";
        }

        if (message.GetRawText().Length > MaxAskBytes)
        {
            problem = "El pedido es demasiado grande.";
            return false;
        }

        foreach (JsonProperty prop in message.EnumerateObject())
        {
            if (!AskFields.Contains(prop.Name))
            {
                problem = "El pedido trae datos que Rodaje no espera.";
                return false;
            }
        }

        if (!Guid.TryParseExact(id, "N", out _))
        {
            problem = "El pedido no trae su identificador.";
            return false;
        }

        if (!message.TryGetProperty("secuencia", out JsonElement seqProp) || seqProp.ValueKind != JsonValueKind.String
            || seqProp.GetString() is not { Length: > 0 and <= MaxSequenceLength } sequenceId)
        {
            problem = "El pedido no dice qué secuencia exportar.";
            return false;
        }

        string fileName = "";
        if (message.TryGetProperty("nombre", out JsonElement nameProp) && nameProp.ValueKind == JsonValueKind.String)
        {
            fileName = SafeFileName(nameProp.GetString());
        }

        if (!message.TryGetProperty("ajustes", out JsonElement settingsProp) || settingsProp.ValueKind != JsonValueKind.Object)
        {
            problem = "El pedido no trae los ajustes de exportación.";
            return false;
        }

        ExportSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize(settingsProp.GetRawText(), ExportQueueJsonContext.Default.ExportSettings);
        }
        catch (JsonException)
        {
            problem = "Los ajustes del pedido no se entendieron.";
            return false;
        }

        if (settings is null || ExportSettingsNumbers.NonFinite(settings) is not null)
        {
            problem = "Los ajustes del pedido traen números imposibles.";
            return false;
        }

        ExportRange? range = null;
        if (message.TryGetProperty("rango", out JsonElement rangeProp))
        {
            if (rangeProp.ValueKind != JsonValueKind.Object
                || !rangeProp.TryGetProperty("inicio", out JsonElement startProp) || !startProp.TryGetInt64(out long startTicks)
                || !rangeProp.TryGetProperty("fin", out JsonElement endProp) || !endProp.TryGetInt64(out long endTicks)
                || startTicks < 0 || endTicks <= startTicks || endTicks > MaxRangeTicks)
            {
                problem = "El rango del pedido no vale.";
                return false;
            }

            range = new ExportRange(Time.FromTicks(startTicks), Time.FromTicks(endTicks));
        }

        ask = new HostExportAsk(id, fileName.Length > 0 ? fileName : "exportacion", settings, range, sequenceId);
        problem = "";
        return true;
    }

    /// <summary>Lee «exportar-progreso» del anfitrión (viene de confianza, pero se acotan los valores igual).</summary>
    public static (double? Fraction, string Phase, bool Waiting) ReadProgress(JsonElement message)
    {
        double? fraction = null;
        if (message.TryGetProperty("fraccion", out JsonElement f) && f.ValueKind == JsonValueKind.Number
            && f.TryGetDouble(out double value) && double.IsFinite(value))
        {
            fraction = Math.Clamp(value, 0, 1);
        }

        string phase = "";
        if (message.TryGetProperty("fase", out JsonElement p) && p.ValueKind == JsonValueKind.String)
        {
            phase = CleanText(p.GetString(), MaxPhaseLength);
        }

        bool waiting = message.TryGetProperty("espera", out JsonElement w)
            && w.ValueKind is JsonValueKind.True or JsonValueKind.False && w.GetBoolean();
        return (fraction, phase, waiting);
    }

    /// <summary>Lee «exportar-listo»: el nombre del archivo que quedó en el equipo del anfitrión.</summary>
    public static string ReadDone(JsonElement message) =>
        message.TryGetProperty("nombre", out JsonElement n) && n.ValueKind == JsonValueKind.String
            ? SafeFileName(n.GetString())
            : "";

    /// <summary>Lee «exportar-error»: el texto en español y el motivo («rechazada», «cancelada-anfitrion»…).</summary>
    public static (string Text, string Motivo) ReadError(JsonElement message)
    {
        string text = "";
        if (message.TryGetProperty("texto", out JsonElement t) && t.ValueKind == JsonValueKind.String)
        {
            text = CleanText(t.GetString(), MaxTextLength);
        }

        string motivo = "";
        if (message.TryGetProperty("motivo", out JsonElement m) && m.ValueKind == JsonValueKind.String)
        {
            motivo = CleanText(m.GetString(), 40);
        }

        return (text, motivo);
    }

    /// <summary>El nombre sugerido por el invitado reducido a un nombre de archivo seguro (sin carpetas ni caracteres raros).</summary>
    public static string SafeFileName(string? name)
    {
        string clean = MediaManifest.DisplayName(name ?? "");
        clean = new string([.. clean.Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0)]).Trim(' ', '.');
        if (clean.Length > MaxNameLength)
        {
            clean = clean[..MaxNameLength].TrimEnd(' ', '.');
        }

        return clean.Length > 0 ? WindowsFileNames.AvoidReserved(clean) : "";
    }

    /// <summary>Una línea de texto plano: sin controles y acotada, para enseñar en la interfaz o en un aviso.</summary>
    private static string CleanText(string? text, int max)
    {
        string clean = new([.. (text ?? "").Where(c => !char.IsControl(c))]);
        return clean.Length > max ? clean[..max] : clean;
    }
}

/// <summary>
/// El lado del anfitrión de «Exportar en el anfitrión»: recibe el pedido de un invitado, lo trata como hostil, lo muestra
/// para aprobar a mano y, si se acepta, lo encola en la cola de exportación normal sobre la copia confirmada del proyecto.
/// Manda progreso y resultado por directos; el archivo queda en la carpeta de exportaciones de la sala, en este equipo.
/// </summary>
public sealed partial class ExportShareHost : IDisposable
{
    private const int ProgressIntervalMs = 500;

    private readonly RoomService _room;
    private readonly ExportJobList _jobs;
    private readonly Func<CancellationToken, Task<ConfirmedExportProject>> _confirmed;
    private readonly string _exportsFolder;
    private readonly Func<Action, Task> _onUi;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.Ordinal);

    /// <param name="confirmed">La copia confirmada del proyecto con sus rutas reales (la saca <see cref="CollabHost"/> del estado del motor).</param>
    /// <param name="onUi">Cómo correr un paso en el hilo de la interfaz (la cola no es segura entre hilos); en pruebas, nulo.</param>
    public ExportShareHost(RoomService room, ExportJobList jobs, Func<CancellationToken, Task<ConfirmedExportProject>> confirmed,
        string roomFolder, Func<Action, Task>? onUi = null)
    {
        _room = room ?? throw new ArgumentNullException(nameof(room));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _confirmed = confirmed ?? throw new ArgumentNullException(nameof(confirmed));
        _exportsFolder = Path.Combine(Path.GetFullPath(roomFolder), "exportaciones");
        _onUi = onUi ?? (work => { work(); return Task.CompletedTask; });
        room.DirectReceived += OnDirect;
        jobs.Changed += OnJobsChanged;
        jobs.Finished += OnJobFinished;
    }

    /// <summary>Aprobar a mano el pedido («Luis pide exportar el proyecto con…»). Sin ella, todo pedido se rechaza.</summary>
    public Func<HostExportApproval, Task<bool>>? Approve { get; set; }

    /// <summary>Cuántos pedidos activos caben a la vez en el equipo (el límite real es <c>ExportShareMessages.MaxActive</c>; las pruebas lo bajan).</summary>
    public int MaxActive { get; init; } = ExportShareMessages.MaxActive;

    public void Dispose()
    {
        _room.DirectReceived -= OnDirect;
        _jobs.Changed -= OnJobsChanged;
        _jobs.Finished -= OnJobFinished;
        _stop.Cancel();
        _stop.Dispose();
    }

    private void OnDirect(string from, JsonElement message)
    {
        string? type = ShareMessages.Type(message);
        if (type == ExportShareMessages.Ask && _room.State is { IsHost: true, Phase: RoomPhase.Hosting })
        {
            // Clonado: el documento del mensaje puede dejar de vivir al volver el manejador.
            UiTask.Run(Task.Run(() => HandleAskAsync(from, message.Clone())));
        }
        else if (type == ExportShareMessages.Cancel && _room.State.IsHost)
        {
            HandleCancel(from, message);
        }
    }

    /// <summary>«exportar-cancelar»: marca el pedido como cancelado por quien lo hizo y lo saca de la cola o del trabajo.</summary>
    private void HandleCancel(string from, JsonElement message)
    {
        if (!message.TryGetProperty("id", out JsonElement i) || i.ValueKind != JsonValueKind.String || i.GetString() is not { } id)
        {
            return;
        }

        Tracked? tracked;
        lock (_gate)
        {
            if (!_tracked.TryGetValue(id, out tracked) || tracked.From != from)
            {
                return;
            }

            tracked.GuestCancelled = true;
        }

        if (tracked.Entry is { } entry)
        {
            UiTask.Run(_onUi(() => _jobs.Cancel(entry)));
        }
    }

    private async Task HandleAskAsync(string from, JsonElement message)
    {
        if (!ExportShareMessages.TryParseAsk(message, out string id, out HostExportAsk? ask, out string problem) || ask is null)
        {
            if (id.Length > 0)
            {
                await SendAsync(from, ExportShareMessages.MakeError(id, problem, "invalida")).ConfigureAwait(false);
            }

            return;
        }

        RoomMember? member = _room.State.Members.FirstOrDefault(m => m.Key == from);
        if (member is not { Role: Rol.Editar })
        {
            await SendAsync(from, ExportShareMessages.MakeError(id,
                "Con el permiso de solo mirar no puedes pedir exportaciones. Pídele a quien hospeda que te deje editar.",
                "rol")).ConfigureAwait(false);
            return;
        }

        var tracked = new Tracked { Id = ask.Id, From = from };
        string? reject = null;
        lock (_gate)
        {
            if (_tracked.ContainsKey(ask.Id))
            {
                return; // el mismo pedido otra vez (reintento de red): se ignora
            }

            if (_tracked.Values.Any(t => t.From == from))
            {
                reject = "Ya tienes una exportación pedida o en marcha: espera a que termine antes de pedir otra.";
            }
            else if (_tracked.Count >= MaxActive)
            {
                reject = "Ya hay varias exportaciones esperando su turno en este equipo. Vuelve a pedirla en un rato.";
            }
            else
            {
                _tracked[ask.Id] = tracked;
            }
        }

        if (reject is not null)
        {
            await SendAsync(from, ExportShareMessages.MakeError(id, reject, "ocupado")).ConfigureAwait(false);
            return;
        }

        try
        {
            await ServeAsync(tracked, ask, member).ConfigureAwait(false);
        }
        catch (AskException ex)
        {
            Untrack(tracked);
            await SendAsync(from, ExportShareMessages.MakeError(ask.Id, ex.Message, ex.Motivo)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Untrack(tracked);
            await SendAsync(from, ExportShareMessages.MakeError(ask.Id,
                "No se pudo preparar la exportación en este equipo. Vuelve a pedirla.",
                "error")).ConfigureAwait(false);
            _ = UiTask.Report(ex, notify: false);
        }
    }

    /// <summary>Valida los ajustes contra los codificadores de este equipo, aprueba a mano y encola sobre la copia confirmada.</summary>
    private async Task ServeAsync(Tracked tracked, HostExportAsk ask, RoomMember member)
    {
        IReadOnlyList<EncoderSpec> encoders = await _jobs.Runner.GetEncodersAsync(_stop.Token).ConfigureAwait(false);
        ExportSettings settings = ExportSettingsEditor.Coerce(ask.Settings, encoders).Settings;
        if (ExportSettingsEditor.Validate(settings, encoders) is { Count: > 0 } problems)
        {
            throw new AskException("ajustes", "Los ajustes no se pueden exportar en este equipo: " + problems[0].Message);
        }

        ConfirmedExportProject confirmed = await _confirmed(_stop.Token).ConfigureAwait(false);
        if (confirmed.Project.FindSequence(ask.SequenceId) is not { } sequence)
        {
            throw new AskException("secuencia", "Esa secuencia ya no está en el proyecto de la sala.");
        }

        ThrowIfGuestCancelled(tracked);

        HostExportApproval approval = new(member.Alias, Describe(ask.FileName, settings, sequence, ask.Range));
        if (Approve is not { } approve || !await approve(approval).ConfigureAwait(false))
        {
            throw new AskException("rechazada", "Quien hospeda la sala no aceptó la exportación.");
        }

        ThrowIfGuestCancelled(tracked);

        ExportEntry? entry = null;
        await _onUi(() =>
        {
            string output = UniqueOutput(ask.FileName, settings);
            Directory.CreateDirectory(_exportsFolder);
            var request = new ExportRequest(confirmed.Project, ask.SequenceId, output, settings, ask.Range)
            {
                ProjectDirectory = confirmed.ProjectDirectory,
            };
            entry = _jobs.Enqueue(request);
            lock (_gate)
            {
                tracked.Entry = entry;
                tracked.FileName = Path.GetFileName(output);
            }
        }).ConfigureAwait(false);

        if (tracked.GuestCancelled && entry is not null)
        {
            await _onUi(() => _jobs.Cancel(entry)).ConfigureAwait(false);
            throw new AskException("cancelada-invitado", "Cancelaste la exportación.");
        }

        await SendAsync(tracked.From, ExportShareMessages.MakeProgress(tracked.Id, "En la cola de exportaciones", true, null))
            .ConfigureAwait(false);
    }

    private static void ThrowIfGuestCancelled(Tracked tracked)
    {
        if (tracked.GuestCancelled)
        {
            throw new AskException("cancelada-invitado", "Cancelaste la exportación.");
        }
    }

    /// <summary>««Boda.mp4» · MP4 · H.264 · 1920×1080 · 30 fps · 1:23», para el aviso de aprobación.</summary>
    private static string Describe(string fileName, ExportSettings settings, Sequence sequence, ExportRange? range)
    {
        List<string> parts = [$"«{fileName}»", ExportChoices.ContainerName(settings.Container)];
        if (settings.Video is { } video)
        {
            parts.Add(ExportChoices.CodecName(video.Codec));
            (int width, int height) = video.OutputSize(sequence.Width, sequence.Height);
            parts.Add($"{width}×{height}");
            parts.Add($"{ExportText.Number((video.FrameRate ?? sequence.FrameRate).ToDouble(), 3)} fps");
        }
        else if (settings.Audio is { } audio)
        {
            parts.Add(ExportChoices.CodecName(audio.Codec));
        }

        parts.Add(ExportRanges.Clock(range?.Duration ?? sequence.Duration));
        return string.Join(" · ", parts);
    }

    /// <summary>Un nombre único dentro de la carpeta de exportaciones de la sala; el destino siempre lo elige este equipo.</summary>
    private string UniqueOutput(string fileName, ExportSettings settings)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        if (baseName.Length == 0)
        {
            baseName = "exportacion";
        }

        string path = ExportRanges.WithExtension(Path.Combine(_exportsFolder, baseName), settings);
        for (int i = 2; File.Exists(path) || OutputTaken(path); i++)
        {
            path = ExportRanges.WithExtension(Path.Combine(_exportsFolder, $"{baseName} ({i})"), settings);
        }

        return path;
    }

    private bool OutputTaken(string path)
    {
        lock (_gate)
        {
            return _jobs.Entries.Any(e => string.Equals(e.Request.OutputPath, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Manda «exportar-progreso» cuando cambia la fase o ha pasado el intervalo; el estado sale de <see cref="ExportText.State"/>.</summary>
    private void OnJobsChanged(object? sender, EventArgs e)
    {
        List<Tracked> active;
        lock (_gate)
        {
            active = [.. _tracked.Values.Where(t => t.Entry is not null)];
        }

        long now = Environment.TickCount64;
        foreach (Tracked tracked in active)
        {
            if (tracked.Entry is not { } entry || entry.IsFinished)
            {
                continue;
            }

            if (!_jobs.Entries.Contains(entry))
            {
                // El anfitrión la quitó de la cola mientras esperaba: no hay evento Finished.
                if (Untrack(tracked))
                {
                    UiTask.Run(SendAsync(tracked.From, ExportShareMessages.MakeError(tracked.Id,
                        tracked.GuestCancelled ? "Cancelaste la exportación." : "Quien hospeda la sala quitó la exportación de la cola.",
                        tracked.GuestCancelled ? "cancelada-invitado" : "cancelada-anfitrion")));
                }

                continue;
            }

            ExportProgress progress = entry.Progress;
            string phase = ExportText.State(progress);
            bool waiting = progress.State == ExportJobState.Queued;
            double? fraction = progress.State == ExportJobState.Queued ? null : progress.Fraction;
            bool send;
            lock (_gate)
            {
                send = phase != tracked.LastPhase || now - tracked.LastSent >= ProgressIntervalMs;
                if (send)
                {
                    tracked.LastPhase = phase;
                    tracked.LastSent = now;
                }
            }

            if (send)
            {
                UiTask.Run(SendAsync(tracked.From, ExportShareMessages.MakeProgress(tracked.Id, phase, waiting, fraction)));
            }
        }
    }

    /// <summary>«exportar-listo» o «exportar-error» cuando el trabajo termina; el archivo queda en este equipo.</summary>
    private void OnJobFinished(object? sender, ExportEntry entry)
    {
        Tracked? tracked = null;
        lock (_gate)
        {
            tracked = _tracked.Values.FirstOrDefault(t => ReferenceEquals(t.Entry, entry));
            if (tracked is not null)
            {
                _tracked.Remove(tracked.Id);
            }
        }

        if (tracked is not null)
        {
            UiTask.Run(FinishAsync(tracked, entry));
        }
    }

    private async Task FinishAsync(Tracked tracked, ExportEntry entry)
    {
        string fileName = tracked.FileName.Length > 0 ? tracked.FileName : Path.GetFileName(entry.Request.OutputPath);
        switch (entry.Progress.State)
        {
            case ExportJobState.Completed:
                await SendAsync(tracked.From, ExportShareMessages.MakeDone(tracked.Id, fileName)).ConfigureAwait(false);
                break;
            case ExportJobState.Canceled:
                await SendAsync(tracked.From, ExportShareMessages.MakeError(tracked.Id,
                    tracked.GuestCancelled ? "Cancelaste la exportación." : "Quien hospeda la sala canceló la exportación.",
                    tracked.GuestCancelled ? "cancelada-invitado" : "cancelada-anfitrion")).ConfigureAwait(false);
                break;
            default:
                await SendAsync(tracked.From, ExportShareMessages.MakeError(tracked.Id, FailureText(entry), "error"))
                    .ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Un mensaje en español para el invitado, sin rutas ni detalles del equipo de quien hospeda.</summary>
    private static string FailureText(ExportEntry entry) => entry.Failure?.Code switch
    {
        ExportErrorCode.MediaMissing => "No se pudo exportar: falta un archivo en el equipo de quien hospeda la sala.",
        ExportErrorCode.ContentMissing => "No se pudo exportar: en el equipo de quien hospeda falta contenido que usa el proyecto.",
        ExportErrorCode.DiskFull => "No se pudo exportar: no queda espacio en el disco de quien hospeda la sala.",
        ExportErrorCode.OutputNotWritable => "No se pudo exportar: no se puede escribir en la carpeta de exportaciones de quien hospeda.",
        ExportErrorCode.EncoderMissing => "No se pudo exportar: a quien hospeda le falta el programa que genera el video.",
        ExportErrorCode.InvalidSettings => "No se pudo exportar: los ajustes no valen en el equipo de quien hospeda.",
        _ => "No se pudo exportar en el equipo de quien hospeda la sala. Vuelve a intentarlo o pídele que la haga allí.",
    };

    private bool Untrack(Tracked tracked)
    {
        lock (_gate)
        {
            return _tracked.Remove(tracked.Id);
        }
    }

    /// <summary>Directo de vuelta al que pidió, con el token del servicio; si el miembro ya se fue, no pasa nada.</summary>
    private async Task SendAsync(string to, JsonElement message)
    {
        try
        {
            await _room.SendDirectAsync(message, to, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CollabException or OperationCanceledException)
        {
        }
    }

    /// <summary>Un pedido en marcha: a la espera de aprobación, en la cola o exportando.</summary>
    private sealed class Tracked
    {
        public required string Id { get; init; }
        public required string From { get; init; }
        public string FileName = "";
        public ExportEntry? Entry;
        public bool GuestCancelled;
        public long LastSent;
        public string LastPhase = "";
    }

    /// <summary>Cómo levantar la mano dentro del flujo del pedido con el motivo y el texto para el invitado.</summary>
    private sealed class AskException(string motivo, string text) : Exception(text)
    {
        public string Motivo => motivo;
    }
}

/// <summary>
/// El lado del invitado de «Exportar en el anfitrión»: manda el pedido, enseña el progreso en un
/// <see cref="OperationProgress"/> (porcentaje, fase, último avance, tiempo restante y Cancelar) y devuelve el nombre del
/// archivo que quedó en el equipo de quien hospeda. El video exportado no se transfiere: está allí.
/// </summary>
public static class ExportShareGuest
{
    /// <summary>Tiempo sin señal del anfitrión tras el que se da por perdido el pedido (la aprobación a mano puede tardar).</summary>
    public static readonly TimeSpan SilenceTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Pide a quien hospeda que exporte la secuencia con esos ajustes (y rango opcional) sobre su copia confirmada.
    /// Devuelve el nombre del archivo que quedó allí. Lanza <see cref="CollabException"/> con el motivo en español si se
    /// rechaza o falla, y <see cref="OperationCanceledException"/> si se cancela aquí.
    /// </summary>
    public static async Task<string> RequestAsync(RoomService room, string sequenceId, string suggestedName,
        ExportSettings settings, ExportRange? range, OperationProgress? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(sequenceId);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(suggestedName);

        string host = room.HostKey;
        if (host.Length == 0 || room.State.IsHost)
        {
            throw new CollabException("Esto solo lo puede pedir quien se unió a la sala de otra persona.");
        }

        string id = Guid.NewGuid().ToString("N");
        TaskCompletionSource<string> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long lastSign = Environment.TickCount64;

        void OnDirect(string from, JsonElement message)
        {
            if (from != host || !ExportShareMessages.MatchesId(message, id))
            {
                return;
            }

            string? type = ShareMessages.Type(message);
            switch (type)
            {
                case ExportShareMessages.Progress:
                    Interlocked.Exchange(ref lastSign, Environment.TickCount64);
                    (double? fraction, string phase, bool waiting) = ExportShareMessages.ReadProgress(message);
                    progress?.Pulse();
                    progress?.SetWaiting(waiting);
                    // El primer avance confirma que quien hospeda aceptó: la fase deja de ser «pidiendo permiso».
                    progress?.SetPhase(2, 2, "Exportando en el equipo de quien hospeda…");
                    if (phase.Length > 0 || fraction is not null)
                    {
                        progress?.Report(fraction, phase.Length > 0 ? phase : null);
                    }

                    break;
                case ExportShareMessages.Done:
                    done.TrySetResult(ExportShareMessages.ReadDone(message));
                    break;
                case ExportShareMessages.Failed:
                    (string text, string motivo) = ExportShareMessages.ReadError(message);
                    if (motivo == "cancelada-invitado")
                    {
                        done.TrySetCanceled(cancellationToken);
                    }
                    else
                    {
                        done.TrySetException(new CollabException(
                            text.Length > 0 ? text : "La exportación no se pudo hacer en el equipo de quien hospeda."));
                    }

                    break;
            }
        }

        void OnState(RoomState state)
        {
            if (state.Phase is RoomPhase.Closed or RoomPhase.Ended)
            {
                done.TrySetException(new CollabException(
                    state.Problem is { Length: > 0 } problem ? problem : "La sala se cerró antes de terminar la exportación."));
            }
        }

        room.DirectReceived += OnDirect;
        room.StateChanged += OnState;
        try
        {
            progress?.SetCancellable(true);
            progress?.SetWaiting(true);
            progress?.Report(null, "Pidiendo permiso a quien hospeda la sala…");
            progress?.SetPhase(1, 2, "Pidiendo permiso a quien hospeda la sala…");
            // El nombre sugerido viaja ya reducido a nombre de archivo: por el cable no pasa ninguna ruta.
            suggestedName = ExportShareMessages.SafeFileName(suggestedName);
            await room.SendDirectAsync(
                ExportShareMessages.MakeAsk(id, sequenceId,
                    suggestedName.Length > 0 ? suggestedName : "exportacion", settings, range), host, cancellationToken)
                .ConfigureAwait(false);
            while (!done.Task.IsCompleted)
            {
                // WhenAny no propaga la cancelación del Delay: se comprueba el token en cada vuelta.
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan left = SilenceTimeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - lastSign);
                if (left <= TimeSpan.Zero)
                {
                    throw new CollabException("Quien hospeda la sala no contestó a tiempo. Vuelve a pedir la exportación.");
                }

                await Task.WhenAny(done.Task, Task.Delay(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), cancellationToken))
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string fileName = await done.Task.ConfigureAwait(false);
            progress?.Complete();
            return fileName;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            progress?.MarkCancelling();
            await SendCancelAsync(room, host, id).ConfigureAwait(false);
            throw;
        }
        finally
        {
            room.DirectReceived -= OnDirect;
            room.StateChanged -= OnState;
        }
    }

    /// <summary>Avisa a quien hospeda de que se canceló, sin que un fallo del aviso estropee la cancelación.</summary>
    private static async Task SendCancelAsync(RoomService room, string host, string id)
    {
        try
        {
            await room.SendDirectAsync(ExportShareMessages.MakeCancel(id), host, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CollabException or OperationCanceledException)
        {
        }
    }
}
