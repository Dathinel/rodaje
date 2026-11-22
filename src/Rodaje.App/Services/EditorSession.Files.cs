using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Titles;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;

namespace Rodaje.App.Services;

/// <summary>Un archivo que no se importó y por qué.</summary>
public sealed record SkippedMedia(string Path, string Reason);

/// <summary>Resultado de una importación: los medios de la biblioteca que corresponden a los archivos y los que no entraron.</summary>
public sealed record ImportResult(IReadOnlyList<string> MediaIds, IReadOnlyList<SkippedMedia> Skipped)
{
    public static readonly ImportResult Empty = new([], []);
}

public sealed partial class EditorSession
{
    /// <summary>Nombre de archivo que se propone al guardar por primera vez.</summary>
    public string SuggestedFileName => (FilePath is null ? ProjectName : Path.GetFileNameWithoutExtension(FilePath)) + ProjectSerializer.FileExtension;

    public async Task<bool> NewProjectAsync()
    {
        VerifyAccess();
        if (RefuseWhileBusy() || !await ConfirmDiscardChangesAsync() || !await ConfirmLeavingRoomAsync())
        {
            return false;
        }

        Project project = CreateEmptyProject();
        ReplaceProject(project, project.Sequences[0].Id, null, "Proyecto nuevo");
        return true;
    }

    private Task<bool> ConfirmLeavingRoomAsync() => BeforeProjectReplaced?.Invoke() ?? Task.FromResult(true);

    /// <summary>Abre un <c>.rodaje</c>; sin ruta, se le pide a la persona.</summary>
    public async Task<bool> OpenProjectAsync(string? path = null)
    {
        VerifyAccess();
        if (RefuseWhileBusy() || !await ConfirmDiscardChangesAsync() || !await ConfirmLeavingRoomAsync())
        {
            return false;
        }

        path ??= await _dialogs.PickProjectToOpenAsync();
        if (path is null)
        {
            return false;
        }

        string full = Path.GetFullPath(path);
        if (RefuseWhileBusy())
        {
            return false;
        }

        using LongActions.LongActionScope? progress = ProgressHost?.TryBegin(
            "proyecto.abrir", "Abriendo el proyecto", Path.GetFileName(full), showAfter: LongActions.LongActionService.QuickOperationDelay);
        progress?.Operation.SetPhase(1, 2, "Leyendo el archivo");
        BeginBusy();
        Project loaded;
        ClampSummary clamped;
        try
        {
            (loaded, clamped) = await Task.Run(() =>
            {
                Project project = ProjectSerializer.Load(full, out ClampSummary summary);
                ProjectPaths.MakeAbsolute(project, Path.GetDirectoryName(full)!);
                return (project, summary);
            });
        }
        catch (Exception ex) when (ex is ProjectFormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Notify(NotificationSeverity.Error, $"No se pudo abrir «{Path.GetFileName(full)}»", ex.Message);
            return false;
        }
        finally
        {
            EndBusy();
        }

        Sequence? first = loaded.Sequences.FirstOrDefault();
        if (first is null)
        {
            Notify(NotificationSeverity.Error, $"No se pudo abrir «{Path.GetFileName(full)}»", "El proyecto no tiene ninguna secuencia.");
            return false;
        }

        progress?.Operation.SetPhase(2, 2, "Preparando la línea de tiempo");

        // Un proyecto de la v1.0.0 con valores más allá del límite de hoy (posición, ancla, giro, tasa, clips tras el final)
        // se acota al leerlo. Antes era en silencio y el proyecto quedaba «Guardado» aunque ya no era igual al archivo:
        // ahora queda con cambios y el aviso dice qué se acotó y cuánto de cada clase.
        ReplaceProject(loaded, first.Id, full, "Proyecto abierto", unsaved: clamped.Total > 0);
        if (clamped.Total > 0)
        {
            Notify(NotificationSeverity.Warning, "Se ajustaron valores fuera de rango", ClampedNotice(loaded, clamped));
        }

        return true;
    }

