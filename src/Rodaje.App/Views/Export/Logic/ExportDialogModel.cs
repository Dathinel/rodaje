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

    public ExportDialogModel(ExportPreferences preferences, Sequence sequence, IReadOnlyCollection<string> selectedClipIds,
        IReadOnlyList<EncoderSpec> encoders, string projectName, string? projectFolder)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(encoders);
        _preferences = preferences;
        _sequence = sequence;
        Encoders = encoders;
        Ranges = ExportRanges.Choices(sequence, selectedClipIds ?? []);
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

    public Rational FrameRate => Settings.Video?.FrameRate ?? _sequence.FrameRate;

    public Time Duration => Ranges[RangeIndex].Range?.Duration ?? _sequence.Duration;

    public EncoderSpec? VideoEncoder => ExportSettingsEditor.Encoders(Settings, Encoders).Video;

    public EncoderSpec? AudioEncoder => ExportSettingsEditor.Encoders(Settings, Encoders).Audio;

    public (int Width, int Height) OutputSize => Settings.Video is { } v ? ExportGeometry.OutputSize(v, SequenceWidth, SequenceHeight) : (0, 0);

    /// <summary>
    /// El preajuste elegido está pensado para otra forma que la del proyecto (Reels 9:16 con un proyecto 16:9): la salida
    /// sigue al proyecto y la vista ofrece cambiar el formato del proyecto. Nulo si no.
    /// </summary>
    public string? PresetShapeNote =>
        ExportPresetLibrary.Find(_preferences, PresetId) is { Settings.Video: { Width: { } w, Height: { } h } } preset
        && ExportGeometry.ShapeDiffers(w, h, SequenceWidth, SequenceHeight)
            ? $"«{preset.Name}» es para {ExportGeometry.AspectName(w, h)} y el proyecto es {ExportGeometry.AspectName(SequenceWidth, SequenceHeight)}: "
              + "se exporta con la forma del proyecto."
            : null;

    /// <summary>La salida tiene otra forma que la secuencia (solo si se eligió en Avanzado): barras, recorte o estirar.</summary>
    public bool ShapeDiffers => Settings.Video is not null && OutputSize is var (w, h) && ExportGeometry.ShapeDiffers(w, h, SequenceWidth, SequenceHeight);

    public ExportEstimate Estimate => ExportEstimator.Estimate(Settings with { Performance = Performance }, VideoEncoder, OutputSize.Width,
        OutputSize.Height, FrameRate.ToDouble(), TimeSpan.FromSeconds(Duration.ToSeconds()));

    public OutputCheck OutputCheck => ExportRanges.Check(OutputPath);

    /// <summary>Lo que impide exportar (vacío si se puede); la ruta que ya existe no impide, pide confirmar.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            var problems = ExportSettingsEditor.Validate(Settings, Encoders).Select(p => p.Message).ToList();
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
        ApplySettings(ExportGeometry.FollowSequence(preset.Settings, SequenceWidth, SequenceHeight), keepPreset: true);
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
