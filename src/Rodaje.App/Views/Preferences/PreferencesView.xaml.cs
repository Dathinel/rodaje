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
        ShortcutPresetBox.Items.Add("Estilo Rodaje");
        ShortcutPresetBox.Items.Add("Estilo Ágil");
        ShortcutPresetBox.Items.Add("Estilo Clásico");
    }

    public void Attach(AppSettings settings)
    {
        _model = new PreferencesViewModel(settings);
        VideoTracksBox.Value = _model.VideoTracks;
        AudioTracksBox.Value = _model.AudioTracks;
        LinkedAudioBox.IsChecked = settings.LinkedAudioTracks.IsAll;
        AutoProxiesBox.IsChecked = _model.GenerateProxies;
        GeneratedFolderBox.Text = _model.GeneratedMediaFolder;
        ProxyUsageBox.SelectedIndex = (int)_model.ProxyUsage;
        PreviewQualityBox.SelectedIndex = (int)_model.PreviewQuality;
        ShortcutPresetBox.SelectedIndex = (int)_model.ShortcutPreset;
    }

    public void Apply()
    {
        if (_model is not { } model) return;
        model.VideoTracks = double.IsFinite(VideoTracksBox.Value) ? (int)VideoTracksBox.Value : NewProjectTracks.Default.Video;
        model.AudioTracks = double.IsFinite(AudioTracksBox.Value) ? (int)AudioTracksBox.Value : NewProjectTracks.Default.Audio;
        model.GenerateProxies = AutoProxiesBox.IsChecked == true;
        model.GeneratedMediaFolder = GeneratedFolderBox.Text;
        model.ProxyUsage = (ProxyUsage)Math.Max(0, ProxyUsageBox.SelectedIndex);
        model.PreviewQuality = (PreviewQuality)Math.Max(0, PreviewQualityBox.SelectedIndex);
        model.ShortcutPreset = (ShortcutPreset)Math.Max(0, ShortcutPresetBox.SelectedIndex);
        model.Settings.LinkedAudioVideoTracks = LinkedAudioBox.IsChecked == true ? null : [];
        model.Settings.Normalize();
    }
}
