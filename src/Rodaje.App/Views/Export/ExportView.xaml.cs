using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Settings;
using Rodaje.App.Shell;
using Rodaje.App.Views.Inspector;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Export.Running;
using Rodaje.Media.Export;

namespace Rodaje.App.Views.Export;

/// <summary>
/// El diálogo de exportar, como página encima del editor. Cada vez que se abre arma un <see cref="ExportDialogModel"/>
/// con la secuencia, la selección y lo recordado; al exportar pasa la petición (con una copia del proyecto) a la cola y
/// se cierra: la edición sigue mientras exporta.
/// </summary>
internal sealed partial class ExportView : UserControl
{
    private static readonly (string Label, LoudnessTarget? Value)[] LoudnessChoices =
    [
        ("No", null),
        ("−14 LUFS (plataformas de video y música)", LoudnessTarget.Platforms),
        ("−16 LUFS (pódcast)", new LoudnessTarget(-16)),
        ("−23 LUFS (televisión, EBU R128)", LoudnessTarget.EbuR128),
    ];

    private static readonly (string Label, ExportFit Value)[] FitChoices =
        [("Barras (la imagen entera)", ExportFit.Bars), ("Recortar (llena el fotograma)", ExportFit.Crop), ("Estirar (deforma)", ExportFit.Stretch)];

    private readonly EditorServices _services;
    private readonly ExportJobList _jobs;
    private readonly nint _hwnd;
    private ExportDialogModel? _model;
    private bool _refreshing;
    private bool _refreshQueued;

    private IReadOnlyList<ExportContainer> _containers = [];
    private IReadOnlyList<ExportCodec> _videoCodecs = [];
    private IReadOnlyList<EncoderSpec> _videoEncoders = [];
    private IReadOnlyList<RateChoice> _rates = [];
    private IReadOnlyList<SizeChoice> _sizes = [];
    private IReadOnlyList<int> _bitDepths = [];
    private IReadOnlyList<ChromaSubsampling> _chromas = [];
    private IReadOnlyList<string?> _profiles = [];
    private IReadOnlyList<string?> _speedPresets = [];
    private IReadOnlyList<ExportCodec> _audioCodecs = [];
    private IReadOnlyList<int> _sampleRates = [];
    private IReadOnlyList<int> _channels = [];

