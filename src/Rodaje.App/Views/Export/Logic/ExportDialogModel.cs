using Rodaje.App.Services.Settings;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Views.Export;

/// <summary>
/// El estado del diálogo de exportar, sin WinUI: preajuste, ajustes (siempre ajustados a lo que se puede hacer),
/// rango, ruta, rendimiento y lo que se calcula de ellos. La vista solo dibuja esto y llama a sus métodos.
/// </summary>
public sealed class ExportDialogModel
{
    private readonly ExportPreferences _preferences;
    private readonly Sequence _sequence;

    /// <param name="marks">Las marcas I y O de la secuencia: con ellas, el rango propuesto es «Entre la entrada y la salida».</param>
    public ExportDialogModel(ExportPreferences preferences, Sequence sequence, IReadOnlyCollection<string> selectedClipIds,
        IReadOnlyList<EncoderSpec> encoders, string projectName, string? projectFolder, SequenceMarks marks = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(encoders);
        _preferences = preferences;
        _sequence = sequence;
        Encoders = encoders;
        Ranges = ExportRanges.Choices(sequence, selectedClipIds ?? [], marks);
        RangeIndex = ExportRanges.DefaultIndex(Ranges);
        Performance = preferences.Performance;

        ExportPreset? remembered = ExportPresetLibrary.Find(preferences, preferences.PresetId);
        ExportSettings start = ExportGeometry.FollowSequence(preferences.Settings ?? remembered?.Settings ?? ExportPresets.YouTube1080p.Settings,
            sequence.Width, sequence.Height);
        PresetId = remembered is not null && PresetMatches(remembered, start) ? remembered.Id
            : preferences.Settings is null ? ExportPresets.YouTube1080p.Id : ExportPresetLibrary.CustomId;
        ApplySettings(start, keepPreset: true);

        string folder = preferences.Folder is { } f && Directory.Exists(f) ? f
            : projectFolder is { } p && Directory.Exists(p) ? p
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        OutputPath = Path.Combine(folder, ExportRanges.SuggestFileName(projectName, Settings));
    }

    /// <summary>Cambió algo que la vista muestra.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<EncoderSpec> Encoders { get; }

    public IReadOnlyList<ExportPreset> Presets => ExportPresetLibrary.All(_preferences);

    /// <summary>Id del preajuste marcado, o <see cref="ExportPresetLibrary.CustomId"/>.</summary>
    public string PresetId { get; private set; }

    public ExportSettings Settings { get; private set; } = ExportPresets.YouTube1080p.Settings;

    /// <summary>Lo que el último cambio tuvo que ajustar para que se pudiera exportar, en español.</summary>
    public IReadOnlyList<string> Adjustments { get; private set; } = [];

    public IReadOnlyList<RangeChoice> Ranges { get; }

    public int RangeIndex { get; private set; }

    public string OutputPath { get; private set; }

    public ExportPerformance Performance { get; private set; }

    public int SequenceWidth => _sequence.Width;

    public int SequenceHeight => _sequence.Height;

    public Rational SequenceFrameRate => _sequence.FrameRate;

    public SequenceColorSpace SequenceColorSpace => _sequence.ColorSpace;

    /// <summary>
    /// Con una secuencia HDR: si la salida va en HDR10 o HLG (misma regla que <c>ExportPlan</c>: HEVC, AV1 o ProRes en Y'CbCr de 10 bits) o
    /// por qué cae a SDR. Nulo en una secuencia SDR o sin video.
    /// </summary>
    public string? HdrNote
    {
        get
        {
            if (_sequence.ColorSpace == SequenceColorSpace.Sdr709 || Settings.Video is not { } video || VideoEncoder is not { } encoder)
            {
                return null;
            }

            bool hdrCodec = encoder.Codec is ExportCodec.Hevc or ExportCodec.Av1 or ExportCodec.ProRes;
            (int width, int height) = OutputSize;
            ExportColor color = video.Color ?? (encoder.PixelFormatFor(video.BitDepth, video.Chroma, video.Alpha) is { } pixels
                ? ExportColor.For(width, height, pixels, hdrCodec ? _sequence.ColorSpace : SequenceColorSpace.Sdr709)
                : ExportColor.Bt709Limited);
            return color.IsHdr
                ? $"Sale en {(color.Transfer == ExportTransfer.Pq ? "HDR10 (PQ)" : "HLG")}, BT.2020 de 10 bits."
                : "Sale en SDR: el HDR necesita HEVC, AV1 o ProRes con 10 bits y sin alfa.";
        }
    }

    /// <summary>Si alguna pista de subtítulos visible tiene texto: solo entonces se ofrece cómo exportarlos.</summary>
    public bool HasSubtitles => _sequence.VideoTracks.Any(t => t.Role == TrackRoles.Subtitles && !t.Hidden
        && t.Clips.Any(c => c.Generator is { Kind: GeneratorKinds.Text }));