    /// <summary>
    /// El aviso de una línea al abrir un proyecto con valores acotados: qué clase se acotó y cuántos de cada una (tasas de
    /// secuencia, clips recortados, clips quitados y valores de efecto). Un único clip quitado se nombra, porque desaparece
    /// de la línea de tiempo; <paramref name="project"/> es el proyecto ya abierto, donde siguen su medio o su compuesto.
    /// </summary>
    internal static string ClampedNotice(Project project, ClampSummary clamped)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clamped);
        var parts = new List<string>(4);
        if (clamped.FrameRates > 0)
        {
            parts.Add(clamped.FrameRates == 1
                ? $"la tasa de fotogramas de 1 secuencia se llevó al rango de {Sequence.MinFrameRate} a {Sequence.MaxFrameRate} fps"
                : $"las tasas de fotogramas de {clamped.FrameRates} secuencias se llevaron al rango de {Sequence.MinFrameRate} a {Sequence.MaxFrameRate} fps");
        }

        if (clamped.TrimmedClips > 0)
        {
            parts.Add(clamped.TrimmedClips == 1
                ? "1 clip que pasaba del final de la línea de tiempo se recortó"
                : $"{clamped.TrimmedClips} clips que pasaban del final de la línea de tiempo se recortaron");
        }

        if (clamped.RemovedClips.Count > 0)
        {
            string name = clamped.RemovedClips.Count == 1 ? ClipLabels.Name(project, clamped.RemovedClips[0]) : "";
            parts.Add(clamped.RemovedClips.Count > 1
                ? $"{clamped.RemovedClips.Count} clips que pasaban del final de la línea de tiempo se quitaron"
                : name.Length > 0
                    ? $"el clip «{name}», que pasaba del final de la línea de tiempo, se quitó"
                    : "1 clip que pasaba del final de la línea de tiempo se quitó");
        }

        if (clamped.Values > 0)
        {
            parts.Add(clamped.Values == 1
                ? "1 valor de efecto (posición, ancla o rotación) se acotó al límite"
                : $"{clamped.Values} valores de efecto (posición, ancla o rotación) se acotaron al límite");
        }

        string list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " y " + parts[^1];
        return "Al abrir, " + list + ". Guarda el proyecto para conservar el cambio.";
    }

    /// <summary>Guarda en el archivo del proyecto; si nunca se guardó, pregunta dónde.</summary>
    public Task<bool> SaveAsync() => FilePath is { } path ? SaveToAsync(path) : SaveAsAsync();

    /// <summary>Guarda en otro archivo (sin ruta, se pregunta) y desde entonces el proyecto es ese archivo.</summary>
    public async Task<bool> SaveAsAsync(string? path = null)
    {
        VerifyAccess();
        path ??= await _dialogs.PickProjectSavePathAsync(SuggestedFileName);
        if (path is null)
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(ProjectPaths.WithProjectExtension(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            Notify(NotificationSeverity.Error, "No se pudo guardar", ex.Message);
            return false;
        }

        return await SaveToAsync(full);
    }

    /// <summary>
    /// Si hay cambios sin guardar, pregunta si guardarlos. <c>true</c> si se puede seguir (guardados o descartados);
    /// <c>false</c> si la persona canceló o el guardado falló.
    /// </summary>
    public async Task<bool> ConfirmDiscardChangesAsync()
    {
        VerifyAccess();
        CancelGesture();
        if (!IsDirty)
        {
            return true;
        }

        return await _dialogs.AskSaveChangesAsync(ProjectName) switch
        {
            UnsavedChangesChoice.Save => await SaveAsync(),
            UnsavedChangesChoice.Discard => true,
            _ => false,
        };
    }

    /// <summary>Entraron medios nuevos a la biblioteca (proxies automáticos, pistas).</summary>
    public event EventHandler<IReadOnlyList<Rodaje.Media.Probe.MediaInfo>>? MediaImported;

    /// <summary>
    /// Lee los archivos fuera del hilo de la interfaz y agrega a la biblioteca los que se pueden usar, en un solo paso
    /// del historial. Un archivo que ya estaba en la biblioteca no se repite: se devuelve su medio. Lo que no entra se
    /// avisa con el motivo.
    /// </summary>
    /// <remarks>
    /// Si la secuencia está vacía, toma la tasa y el tamaño del primer video. Cancelar impide iniciar análisis pendientes
    /// y descarta el lote; espera a que terminen los análisis síncronos que ya estaban en curso.
    /// </remarks>
    public Task<ImportResult> ImportMediaAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        ImportMediaAsync(paths, canApply: null, cancellationToken);

    /// <summary>Permite descartar una importación si su destino cambió durante la lectura, antes de tocar el historial.</summary>
    private async Task<ImportResult> ImportMediaAsync(IEnumerable<string> paths, Func<bool>? canApply, CancellationToken cancellationToken)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        string[] files = [.. paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (files.Length == 0)
        {
            return ImportResult.Empty;
        }

        Project target = Project;
        using LongActions.LongActionScope? progress = ProgressHost?.TryBegin(
            "medios.importar", files.Length == 1 ? "Importando el medio" : "Importando medios", "", canCancel: true,
            showAfter: LongActions.LongActionService.QuickOperationDelay);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, progress?.Token ?? default);
        CancellationToken token = linked.Token;
        BeginBusy();
        MediaProbeResult[] probes;
        try
        {
            // Limita las lecturas simultáneas del disco sin bloquear hilos del pool mientras esperan su turno.
            int concurrency = Math.Clamp(Environment.ProcessorCount, 2, 8);
            using var slots = new SemaphoreSlim(concurrency, concurrency);
            int read = 0;
            probes = await Task.WhenAll(files.Select(async file =>
            {
                await slots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    MediaProbeResult probe = await Task.Run(() => _prober.Probe(file), token).ConfigureAwait(false);
                    int done = Interlocked.Increment(ref read);
                    progress?.Report(done / (double)files.Length, $"Leyendo los medios: {done} de {files.Length}");
                    return probe;
                }
                finally
                {
                    slots.Release();
                }
            }));
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (progress is { Token.IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
        {
            Notify(NotificationSeverity.Informational, "Importación cancelada", "No se añadió ningún medio.");
            return ImportResult.Empty;
        }
        finally
        {
            EndBusy();
        }

        // Un gesto en curso (arrastrar un clip, un deslizador) no admite otro paso del historial: se espera a que acabe.
        try
        {
            while (IsInGesture && ReferenceEquals(target, Project) && canApply?.Invoke() != false)
            {
                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException) when (progress is { Token.IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
        {
            Notify(NotificationSeverity.Informational, "Importación cancelada", "No se añadió ningún medio.");
            return ImportResult.Empty;
        }

        if (!ReferenceEquals(target, Project) || canApply?.Invoke() == false)
        {
            // Mientras se leía cambió el destino: no se incorpora el lote.
            return ImportResult.Empty;
        }

        var ids = new List<string>();
        var added = new List<Rodaje.Media.Probe.MediaInfo>();
        var skipped = new List<SkippedMedia>();
        bool changed = false;
        bool adopted = false;
        History.BeginGroup(files.Length == 1 ? "Importar medio" : "Importar medios");
        try
        {
            foreach (MediaProbeResult probe in probes)
            {
                if (!probe.IsUsable)
                {
                    skipped.Add(new SkippedMedia(probe.Path, probe.Problem ?? "no se pudo leer"));
                    continue;
                }

                if (Project.Media.Find(m => string.Equals(m.Path, probe.Path, StringComparison.OrdinalIgnoreCase)) is { } existing)
                {
                    ids.Add(existing.Id);
                    continue;
                }

                try
                {
                    if (AdoptFormatIfEmpty(probe.Info!))
                    {
                        adopted = changed = true;
                    }

                    var media = new MediaRef { Id = _ids.NewId("media"), Path = probe.Path };
                    probe.Info!.ApplyTo(media);
                    History.Execute(new AddMediaCommand(media));
                    ids.Add(media.Id);
                    added.Add(probe.Info!);
                    changed = true;
                }
                catch (EditException ex)
                {
                    skipped.Add(new SkippedMedia(probe.Path, ex.Message));
                }
            }
        }
        finally
        {
            History.EndGroup();
            History.BreakMerge();
        }

        if (changed)
        {
            AfterEdit(EditKind.Execute, History.UndoCommands.Count > 0 ? History.UndoCommands[^1] : null);
        }

        if (added.Count > 0)
        {
            MediaImported?.Invoke(this, added);
        }

        if (adopted)
        {
            OfferStandardFormat();
        }

        if (Sequence.ColorSpace == SequenceColorSpace.Sdr709
            && added.Where(i => i.Video?.HdrTransfer is not null).Select(i => $"{Path.GetFileName(i.Path)} ({i.Video!.HdrTransfer})").ToList() is { Count: > 0 } hdr)
        {
            Notify(NotificationSeverity.Informational, hdr.Count == 1 ? "Video HDR" : "Videos HDR",
                $"{string.Join(", ", hdr)}: HDR: se convierte a SDR; ajusta el brillo en Propiedades");
        }

        if (skipped.Count > 0)
        {
            Notify(NotificationSeverity.Warning,
                skipped.Count == 1 ? "Un archivo no se importó" : $"{skipped.Count} archivos no se importaron",
                string.Join("\n", skipped.Select(s => $"{Path.GetFileName(s.Path)}: {s.Reason}")));
        }

        return new ImportResult(ids, skipped);
    }

    /// <summary>Pide archivos a la persona y los importa.</summary>
    public async Task<ImportResult> ImportMediaAsync()
    {
        VerifyAccess();
        IReadOnlyList<string> files = await _dialogs.PickMediaFilesAsync();
        return files.Count == 0 ? ImportResult.Empty : await ImportMediaAsync(files);
    }

    /// <summary>
    /// De qué pistas de video sale el audio de un medio al añadirlo a la línea de tiempo (Ajustes). Por defecto, de todas.
    /// </summary>
    public LinkedAudioTracks LinkedAudioTracks
    {
        get => _linkedAudioTracks;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _linkedAudioTracks = value;
        }
    }

    private LinkedAudioTracks _linkedAudioTracks = LinkedAudioTracks.All;

    /// <summary>
    /// Pone el medio entero al final de la pista principal, con su audio enlazado en la primera de audio si
    /// <see cref="LinkedAudioTracks"/> lo permite para esa pista (si no, entra sin clip de audio); un medio sin video va
    /// al final de la última pista de audio (música).
    /// </summary>
    public bool AppendToTimeline(string mediaId) => AppendToTimeline(mediaId, null, null);

    /// <summary>Como <see cref="AppendToTimeline(string)"/>, con solo el tramo <c>[sourceIn, sourceOut)</c> del medio (un subclip).</summary>
    public bool AppendToTimeline(string mediaId, Time? sourceIn, Time? sourceOut)
    {
        VerifyAccess();
        if (Project.FindMedia(mediaId) is not { } media)
        {
            return false;
        }

        Time from = sourceIn is { } a && a >= Time.Zero && (media.IsStillImage || a < media.Duration) ? a : Time.Zero;
        Time to = sourceOut is { } b && b > from && (media.IsStillImage || b <= media.Duration) ? b : media.InitialClipDuration;

        Sequence sequence = Sequence;
        string? videoTrack = media.HasVideo ? sequence.VideoTracks.FirstOrDefault()?.Id : null;
        bool linkAudio = videoTrack is null || LinkedAudioTracks.Allows(sequence, videoTrack);
        string? audioTrack = !media.HasAudio || !linkAudio ? null
            : media.HasVideo ? sequence.AudioTracks.FirstOrDefault()?.Id
            : sequence.AudioTracks.LastOrDefault()?.Id;
        if (videoTrack is null && audioTrack is null)
        {
            Notify(NotificationSeverity.Warning, "No hay dónde ponerlo", $"La secuencia no tiene una pista para «{media.Name}».");
            return false;
        }

        if (videoTrack is not null && audioTrack is not null)
        {
            // Va pegado al final de la principal aunque el audio llegue más lejos; su sonido, en la primera pista de
            // audio libre en ese tramo.
            Time at = sequence.GetTrack(videoTrack).End;
            Time end = at + (to - from);
            if (sequence.AudioTracks.Find(t => !t.Locked && !t.Clips.Exists(c => c.Start < end && c.End > at)) is { } free)
            {
                string id = media.Id;
                string linked = free.Id;
                return Execute(new SequenceEditCommand("Añadir al final", SequenceId,
                    ctx => TimelineEdits.InsertMedia(ctx, id, from, to, videoTrack, at, PlacementMode.Strict, linked), _ids));
            }
        }

        return Execute(Edits.AppendMedia(SequenceId, media.Id, from, to, videoTrack ?? audioTrack!,
            videoTrack is null ? null : audioTrack, _ids));
    }

    /// <summary>
    /// Archivos de la línea de comandos o arrastrados a la ventana: un <c>.rodaje</c> se abre; los medios se importan y
    /// se ponen uno tras otro en la pista principal, en un solo paso del historial.
    /// </summary>
    public async Task OpenFilesAsync(IReadOnlyList<string> paths)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.FirstOrDefault(ProjectPaths.IsProjectFile) is { } project)
        {
            if (paths.Count > 1)
            {
                Notify(NotificationSeverity.Informational, "Se abrió solo el proyecto",
                    "Un proyecto .rodaje se abre solo; los demás archivos no se usaron.");
            }

            await OpenProjectAsync(project);
            return;
        }

        ImportResult imported = await ImportMediaAsync(paths);
        if (imported.MediaIds.Count == 0)
        {
            return;
        }

        BeginGesture(imported.MediaIds.Count == 1 ? "Añadir a la línea de tiempo" : "Añadir medios a la línea de tiempo");
        try
        {
            foreach (string id in imported.MediaIds)
            {
                AppendToTimeline(id);
            }
        }
        finally
        {
            EndGesture();
        }
    }

    private async Task<bool> SaveToAsync(string path, bool overwrite = false)
    {
        VerifyAccess();
        // Los guardados van en fila: dos Ctrl+S seguidos no escriben a la vez el mismo temporal, y uno viejo que termina
        // tarde no pisa al nuevo.
        Task previous = _saving;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _saving = finished.Task;
        BeginBusy();
        try
        {
            await previous;
            return await WriteProjectAsync(path, overwrite);
        }
        finally
        {
            EndBusy();
            finished.SetResult();
        }
    }

    private async Task<bool> WriteProjectAsync(string path, bool overwrite)
    {
        if (IsInGesture)
        {
            Notify(NotificationSeverity.Informational, "Todavía no", "Suelta lo que estás arrastrando antes de guardar.");
            return false;
        }

        long version = _editVersion;
        Project target = Project;
        bool rename = !string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase);
        string name = rename ? Path.GetFileNameWithoutExtension(path) : Project.Name;
        string folder = Path.GetDirectoryName(path)!;
        string? previousFolder = FilePath is null ? null : Path.GetDirectoryName(FilePath);
        Project copy = ProjectPaths.ForSaving(Project, folder, previousFolder);
        copy.Name = name;
        // El mismo proyecto abierto en otra ventana (u otro equipo, en una carpeta compartida): si el archivo cambió desde
        // que se abrió o guardó aquí, no se pisa sin preguntar. La fecha se mira fuera del hilo de la interfaz (UNC).
        DateTimeOffset? expected = await _diskWrite;
        bool changedOutside = false;
        DateTimeOffset? written = null;
        try
        {
            await Task.Run(() =>
            {
                if (!rename && !overwrite && expected is { } known && LastWriteOf(path) is { } onDisk && onDisk != known)
                {
                    changedOutside = true;
                    return;
                }

                ProjectSerializer.Save(copy, path);
                written = LastWriteOf(path);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectFormatException or ArgumentException or NotSupportedException)
        {
            Notify(NotificationSeverity.Error, "No se pudo guardar", ex.Message);
            return false;
        }

        if (changedOutside)
        {
            Notify(NotificationSeverity.Warning, "El proyecto cambió fuera de esta ventana",
                $"Otra ventana o programa guardó «{Path.GetFileName(path)}» después de abrirlo aquí. Guardar encima borra esos cambios; «Guardar como…» los conserva.",
                new NotificationAction("Guardar encima", () => UiTask.Run(SaveToAsync(path, overwrite: true))),
                new NotificationAction("Guardar como…", () => UiTask.Run(SaveAsAsync())));
            return false;
        }

        if (!ReferenceEquals(target, Project))
        {
            return true;
        }

        if (rename)
        {
            Project.Name = name;
            OnPropertyChanged(nameof(ProjectName));
        }

        // Las referencias «file:» en memoria son absolutas (LutReference.ForFile y al abrir): no hay que tocarlas al
        // cambiar de carpeta, ni en el proyecto ni en la pila de deshacer.
        FilePath = path;
        _diskWrite = Task.FromResult(written);
        LastSavedAt = DateTimeOffset.UtcNow;
        if (version == _editVersion && !IsInGesture)
        {
            // Si se editó mientras se escribía, lo guardado ya es viejo y el proyecto sigue con cambios.
            History.MarkClean();
        }

        return true;
    }

    private bool RefuseWhileBusy()
    {
        if (!IsBusy)
        {
            return false;
        }

        Notify(NotificationSeverity.Informational, "Todavía no", "Espera a que termine lo que está en curso: abrir, guardar o importar.");
        return true;
    }

    /// <summary>
    /// La secuencia tomó del primer clip un tamaño raro (la pantalla de un celular, 1080×2400): se ofrece el estándar más
    /// cercano rellenando el cuadro, en un paso que se deshace. El aviso caduca si el tamaño cambia por otro lado.
    /// </summary>
    private void OfferStandardFormat()
    {
        (int width, int height) = (Sequence.Width, Sequence.Height);
        if (ProjectFormats.IsStandard(width, height))
        {
            return;
        }

        (int w, int h) = ProjectFormats.NearestStandard(width, height);
        string sequenceId = SequenceId;
        Notify(NotificationSeverity.Informational, "Formato del proyecto", $"La secuencia tomó {width}×{height} del clip.",
            new NotificationAction($"Usar {w}×{h} ({ProjectFormats.StandardName(w, h)})", () => ChangeFormat(w, h, Sequence.FrameRate, ClipFit.Fill)),
            new NotificationAction("Dejar así", static () => { }),
            () => SequenceId != sequenceId || Sequence.Width != width || Sequence.Height != height);
    }

    private bool AdoptFormatIfEmpty(MediaInfo info)
    {
        Sequence sequence = Sequence;
        if (info.IsStillImage || info.Video is not { } video || KeepsTemplateFormat || Project.Media.Exists(m => m.HasVideo && !m.IsStillImage) || sequence.AllTracks.Any(t => t.Clips.Count > 0))
        {
            return false;
        }

        if (sequence.FrameRate == video.FrameRate && sequence.Width == video.DisplayWidth && sequence.Height == video.DisplayHeight)
        {
            return false;
        }

        History.Execute(new SequenceFormatCommand(SequenceId, video.FrameRate, video.DisplayWidth, video.DisplayHeight));
        return true;
    }
}