    public ExportView(EditorServices services, ExportJobList jobs, nint hwnd)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(jobs);
        _services = services;
        _jobs = jobs;
        _hwnd = hwnd;
        InitializeComponent();
        PerformanceFastTitle.Text = ExportText.Performance(ExportPerformance.Fastest);
        PerformanceFastDetail.Text = ExportText.PerformanceDetail(ExportPerformance.Fastest);
        PerformanceLowTitle.Text = ExportText.Performance(ExportPerformance.Background);
        PerformanceLowDetail.Text = ExportText.PerformanceDetail(ExportPerformance.Background);
    }

    /// <summary>Pide cerrar la página (Cancelar, o tras lanzar la exportación).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Se lanzó una exportación.</summary>
    public event EventHandler<ExportEntry>? Started;

    /// <summary>El modelo actual, para la autoprueba.</summary>
    internal ExportDialogModel? Model => _model;

    /// <summary>
    /// Autoprueba: pulsa las tarjetas y elige cada rango como lo haría la persona (por el par de automatización, que lanza el
    /// mismo Click) y comprueba que cada elección queda marcada. Devuelve el fallo o <c>null</c>.
    /// </summary>
    /// <summary>Si ya están dibujadas las tarjetas de esos preajustes (la sonda de codificadores terminó).</summary>
    internal bool HasPresetCards(IEnumerable<string> presetIds) =>
        presetIds.All(id => PresetCards.Children.OfType<Button>().Any(b => b.Tag as string == id));

    internal async Task<string?> CheckSwitchingAsync(IReadOnlyList<string> presetIds)
    {
        foreach (string id in presetIds)
        {
            if (PresetCards.Children.OfType<Button>().FirstOrDefault(b => (string)b.Tag == id) is not { } card)
            {
                return $"falta la tarjeta «{id}»";
            }

            new ButtonAutomationPeer(card).Invoke();
            await Task.Delay(200);
            Button? marked = PresetCards.Children.OfType<Button>().FirstOrDefault(b => b.BorderThickness.Left > 1);
            if (_model?.PresetId != id || marked?.Tag as string != id)
            {
                return $"la tarjeta «{id}» no quedó elegida (modelo: {_model?.PresetId}, marcada: {marked?.Tag})";
            }
        }

        for (int i = RangeBox.Items.Count - 1; i >= 0; i--)
        {
            RangeBox.SelectedIndex = i;
            await Task.Delay(150);
            if (_model?.RangeIndex != i || RangeBox.SelectedIndex != i)
            {
                return $"el rango {i} no quedó elegido";
            }
        }

        return null;
    }

    /// <summary>Arma el diálogo con el estado de ahora: secuencia, selección, codificadores y lo recordado.</summary>
    public async Task OpenAsync()
    {
        EditorSession session = _services.Session;
        ExportButton.IsEnabled = false;
        ProjectsButton.IsEnabled = false;
        VersionsButton.IsEnabled = false;
        FooterNote.Text = "Comprobando los codificadores de este equipo…";
        IReadOnlyList<EncoderSpec> encoders = await _jobs.Runner.GetEncodersAsync();
        string? projectFolder = session.FilePath is { } file ? Path.GetDirectoryName(file) : null;
        _model = new ExportDialogModel(_services.Settings.Current.Export, session.Sequence, session.SelectedClipIds, encoders, session.ProjectName,
            projectFolder, new SequenceMarks(session.SequenceIn, session.SequenceOut)) { PendingOutputs = [.. _jobs.Entries.Where(e => !e.IsFinished).Select(e => e.OutputPath)] };
        _model.Changed += (_, _) => QueueRefresh();
        ShowAdvanced(_services.Settings.Current.Export.AdvancedOpen);
        Refresh();
    }

    /// <summary>Despliega o pliega «Avanzado» sin recordarlo (la autoprueba lo abre para dibujarlo con AOT).</summary>
    internal void ShowAdvanced(bool open)
    {
        AdvancedToggle.IsChecked = open;
        AdvancedPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AdvancedGlyph.Glyph = open ? "" : "";
    }

    /// <summary>
    /// Redibuja después del evento que cambió el modelo, nunca dentro: rehacer la lista de un ComboBox o quitar la tarjeta
    /// pulsada mientras WinUI todavía atiende su clic dejaba el control sin responder al siguiente (el error de «no deja
    /// elegir otro»).
    /// </summary>
    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        DispatcherQueue.RunAfterEvent(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    /// <summary>
    /// Un refresco que llegó con la lista abierta no tocó ese desplegable (ver <c>Fill</c>): al cerrarse, otro refresco
    /// lo pone al día, también si se cerró sin elegir nada.
    /// </summary>
    private void OnDropDownClosed(object sender, object e) => QueueRefresh();

    private void Refresh()
    {
        if (_model is not { } model)
        {
            return;
        }

        _refreshing = true;
        try
        {
            RefreshPresets(model);
            RefreshSimple(model);
            RefreshVideo(model);
            RefreshAudio(model);
            RefreshSide(model);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshPresets(ExportDialogModel model)
    {
        var cards = model.Presets
            .Select(p => (Id: p.Id, Name: p.Name, Description: p.Origin == ExportPresetOrigin.User ? "Propio · " + p.Description : p.Description))
            .Append((Id: ExportPresetLibrary.CustomId, Name: "Personalizado", Description: "Lo que elijas en Avanzado."))
            .ToList();
        if (ComboChoices.NeedsRebuild(PresetCards.Children.Select(c => (c as Button)?.Tag as string ?? ""), cards.Select(c => c.Id)))
        {
            PresetCards.Children.Clear();
            foreach ((string id, string name, string description) in cards)
            {
                PresetCards.Children.Add(PresetCard(id, name, description));
            }
        }

        foreach (Button card in PresetCards.Children.OfType<Button>())
        {
            bool selected = (string)card.Tag == model.PresetId;
            card.BorderThickness = new Thickness(selected ? 2 : 1);
            card.BorderBrush = (Brush)Application.Current.Resources[selected ? "AccentBrush" : "ControlBorderBrush"];
            AutomationProperties.SetItemStatus(card, selected ? "Elegido" : "");
        }

        bool userPreset = model.Presets.Any(p => p.Id == model.PresetId && p.Origin == ExportPresetOrigin.User);
        RemovePresetButton.IsEnabled = userPreset;
        ExportPresetButton.IsEnabled = model.PresetId != ExportPresetLibrary.CustomId;
    }

    private Button PresetCard(string id, string name, string description)
    {
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["PanelTitleTextStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = description, Style = (Style)Application.Current.Resources["CaptionTextStyle"], TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2,
        });
        var card = new Button
        {
            Content = text, Tag = id, Width = 176, Height = 70, Padding = new Thickness(10, 6, 10, 6),
            HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
        };
        ToolTipService.SetToolTip(card, description);
        card.Click += OnPresetClick;
        return card;
    }

    private void RefreshSimple(ExportDialogModel model)
    {
        if (PathBox.FocusState == FocusState.Unfocused)
        {
            PathBox.Text = model.OutputPath;
        }

        string? pathNote = ExportRanges.Describe(model.OutputCheck);
        PathNote.Text = pathNote ?? "";
        PathNote.Visibility = pathNote is null ? Visibility.Collapsed : Visibility.Visible;

        Fill(RangeBox, model.Ranges.Select(r => r.Label), model.RangeIndex);
        SummaryText.Text = Summary(model);

        string? presetNote = model.PresetShapeNote;
        PresetShapeText.Text = presetNote ?? "";
        PresetShapePanel.Visibility = presetNote is null ? Visibility.Collapsed : Visibility.Visible;
        ProjectFormatText.Text = $"Formato del proyecto: {ExportGeometry.AspectName(model.SequenceWidth, model.SequenceHeight)} · "
            + $"{model.SequenceWidth}×{model.SequenceHeight} · {ExportText.Number(model.SequenceFrameRate.ToDouble(), 3)} fps"
            + (model.HdrNote is { } hdr ? $" · {ProjectFormats.ColorSpaceName(model.SequenceColorSpace)}. {hdr}" : "");
    }

    private void RefreshVideo(ExportDialogModel model)
    {
        ExportSettings s = model.Settings;
        _containers = ExportChoices.Containers(model.Encoders);
        Fill(ContainerBox, _containers.Select(ExportChoices.ContainerName), Index(_containers, s.Container));
        FastStartBox.IsChecked = s.FastStart;
        FastStartBox.IsEnabled = s.Container is ExportContainer.Mp4 or ExportContainer.Mov or ExportContainer.M4a;
        ChaptersBox.IsChecked = s.ChaptersFromMarkers;
        ChaptersBox.IsEnabled = s.Container is ExportContainer.Mp4 or ExportContainer.Mov or ExportContainer.Mkv;
        Visibility subtitles = model.HasSubtitles ? Visibility.Visible : Visibility.Collapsed;
        SubtitlesLabel.Visibility = SubtitlesBox.Visibility = subtitles;
        Fill(SubtitlesBox, SubtitleModes.Select(m => m.Name), model.AllowsSoftSubtitles ? IndexOf(SubtitleModes, m => m.Mode == s.Subtitles) : 0);
        SubtitlesBox.IsEnabled = model.AllowsSoftSubtitles;

        _videoCodecs = ExportChoices.VideoCodecs(s.Container, model.Encoders);
        VideoSwitch.IsOn = s.Video is not null;
        VideoSwitch.IsEnabled = _videoCodecs.Count > 0 && s.Container.AllowsAudio();
        VideoGrid.Visibility = s.Video is null ? Visibility.Collapsed : Visibility.Visible;
        FitBox.IsEnabled = false;
        if (s.Video is not { } video || model.VideoEncoder is not { } encoder)
        {
            return;
        }

        Fill(VideoCodecBox, _videoCodecs.Select(ExportChoices.CodecName), Index(_videoCodecs, video.Codec));
        _videoEncoders = ExportChoices.EncodersFor(video.Codec, model.Encoders);
        Fill(EncoderBox, _videoEncoders.Select(ExportChoices.EncoderLabel), Index(_videoEncoders, encoder));

        _rates = ExportChoices.RateChoices(encoder);
        RateChoice rate = ExportChoices.RateChoiceOf(video.RateControl.Mode, video.RateControl.TargetSizeBytes is not null);
        Fill(RateBox, _rates.Select(ExportChoices.RateChoiceName), Index(_rates, rate));
        ShowRateValues(video.RateControl, rate, encoder);

        _sizes = ExportGeometry.Sizes(model.SequenceWidth, model.SequenceHeight);
        int sizeIndex = IndexOf(_sizes, z => z.Width == video.Width && z.Height == video.Height);
        if (sizeIndex < 0)
        {
            (int w, int h) = model.OutputSize;
            _sizes = [.. _sizes, new SizeChoice($"{w}×{h} (del preajuste)", video.Width, video.Height)];
            sizeIndex = _sizes.Count - 1;
        }

        Fill(SizeBox, _sizes.Select(z => z.Label), sizeIndex);
        Fill(FitBox, FitChoices.Select(f => f.Label), Array.FindIndex(FitChoices, f => f.Value == video.Fit));
        FitBox.IsEnabled = model.ShapeDiffers;
        Fill(FrameRateBox, ExportGeometry.FrameRates.Select(f => f.Rate is null ? $"Como la secuencia ({ExportText.Number(model.FrameRate.ToDouble(), 3)})" : f.Label),
            IndexOf(ExportGeometry.FrameRates, f => f.Rate == video.FrameRate));

        _bitDepths = ExportChoices.BitDepths(encoder);
        Fill(BitDepthBox, _bitDepths.Select(b => $"{b} bits"), Index(_bitDepths, video.BitDepth));
        _chromas = ExportChoices.Chromas(encoder, video.BitDepth);
        Fill(ChromaBox, _chromas.Select(ExportChoices.ChromaName), Math.Max(0, Index(_chromas, video.Chroma)));
        ChromaBox.IsEnabled = _chromas.Count > 1;
        ExportPixelFormat? pixels = encoder.PixelFormatFor(video.BitDepth, video.Chroma, video.Alpha);
        _profiles = [null, .. ExportChoices.Profiles(encoder, pixels).Select(p => p.Id)];
        Fill(ProfileBox, _profiles.Select(id => id is null ? "Automático" : encoder.Profiles.First(p => p.Id == id).Name), Index(_profiles, video.Profile));
        ProfileBox.IsEnabled = _profiles.Count > 1;
        _speedPresets = [null, .. encoder.SpeedPresets];
        Fill(SpeedPresetBox, _speedPresets.Select(p => p ?? (encoder.DefaultSpeedPreset is { } d ? $"Por defecto ({d})" : "Por defecto")),
            Index(_speedPresets, video.SpeedPreset));
        SpeedPresetBox.IsEnabled = _speedPresets.Count > 1;
        KeyframeBox.IsEnabled = !encoder.IntraOnly;
        KeyframeBox.Value = video.Gop.KeyframeSeconds ?? double.NaN;
        MeasureLightBox.Visibility = model.OffersLightMeasurement ? Visibility.Visible : Visibility.Collapsed;
        MeasureLightBox.IsChecked = video.MeasureLightLevels;
    }

    private void ShowRateValues(RateControl rate, RateChoice choice, EncoderSpec encoder)
    {
        RateMaxBox.Visibility = choice == RateChoice.Vbr ? Visibility.Visible : Visibility.Collapsed;
        RateValueBox.Visibility = choice is RateChoice.Lossless or RateChoice.ProfileDefined ? Visibility.Collapsed : Visibility.Visible;
        switch (choice)
        {
            case RateChoice.Quality when encoder.Quality is { } scale:
                RateValueBox.Minimum = scale.Min;
                RateValueBox.Maximum = scale.Max;
                RateValueBox.Value = rate.Quality ?? scale.Default;
                RateUnit.Text = $"{scale.Name} {ExportText.Number(scale.Min)}–{ExportText.Number(scale.Max)}, {(scale.LowerIsBetter ? "menos" : "más")} es mejor";
                break;
            case RateChoice.Vbr:
                SetMbps(RateValueBox, rate.BitrateKbps);
                SetMbps(RateMaxBox, rate.MaxBitrateKbps ?? rate.BitrateKbps);
                RateUnit.Text = "Mbps (media y máxima)";
                break;
            case RateChoice.Cbr:
                SetMbps(RateValueBox, rate.BitrateKbps);
                RateUnit.Text = "Mbps";
                break;
            case RateChoice.TargetSize:
                RateValueBox.Minimum = 1;
                RateValueBox.Maximum = 1_000_000;
                RateValueBox.Value = (rate.TargetSizeBytes ?? 0) / 1e6;
                RateUnit.Text = "MB (dos pasadas)";
                break;
            default:
                RateUnit.Text = choice == RateChoice.Lossless ? "Sin pérdidas" : "La fija el perfil";
                break;
        }
    }

    private static void SetMbps(NumberBox box, int? kbps)
    {
        box.Minimum = 0.1;
        box.Maximum = 1000;
        box.Value = (kbps ?? 0) / 1000.0;
    }

    private void RefreshAudio(ExportDialogModel model)
    {
        ExportSettings s = model.Settings;
        _audioCodecs = ExportChoices.AudioCodecs(s.Container, model.Encoders);
        AudioSwitch.IsOn = s.Audio is not null;
        AudioSwitch.IsEnabled = _audioCodecs.Count > 0 && !s.Container.IsAudioOnly();
        AudioGrid.Visibility = s.Audio is null ? Visibility.Collapsed : Visibility.Visible;
        if (s.Audio is not { } audio || model.AudioEncoder is not { } encoder)
        {
            return;
        }

        Fill(AudioCodecBox, _audioCodecs.Select(ExportChoices.CodecName), Index(_audioCodecs, audio.Codec));
        bool lossless = audio.RateControl.Mode == RateControlMode.Lossless;
        AudioRateBox.Visibility = lossless ? Visibility.Collapsed : Visibility.Visible;
        AudioRateBox.Value = audio.RateControl.BitrateKbps ?? double.NaN;
        AudioRateUnit.Text = lossless ? "Sin pérdidas" : "kbps";
        _sampleRates = ExportChoices.SampleRates(encoder);
        Fill(SampleRateBox, _sampleRates.Select(r => ExportText.Number(r / 1000.0) + " kHz"), Index(_sampleRates, audio.SampleRate));
        _channels = ExportChoices.Channels(encoder);
        Fill(ChannelsBox, _channels.Select(c => c switch { 1 => "Mono", 2 => "Estéreo", 6 => "5.1", _ => $"{c} canales" }), Index(_channels, audio.Channels));
        Fill(LoudnessBox, LoudnessChoices.Select(l => l.Label), Array.FindIndex(LoudnessChoices, l => l.Value?.Lufs == audio.Loudness?.Lufs));
    }

    private void RefreshSide(ExportDialogModel model)
    {
        PerformanceFast.IsChecked = model.Performance == ExportPerformance.Fastest;
        PerformanceLow.IsChecked = model.Performance == ExportPerformance.Background;
        ExportEstimate estimate = model.Estimate;
        EstimateText.Text = (estimate.Approximate ? "≈ " : "") + ExportText.Bytes(estimate.Bytes);
        EstimateDetail.Text = $"Unos {ExportText.Duration(estimate.Time)} con {(model.Performance == ExportPerformance.Fastest ? "alto" : "bajo")} rendimiento"
            + (estimate.Approximate ? ". En calidad constante el tamaño depende de la imagen." : ".");
        RefreshStreamCopy(model);
        ShowAdjustments(model);
        IReadOnlyList<string> problems = model.Problems;
        ProblemsText.Text = string.Join("\n", problems);
        ProblemsText.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = problems.Count == 0;
        ProjectsButton.IsEnabled = problems.Count == 0;
        VersionsButton.IsEnabled = model.VersionProblems.Count == 0;
        FooterNote.Text = _jobs.Runner.IsSimulation
            ? "Simulación: no se escribe ningún archivo."
            : _jobs.ActiveCount > 0 ? "Hay otra exportación en curso: esta esperará su turno." : "";
    }

    /// <summary>«MP4 · H.264 por GPU (h264_nvenc) · 1920×1080 · 30 fps · VBR 12 Mbps · AAC 192 kbps estéreo».</summary>
    private static string Summary(ExportDialogModel model)
    {
        ExportSettings s = model.Settings;
        var parts = new List<string> { ExportChoices.ContainerName(s.Container) };
        if (s.Video is { } video && model.VideoEncoder is { } encoder)
        {
            string where = encoder.IsHardware ? "por GPU" : encoder.Vendor == EncoderVendor.Microsoft ? "por Windows" : "por CPU";
            parts.Add($"{ExportChoices.CodecName(video.Codec)} {where} ({encoder.Id})");
            (int w, int h) = model.OutputSize;
            parts.Add($"{w}×{h}");
            parts.Add($"{ExportText.Number(model.FrameRate.ToDouble(), 3)} fps");
            if (video.BitDepth != 8)
            {
                parts.Add($"{video.BitDepth} bits");
            }

            if (video.Chroma != ChromaSubsampling.Chroma420)
            {
                parts.Add(ExportChoices.ChromaName(video.Chroma));
            }

            RateControl rate = video.RateControl;
            parts.Add(ExportChoices.RateChoiceOf(rate.Mode, rate.TargetSizeBytes is not null) switch
            {
                RateChoice.Vbr => $"VBR {ExportText.Number((rate.BitrateKbps ?? 0) / 1000.0)} Mbps",
                RateChoice.Cbr => $"CBR {ExportText.Number((rate.BitrateKbps ?? 0) / 1000.0)} Mbps",
                RateChoice.TargetSize => $"tamaño objetivo {ExportText.Bytes(rate.TargetSizeBytes ?? 0)}",
                RateChoice.Quality when encoder.Quality is { } q => $"{q.Name} {ExportText.Number(rate.Quality ?? q.Default)}",
                RateChoice.Lossless => "sin pérdidas",
                _ => video.Profile is { } p && encoder.Profiles.FirstOrDefault(x => x.Id == p) is { } profile ? profile.Name : "perfil por defecto",
            });
        }

        if (s.Audio is { } audio)
        {
            string channels = audio.Channels switch { 1 => "mono", 2 => "estéreo", 6 => "5.1", _ => $"{audio.Channels} canales" };
            string rate = audio.RateControl.Mode == RateControlMode.Lossless ? "sin pérdidas" : $"{audio.RateControl.BitrateKbps} kbps";
            parts.Add($"{ExportChoices.CodecName(audio.Codec)} {rate} {channels}"
                + (audio.Loudness is { } l ? $", {ExportText.Number(l.Lufs)} LUFS" : ""));
        }
        else if (s.Video is not null)
        {
            parts.Add("sin audio");
        }

        // Con una secuencia HDR, si sale en HDR o cae a SDR se dice aquí y no solo en Avanzado, que suele estar plegado.
        return string.Join(" · ", parts) + (model.HdrNote is { } hdr ? $"\n{hdr}" : "");
    }

    /// <summary>Pone las opciones y la elegida; si las opciones no cambiaron, solo mueve la elección (ver <see cref="QueueRefresh"/>).</summary>
    /// <remarks>
    /// Con la lista abierta no se toca nada: cambiar los elementos o la elección de un desplegable abierto de WinUI puede
    /// cerrar la app sin rastro (tarea 91); el próximo refresco lo pone al día (<see cref="OnDropDownClosed"/>). La decisión es
    /// la de los demás desplegables (<see cref="ComboBoxChoices"/>): al rehacer, quita la elección antes de vaciar.
    /// </remarks>
    private static void Fill(ComboBox box, IEnumerable<string> labels, int selected)
    {
        var shown = ComboChoices.FromLabels(box.Items.Select(i => (i as ComboBoxItem)?.Content as string ?? ""));
        var next = ComboChoices.FromLabels(labels);
        _ = ComboBoxChoices.Show(box, shown, next, selected, i => new ComboBoxItem { Content = next.Labels[i] });
    }

    private static int IndexOf<T>(IReadOnlyList<T> items, Func<T, bool> match)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (match(items[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static int Index<T>(IReadOnlyList<T> items, T value) => IndexOf(items, x => EqualityComparer<T>.Default.Equals(x, value));

    private bool TryChange(ComboBox box, out int index)
    {
        index = box.SelectedIndex;
        return !_refreshing && _model is not null && index >= 0;
    }

    private void UpdateVideo(Func<VideoExportSettings, VideoExportSettings> change) =>
        _model?.Update(s => s.Video is { } v ? s with { Video = change(v) } : s);

    private void UpdateAudio(Func<AudioExportSettings, AudioExportSettings> change) =>
        _model?.Update(s => s.Audio is { } a ? s with { Audio = change(a) } : s);

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            _model?.SelectPreset(id);
        }
    }

    private void OnRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(RangeBox, out int index) && index != _model!.RangeIndex)
        {
            _model.SetRange(index);
        }
    }

    private void OnPathLostFocus(object sender, RoutedEventArgs e) => _model?.SetOutputPath(PathBox.Text);

    private void OnPathKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            _model?.SetOutputPath(PathBox.Text);
            e.Handled = true;
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (_model is not { } model)
        {
            return;
        }

        string extension = ExportRanges.Extension(model.Settings);
        EditorSession session = _services.Session;
        string? projectFolder = session.FilePath is { } projectFile ? Path.GetDirectoryName(projectFile) : null;
        string? path = FileDialogs.Save(_hwnd, "Exportar como", Path.GetFileName(model.OutputPath), extension,
            FileDialogFolderPolicy.SaveFolder(_services.Settings.Current, SaveDialogKind.Export, projectFolder),
            ($"{ExportChoices.ContainerName(model.Settings.Container)} (*.{extension})", "*." + extension));
        if (path is not null)
        {
            FileDialogFolderPolicy.RememberSave(_services.Settings.Current, SaveDialogKind.Export, path);
            _services.Settings.SaveIfChanged();
            model.SetOutputPath(path);
            PathBox.Text = model.OutputPath;
        }
    }

    /// <summary>Guarda el borrador actual antes de abrir el formato del proyecto para poder retomarlo al volver.</summary>
    private void OnChangeProjectFormatClick(object sender, RoutedEventArgs e)
    {
        _model?.RememberDraft();
        try
        {
            _services.Settings.SaveIfChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Session.Notify(NotificationSeverity.Warning, "Preferencias de exportación", "No se pudo guardar el borrador de exportación: " + ex.Message);
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
        DispatcherQueue.RunLater(() => _services.Shortcuts.Invoke(Views.Player.PlayerActionIds.ProjectFormat));
    }

    private void OnAdvancedClick(object sender, RoutedEventArgs e)
    {
        bool open = AdvancedToggle.IsChecked == true;
        ShowAdvanced(open);
        _services.Settings.Current.Export.AdvancedOpen = open;
    }

    private void OnContainerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(ContainerBox, out int index))
        {
            ExportContainer container = _containers[index];
            _model!.Update(s => s with { Container = container });
        }
    }

    private void OnFastStartClick(object sender, RoutedEventArgs e) => _model?.Update(s => s with { FastStart = FastStartBox.IsChecked == true });

    private static readonly (SubtitleExport Mode, string Name)[] SubtitleModes =
    [
        (SubtitleExport.Burned, "Quemados en la imagen"),
        (SubtitleExport.Both, "Quemados y como pista aparte"),
        (SubtitleExport.Embedded, "Solo como pista aparte (se activan en el reproductor)"),
    ];

    private void OnSubtitlesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(SubtitlesBox, out int index))
        {
            SubtitleExport mode = SubtitleModes[index].Mode;
            _model!.Update(s => s with { Subtitles = mode });
        }
    }

    private void OnChaptersClick(object sender, RoutedEventArgs e) =>
        _model?.Update(s => s with { ChaptersFromMarkers = ChaptersBox.IsChecked == true });

    private void OnVideoToggled(object sender, RoutedEventArgs e)
    {
        if (!_refreshing && _model is { } model && VideoSwitch.IsOn != (model.Settings.Video is not null))
        {
            model.SetVideoEnabled(VideoSwitch.IsOn);
        }
    }

    private void OnAudioToggled(object sender, RoutedEventArgs e)
    {
        if (!_refreshing && _model is { } model && AudioSwitch.IsOn != (model.Settings.Audio is not null))
        {
            model.SetAudioEnabled(AudioSwitch.IsOn);
        }
    }

    private void OnVideoCodecChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(VideoCodecBox, out int index) && _model!.Settings.Video is { } video && video.Codec != _videoCodecs[index])
        {
            ExportCodec codec = _videoCodecs[index];
            VideoExportSettings fresh = ExportSettingsEditor.DefaultVideo(codec, _model.Encoders);
            UpdateVideo(v => fresh with { Width = v.Width, Height = v.Height, Fit = v.Fit, FrameRate = v.FrameRate, Crop = v.Crop });
        }
    }

    private void OnEncoderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(EncoderBox, out int index))
        {
            string id = _videoEncoders[index].Id;
            UpdateVideo(v => v with { EncoderId = id });
        }
    }

    private void OnRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!TryChange(RateBox, out int index) || _model is not { Settings.Video: { } video } model)
        {
            return;
        }

        (int w, int h) = model.OutputSize;
        int kbps = ExportSettingsEditor.SuggestedVideoKbps(video.Codec, w, h, model.FrameRate.ToDouble());
        RateControl rate = _rates[index] switch
        {
            RateChoice.Vbr => RateControl.Vbr(kbps, kbps * 3 / 2),
            RateChoice.Cbr => RateControl.Cbr(kbps),
            RateChoice.TargetSize => RateControl.TargetSize(Math.Max(1_000_000, model.Estimate.Bytes)),
            RateChoice.Lossless => RateControl.Lossless,
            RateChoice.ProfileDefined => RateControl.ProfileDefined,
            _ => RateControl.ConstantQuality(),
        };
        UpdateVideo(v => v with { RateControl = rate });
    }

    private void OnRateValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue) || _model?.Settings.Video is not { } video)
        {
            return;
        }

        double value = args.NewValue;
        RateControl rate = video.RateControl;
        rate = ExportChoices.RateChoiceOf(rate.Mode, rate.TargetSizeBytes is not null) switch
        {
            RateChoice.Quality => rate with { Quality = Math.Round(value) },
            RateChoice.Vbr => rate with { BitrateKbps = Kbps(value), MaxBitrateKbps = Math.Max(rate.MaxBitrateKbps ?? 0, Kbps(value)) },
            RateChoice.Cbr => rate with { BitrateKbps = Kbps(value), MaxBitrateKbps = Kbps(value) },
            RateChoice.TargetSize => rate with { TargetSizeBytes = (long)Math.Round(Math.Max(1, value) * 1e6) },
            _ => rate,
        };
        UpdateVideo(v => v with { RateControl = rate });
    }

    private void OnRateMaxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_refreshing && !double.IsNaN(args.NewValue) && _model?.Settings.Video is { } video)
        {
            UpdateVideo(v => v with { RateControl = video.RateControl with { MaxBitrateKbps = Kbps(args.NewValue) } });
        }
    }

    private static int Kbps(double mbps) => (int)Math.Max(1, Math.Round(mbps * 1000));

    private void OnSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(SizeBox, out int index))
        {
            SizeChoice size = _sizes[index];
            UpdateVideo(v => v with { Width = size.Width, Height = size.Height });
        }
    }

    private void OnFitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(FitBox, out int index))
        {
            ExportFit fit = FitChoices[index].Value;
            UpdateVideo(v => v with { Fit = fit });
        }
    }

    private void OnFrameRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(FrameRateBox, out int index))
        {
            Rational? rate = ExportGeometry.FrameRates[index].Rate;
            UpdateVideo(v => v with { FrameRate = rate });
        }
    }

    private void OnBitDepthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(BitDepthBox, out int index))
        {
            int bits = _bitDepths[index];
            UpdateVideo(v => v with { BitDepth = bits });
        }
    }

    private void OnChromaChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(ChromaBox, out int index) && index < _chromas.Count)
        {
            ChromaSubsampling chroma = _chromas[index];
            UpdateVideo(v => v with { Chroma = chroma });
        }
    }

    /// <summary>«Copiar la línea de ffmpeg»: los argumentos de la exportación con estos ajustes, al portapapeles.</summary>
    private void OnCopyCommandClick(object sender, RoutedEventArgs e)
    {
        if (_model is not { } model)
        {
            return;
        }

        model.SetOutputPath(PathBox.Text);
        if (!model.CanExport)
        {
            _services.Session.Notify(NotificationSeverity.Informational, "Línea de ffmpeg", "Arregla primero lo que impide exportar: la línea sale de los ajustes válidos.");
            return;
        }

        EditorSession session = _services.Session;
        string? projectFolder = session.FilePath is { } file ? Path.GetDirectoryName(file) : null;
        string text;
        try
        {
            ExportRequest request = model.CreatePreviewRequest(session.Project.Clone(), session.SequenceId, projectFolder);
            text = ExportCommandLine.Text(ExportPlan.Create(request, model.Encoders));
        }
        catch (Exception ex) when (ex is ExportException or ArgumentException or InvalidOperationException)
        {
            session.Notify(NotificationSeverity.Warning, "No se pudo armar la línea de ffmpeg", ex.Message);
            return;
        }

        var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        session.Notify(NotificationSeverity.Success, "Línea de ffmpeg copiada",
            "Es lo que recibe ffmpeg.exe: la imagen entra compuesta por la tubería (pipe:0) y la mezcla por «mezcla.wav», sin capítulos ni pistas de subtítulos.");
    }

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(ProfileBox, out int index))
        {
            string? profile = _profiles[index];
            UpdateVideo(v => v with { Profile = profile });
        }
    }

    private void OnSpeedPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(SpeedPresetBox, out int index))
        {
            string? preset = _speedPresets[index];
            UpdateVideo(v => v with { SpeedPreset = preset });
        }
    }

    private void OnMeasureLightClick(object sender, RoutedEventArgs e) => UpdateVideo(v => v with { MeasureLightLevels = MeasureLightBox.IsChecked == true });

    private void OnKeyframeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_refreshing && !double.IsNaN(args.NewValue))
        {
            double seconds = Math.Clamp(args.NewValue, 0.1, 60);
            UpdateVideo(v => v with { Gop = v.Gop with { KeyframeSeconds = seconds } });
        }
    }

    private void OnAudioCodecChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(AudioCodecBox, out int index) && _model!.Settings.Audio is { } audio && audio.Codec != _audioCodecs[index])
        {
            AudioExportSettings fresh = ExportSettingsEditor.DefaultAudio(_audioCodecs[index], _model.Encoders);
            UpdateAudio(a => fresh with { SampleRate = a.SampleRate, Channels = a.Channels, Loudness = a.Loudness });
        }
    }

    private void OnAudioRateChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_refreshing && !double.IsNaN(args.NewValue))
        {
            int kbps = (int)Math.Round(Math.Clamp(args.NewValue, 32, 512));
            UpdateAudio(a => a with { RateControl = a.RateControl with { BitrateKbps = kbps } });
        }
    }

    private void OnSampleRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(SampleRateBox, out int index))
        {
            int rate = _sampleRates[index];
            UpdateAudio(a => a with { SampleRate = rate });
        }
    }

    private void OnChannelsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(ChannelsBox, out int index))
        {
            int channels = _channels[index];
            UpdateAudio(a => a with { Channels = channels });
        }
    }

    private void OnLoudnessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TryChange(LoudnessBox, out int index))
        {
            LoudnessTarget? target = LoudnessChoices[index].Value;
            UpdateAudio(a => a with { Loudness = target });
        }
    }

    private void OnPerformanceChecked(object sender, RoutedEventArgs e)
    {
        if (!_refreshing && sender is RadioButton { Tag: string tag } && Enum.TryParse(tag, out ExportPerformance performance))
        {
            _model?.SetPerformance(performance);
        }
    }

    private void OnSavePresetClick(object sender, RoutedEventArgs e) => UiTask.Run(OnSavePresetClickAsync(sender, e));

    private async Task OnSavePresetClickAsync(object sender, RoutedEventArgs e)
    {
        if (_model is not { } model || XamlRoot is null)
        {
            return;
        }

        var name = new TextBox { PlaceholderText = "Por ejemplo: Mi canal 1080p", Text = "" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Guardar como preajuste", Content = name, PrimaryButtonText = "Guardar", CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text))
        {
            return;
        }

        if (ExportPresetLibrary.FindUserPreset(_services.Settings.Current.Export, name.Text) is { } existing && XamlRoot is not null)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = $"¿Reemplazar «{existing.Name}»?",
                Content = "Ya hay un preajuste propio con ese nombre. Si guardas, sus ajustes se cambian por los de ahora.",
                PrimaryButtonText = "Reemplazar", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close,
            };
            if (await DialogGate.ShowAsync(confirm) != ContentDialogResult.Primary)
            {
                return;
            }
        }

        try
        {
            ExportPreset preset = model.SavePreset(name.Text);
            _services.Session.Notify(NotificationSeverity.Success, "Preajuste guardado", $"«{preset.Name}» queda entre los preajustes.");
        }
        catch (InvalidOperationException ex)
        {
            _services.Session.Notify(NotificationSeverity.Warning, "Preajustes", ex.Message);
        }
    }

    private void OnImportPresetClick(object sender, RoutedEventArgs e)
    {
        if (_model is not { } model)
        {
            return;
        }

        IReadOnlyList<string> picked = FileDialogs.Open(_hwnd, "Importar preajuste", multiple: false,
            FileDialogFolderPolicy.OpenFolder(_services.Settings.Current),
            ("Preajustes de Rodaje", "*" + ExportPresetLibrary.FileExtension));
        FileDialogFolderPolicy.RememberOpen(_services.Settings.Current, picked.Count > 0 ? picked[0] : null);
        if (picked.Count == 0)
        {
            return;
        }

        try
        {
            ExportPreset preset = ExportPresetLibrary.Import(_services.Settings.Current.Export, picked[0]);
            model.SelectPreset(preset.Id);
            _services.Session.Notify(NotificationSeverity.Success, "Preajuste importado", $"«{preset.Name}» queda entre los preajustes.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _services.Session.Notify(NotificationSeverity.Warning, "No se pudo importar", ex.Message);
        }
    }

    private void OnExportPresetClick(object sender, RoutedEventArgs e)
    {
        if (_model is not { } model || ExportPresetLibrary.Find(_services.Settings.Current.Export, model.PresetId) is not { } preset)
        {
            return;
        }

        string suggested = Path.ChangeExtension(ExportRanges.SuggestFileName(preset.Name, model.Settings), ExportPresetLibrary.FileExtension);
        string? projectFolder = _services.Session.FilePath is { } projectFile ? Path.GetDirectoryName(projectFile) : null;
        string? path = FileDialogs.Save(_hwnd, "Guardar preajuste", suggested, ExportPresetLibrary.FileExtension.TrimStart('.'),
            FileDialogFolderPolicy.SaveFolder(_services.Settings.Current, SaveDialogKind.ExportPreset, projectFolder),
            ("Preajustes de Rodaje", "*" + ExportPresetLibrary.FileExtension));
        if (path is null)
        {
            return;
        }

        FileDialogFolderPolicy.RememberSave(_services.Settings.Current, SaveDialogKind.ExportPreset, path);
        _services.Settings.SaveIfChanged();

        try
        {
            ExportPresetLibrary.WriteFile(preset, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Session.Notify(NotificationSeverity.Warning, "No se pudo guardar el preajuste", ex.Message);
        }
    }

    private void OnRemovePresetClick(object sender, RoutedEventArgs e) => UiTask.Run(OnRemovePresetClickAsync());

    private async Task OnRemovePresetClickAsync()
    {
        if (_model is not { } model || XamlRoot is not { } root || DialogGate.IsOpen
            || ExportPresetLibrary.Find(_services.Settings.Current.Export, model.PresetId) is not { Origin: ExportPresetOrigin.User } preset)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "¿Quitar el preajuste?",
            Content = $"Se quitará «{preset.Name}» de tus preajustes. No se puede deshacer.",
            PrimaryButtonText = "Quitar preajuste",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary
            && ReferenceEquals(_model, model) && model.PresetId == preset.Id)
        {
            model.RemovePreset(preset.Id);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnExportClick(object sender, RoutedEventArgs e) => UiTask.Run(OnExportClickAsync(sender, e));

    private async Task OnExportClickAsync(object sender, RoutedEventArgs e)
    {
        if (_model is not { } model)
        {
            return;
        }

        model.SetOutputPath(PathBox.Text);
        if (!model.CanExport)
        {
            return;
        }

        if (model.OutputCheck == OutputCheck.Exists && XamlRoot is not null)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "¿Reemplazar el archivo?",
                Content = $"Ya existe «{Path.GetFileName(model.OutputPath)}» en esa carpeta, o lo escribirá otra exportación de la cola. Si exportas, se reemplaza al terminar.",
                PrimaryButtonText = "Reemplazar", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close,
            };
            if (await DialogGate.ShowAsync(confirm) != ContentDialogResult.Primary)
            {
                return;
            }
        }

        EditorSession session = _services.Session;
        string? projectFolder = session.FilePath is { } file ? Path.GetDirectoryName(file) : null;
        ExportRequest request = model.CreateRequest(session.Project.Clone(), session.SequenceId, projectFolder);
        ExportEntry entry = _jobs.Enqueue(request);
        Started?.Invoke(this, entry);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