    /// <summary>Si el contenedor elegido lleva subtítulos que se activan en el reproductor.</summary>
    public bool AllowsSoftSubtitles => Settings.Video is not null && FfmpegPipeSinkOptions.AllowsSoftSubtitles(Settings.Container);

    public Rational FrameRate => Settings.Video?.FrameRate ?? _sequence.FrameRate;

    public Time Duration => Ranges[RangeIndex].Range?.Duration ?? _sequence.Duration;

    public EncoderSpec? VideoEncoder => ExportSettingsEditor.Encoders(Settings, Encoders).Video;

    public EncoderSpec? AudioEncoder => ExportSettingsEditor.Encoders(Settings, Encoders).Audio;

    public (int Width, int Height) OutputSize => Settings.Video is { } v ? ExportGeometry.OutputSize(v, SequenceWidth, SequenceHeight) : (0, 0);

    /// <summary>
    /// El preajuste elegido es de redes con otra forma que la del proyecto (Reels 9:16 con un proyecto 16:9): la salida
    /// sigue al proyecto y la vista ofrece cambiar el formato del proyecto. Los de 16:9 (YouTube, maestros) no avisan.
    /// También avisa si el preajuste fija otra cadencia que la del proyecto (Vertical 9:16 a 30 fps). Nulo si no.
    /// </summary>
    public string? PresetShapeNote
    {
        get
        {
            if (ExportPresetLibrary.Find(_preferences, PresetId) is not { Settings.Video: { } video } preset)
            {
                return null;
            }

            string? shape = video is { Width: { } w, Height: { } h }
                && ExportGeometry.ShapeDiffers(w, h, SequenceWidth, SequenceHeight) && ExportGeometry.ShapeDiffers(w, h, 16, 9)
                    ? $"«{preset.Name}» es para {ExportGeometry.AspectName(w, h)} y el proyecto es {ExportGeometry.AspectName(SequenceWidth, SequenceHeight)}: "
                      + "se exporta con la forma del proyecto"
                    : null;
            string? rate = video.FrameRate is { } fps && fps != SequenceFrameRate
                ? $"a {ExportText.Number(fps.ToDouble(), 3)} fps (el proyecto va a {ExportText.Number(SequenceFrameRate.ToDouble(), 3)})"
                : null;
            return (shape, rate) switch
            {
                (null, null) => null,
                (null, _) => $"«{preset.Name}» se exporta {rate}.",
                (_, null) => $"{shape}.",
                _ => $"{shape}, {rate}.",
            };
        }
    }

    /// <summary>La salida tiene otra forma que la secuencia (solo si se eligió en Avanzado): barras, recorte o estirar.</summary>
    public bool ShapeDiffers => Settings.Video is not null && OutputSize is var (w, h) && ExportGeometry.ShapeDiffers(w, h, SequenceWidth, SequenceHeight);

    public ExportEstimate Estimate => ExportEstimator.Estimate(Settings with { Performance = Performance }, VideoEncoder, OutputSize.Width,
        OutputSize.Height, FrameRate.ToDouble(), TimeSpan.FromSeconds(Duration.ToSeconds()));

    /// <summary>
    /// Las salidas de la cola que aún no terminaron: su archivo todavía no está en el disco, pero cuentan como si ya
    /// existiera (se pide confirmar y las versiones no las repiten).
    /// </summary>
    public IReadOnlyCollection<string> PendingOutputs { get; init; } = [];

    public OutputCheck OutputCheck => ExportRanges.Check(OutputPath, Settings.Container) is var check && check == OutputCheck.Ok
        && PendingOutputs.Contains(OutputPath, StringComparer.OrdinalIgnoreCase)
            ? OutputCheck.Exists
            : check;

    /// <summary>Lo que impide exportar (vacío si se puede); la ruta que ya existe no impide, pide confirmar.</summary>
    public IReadOnlyList<string> Problems => [.. ExportSettingsEditor.Validate(Settings, Encoders).Select(p => p.Message), .. VersionProblems];

    /// <summary>Lo que impide exportar sea cual sea el preajuste (duración y destino): es lo que frena «Varias versiones».</summary>
    public IReadOnlyList<string> VersionProblems
    {
        get
        {
            var problems = new List<string>();
            if (Duration.Ticks <= 0)
            {
                problems.Add("La secuencia está vacía: no hay nada que exportar.");
            }

            if (OutputCheck is not (OutputCheck.Ok or OutputCheck.Exists))
            {
                problems.Add(ExportRanges.Describe(OutputCheck)!);
            }

            return problems;
        }
    }

    public bool CanExport => Problems.Count == 0;

