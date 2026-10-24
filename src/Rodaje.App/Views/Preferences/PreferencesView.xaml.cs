using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Views.Preferences;

internal sealed partial class PreferencesView : UserControl
{
    private PreferencesViewModel? _model;

    public PreferencesView()
    {
        InitializeComponent();
        ProxyUsageBox.Items.Add("Automático");
        ProxyUsageBox.Items.Add("Siempre que haya");
        ProxyUsageBox.Items.Add("Nunca (originales)");
        PreviewQualityBox.Items.Add("Automática");
        PreviewQualityBox.Items.Add("Completa");
        PreviewQualityBox.Items.Add("Mitad");
        PreviewQualityBox.Items.Add("Cuarto");
        TenBitViewerBox.Items.Add("Automático (si el monitor es de 10 bits)");
        TenBitViewerBox.Items.Add("Siempre");
        TenBitViewerBox.Items.Add("Nunca (8 bits)");
        ShortcutPresetBox.Items.Add("Estilo Rodaje");
        ShortcutPresetBox.Items.Add("Estilo Ágil");
        ShortcutPresetBox.Items.Add("Estilo Clásico");
    }

    public void Attach(AppSettings settings)
    {
        _model = new PreferencesViewModel(settings);
        VideoTracksBox.Value = _model.VideoTracks;
        AudioTracksBox.Value = _model.AudioTracks;
        LinkedAudioTracksPanel.Children.Clear();
        foreach (int number in Enumerable.Range(1, NewProjectTracks.Max))
        {
            LinkedAudioTracksPanel.Children.Add(new CheckBox
            {
                Content = $"Video {number}",
                Tag = number,
                IsChecked = _model.LinkedAudioVideoTracks.Contains(number),
            });
        }
        AutoProxiesBox.IsChecked = _model.GenerateProxies;
        GeneratedFolderBox.Text = _model.GeneratedMediaFolder;
        StillImageDurationBox.Value = _model.StillImageDurationSeconds;
        StillCaptureFolderBox.Text = _model.StillCaptureFolder;
        ProxyUsageBox.SelectedIndex = (int)_model.ProxyUsage;
        PreviewQualityBox.SelectedIndex = (int)_model.PreviewQuality;
        TenBitViewerBox.SelectedIndex = (int)_model.TenBitViewer;
        ShortcutPresetBox.SelectedIndex = (int)_model.ShortcutPreset;
    }

    public void Apply()
    {
        if (_model is not { } model) return;
        model.VideoTracks = double.IsFinite(VideoTracksBox.Value) ? (int)VideoTracksBox.Value : NewProjectTracks.Default.Video;
        model.AudioTracks = double.IsFinite(AudioTracksBox.Value) ? (int)AudioTracksBox.Value : NewProjectTracks.Default.Audio;
        model.StillImageDurationSeconds = double.IsFinite(StillImageDurationBox.Value) ? StillImageDurationBox.Value : Rodaje.Core.Model.MediaRef.DefaultStillImageDurationSeconds;
        model.StillCaptureFolder = StillCaptureFolderBox.Text;
        model.GenerateProxies = AutoProxiesBox.IsChecked == true;
        model.GeneratedMediaFolder = GeneratedFolderBox.Text;
        model.ProxyUsage = (ProxyUsage)Math.Max(0, ProxyUsageBox.SelectedIndex);
        model.PreviewQuality = (PreviewQuality)Math.Max(0, PreviewQualityBox.SelectedIndex);
        model.TenBitViewer = (TenBitViewer)Math.Max(0, TenBitViewerBox.SelectedIndex);
        model.ShortcutPreset = (ShortcutPreset)Math.Max(0, ShortcutPresetBox.SelectedIndex);
        model.SetLinkedAudioVideoTracks(LinkedAudioTracksPanel.Children.OfType<CheckBox>()
            .Where(checkBox => checkBox.IsChecked == true)
            .Select(checkBox => (int)checkBox.Tag));
        model.Settings.Normalize();
    }
}