    /// <summary>
    /// Aviso para «Varias versiones»: los preajustes cuyo contenedor no lleva subtítulos aparte cuando se eligió ese modo
    /// (saldrán quemados). Nulo si no hay nada que avisar.
    /// </summary>
    public string? VersionsNote(IEnumerable<string> presetIds)
    {
        ArgumentNullException.ThrowIfNull(presetIds);
        if (Settings.Subtitles == SubtitleExport.Burned || !HasSubtitles)
        {
            return null;
        }

        string[] names = [.. presetIds.Select(id => ExportPresetLibrary.Find(_preferences, id))
            .Where(p => p is { Settings.Video: not null } && !FfmpegPipeSinkOptions.AllowsSoftSubtitles(p.Settings.Container))
            .Select(p => $"«{p!.Name}»")];
        return names.Length == 0 ? null : $"{string.Join(", ", names)}: el formato no admite subtítulos aparte; irán quemados en la imagen.";
    }

    public void SelectPreset(string id)
    {
        if (id == ExportPresetLibrary.CustomId)
        {
            PresetId = id;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        ExportPreset preset = ExportPresetLibrary.Find(_preferences, id) ?? throw new ArgumentException($"No hay un preajuste «{id}».", nameof(id));
        PresetId = preset.Id;
        // Cómo van los subtítulos es del proyecto, no del preajuste: se conserva al cambiar de tarjeta.
        ApplySettings(ExportGeometry.FollowSequence(preset.Settings, SequenceWidth, SequenceHeight) with { Subtitles = Settings.Subtitles }, keepPreset: true);
    }

    /// <summary>Un cambio desde Avanzado: se ajusta a lo posible y, si ya no es el del preajuste, pasa a «Personalizado».</summary>
    public void Update(Func<ExportSettings, ExportSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ApplySettings(change(Settings), keepPreset: false);
    }

    public void SetVideoEnabled(bool enabled) => Update(s => enabled
        ? s with { Video = s.Video ?? ExportSettingsEditor.DefaultVideo(ExportChoices.VideoCodecs(s.Container, Encoders) is [var v, ..] ? v : ExportCodec.H264, Encoders) }
        : s with { Video = null });

    public void SetAudioEnabled(bool enabled) => Update(s => enabled
        ? s with { Audio = s.Audio ?? ExportSettingsEditor.DefaultAudio(ExportChoices.AudioCodecs(s.Container, Encoders) is [var a, ..] ? a : ExportCodec.Aac, Encoders) }
        : s with { Audio = null });

    public void SetRange(int index)
    {
        RangeIndex = Math.Clamp(index, 0, Ranges.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetOutputPath(string path)
    {
        OutputPath = string.IsNullOrWhiteSpace(path) ? path : ExportRanges.WithExtension(path.Trim(), Settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetPerformance(ExportPerformance performance)
    {
        Performance = performance;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Guarda lo actual como preajuste propio y lo marca.</summary>
    public ExportPreset SavePreset(string name)
    {
        ExportPreset preset = ExportPresetLibrary.SaveUserPreset(_preferences, name, Settings);
        PresetId = preset.Id;
        Changed?.Invoke(this, EventArgs.Empty);
        return preset;
    }

    public void RemovePreset(string id)
    {
        if (ExportPresetLibrary.Remove(_preferences, id) && PresetId == id)
        {
            PresetId = ExportPresetLibrary.CustomId;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Guarda las opciones actuales para reabrir el diálogo después de cambiar el formato del proyecto.</summary>
    public void RememberDraft() => ExportPresetLibrary.Remember(_preferences,
        PresetId == ExportPresetLibrary.CustomId ? null : PresetId,
        Settings with { Performance = Performance }, OutputPath);

    /// <summary>La exportación lista para la cola; recuerda los ajustes. <see cref="CanExport"/> debe ser cierto.</summary>
    public ExportRequest CreateRequest(Project projectCopy, string sequenceId, string? projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(projectCopy);
        if (!CanExport)
        {
            throw new InvalidOperationException(string.Join(" ", Problems));
        }

        ExportSettings settings = Settings with { Performance = Performance };
        if (settings.Video is { } video && settings.Container is not ExportContainer.ImageSequence && VideoEncoder is { } encoder)
        {
            settings = settings with { Video = video with { EncoderId = encoder.Id } };
        }

        if (settings.Audio is { } audio && AudioEncoder is { } audioEncoder)
        {
            settings = settings with { Audio = audio with { EncoderId = audioEncoder.Id } };
        }

        ExportPresetLibrary.Remember(_preferences, PresetId == ExportPresetLibrary.CustomId ? null : PresetId, Settings with { Performance = Performance },
            OutputPath);
        return new ExportRequest(projectCopy, sequenceId, OutputPath, settings, Ranges[RangeIndex].Range) { ProjectDirectory = projectDirectory };
    }

    /// <summary>
    /// «Exportar varias versiones»: una petición por preajuste, con el rango, el rendimiento, los subtítulos y la carpeta de
    /// ahora y el nombre del archivo más el del preajuste («Viaje - YouTube 1080p.mp4»; con un número si ya existe). Un
    /// preajuste de otra forma que el proyecto (Reels 9:16 sobre un proyecto 16:9) conserva su forma y llena el cuadro
    /// recortando al centro; los demás siguen al proyecto.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="VersionProblems"/> no está vacío.</exception>
    /// <exception cref="ArgumentException">Un preajuste no existe o no se puede exportar en este equipo.</exception>
    public IReadOnlyList<ExportRequest> CreateVersionRequests(IEnumerable<string> presetIds, Project projectCopy, string sequenceId,
        string? projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(presetIds);
        ArgumentNullException.ThrowIfNull(projectCopy);
        if (VersionProblems is { Count: > 0 } problems)
        {
            throw new InvalidOperationException(string.Join(" ", problems));
        }

        string folder = Path.GetDirectoryName(OutputPath) is { Length: > 0 } dir ? dir : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string baseName = Path.GetFileNameWithoutExtension(OutputPath);
        var requests = new List<ExportRequest>();
        var used = new HashSet<string>(PendingOutputs, StringComparer.OrdinalIgnoreCase);
        foreach (string id in presetIds)
        {
            ExportPreset preset = ExportPresetLibrary.Find(_preferences, id) ?? throw new ArgumentException($"No hay un preajuste «{id}».", nameof(presetIds));
            ExportSettings settings = VersionSettings(preset.Settings) with { Subtitles = Settings.Subtitles };
            settings = ExportSettingsEditor.Coerce(settings, Encoders).Settings with { Performance = Performance };
            if (settings.Video is null || !FfmpegPipeSinkOptions.AllowsSoftSubtitles(settings.Container))
            {
                settings = settings with { Subtitles = SubtitleExport.Burned };
            }

            if (ExportSettingsEditor.Validate(settings, Encoders) is { Count: > 0 } invalid)
            {
                throw new ArgumentException($"«{preset.Name}»: {string.Join(" ", invalid.Select(p => p.Message))}", nameof(presetIds));
            }

            (EncoderSpec? video, EncoderSpec? audio) = ExportSettingsEditor.Encoders(settings, Encoders);
            if (settings.Video is { } v && settings.Container is not ExportContainer.ImageSequence && video is not null)
            {
                settings = settings with { Video = v with { EncoderId = video.Id } };
            }

            if (settings.Audio is { } a && audio is not null)
            {
                settings = settings with { Audio = a with { EncoderId = audio.Id } };
            }

            string name = string.Concat($"{baseName} - {preset.Name}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            // La extensión se añade, no se cambia: en «Boda 24.10.2026 - Web 1080p» el último punto es del nombre.
            string extension = "." + ExportRanges.Extension(settings);
            string path = Path.Combine(folder, name + extension);
            for (int n = 2; !used.Add(path) || File.Exists(path) || Directory.Exists(path); n++)
            {
                path = Path.Combine(folder, $"{name} ({n}){extension}");
            }

            requests.Add(new ExportRequest(projectCopy, sequenceId, path, settings, Ranges[RangeIndex].Range) { ProjectDirectory = projectDirectory });
        }

        return requests;
    }

    private ExportSettings VersionSettings(ExportSettings preset) =>
        preset.Video is { Width: { } w, Height: { } h } video && ExportGeometry.ShapeDiffers(w, h, SequenceWidth, SequenceHeight)
            && ExportGeometry.ShapeDiffers(w, h, 16, 9)
            ? preset with { Video = video with { Fit = ExportFit.Crop } }
            : ExportGeometry.FollowSequence(preset, SequenceWidth, SequenceHeight);

    /// <summary>Si los ajustes son los del preajuste ya llevado a la forma del proyecto.</summary>
    private bool PresetMatches(ExportPreset preset, ExportSettings settings) =>
        ExportPresetLibrary.Matches(preset with { Settings = ExportGeometry.FollowSequence(preset.Settings, SequenceWidth, SequenceHeight) }, settings);

    private void ApplySettings(ExportSettings settings, bool keepPreset)
    {
        CoercedSettings coerced = ExportSettingsEditor.Coerce(settings, Encoders);
        Settings = coerced.Settings;
        Adjustments = coerced.Changes;
        if (!keepPreset && ExportPresetLibrary.Find(_preferences, PresetId) is { } preset && !PresetMatches(preset, Settings))
        {
            PresetId = ExportPresetLibrary.CustomId;
        }

        if (!string.IsNullOrWhiteSpace(OutputPath))
        {
            OutputPath = ExportRanges.WithExtension(OutputPath, Settings);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
