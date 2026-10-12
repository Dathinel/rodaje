using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using Rodaje.App.Views.Analysis;
using Rodaje.Audio;
using Rodaje.Audio.Capture;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Recording;

namespace Rodaje.App.Views.Voice;

/// <summary>
/// «Grabar voz en off»: elige el micrófono y la pista, cuenta atrás y graba en el cursor mientras suena el resto de la
/// línea de tiempo (con la pista destino en silencio, si se pide). Al detener, la toma entra como clip en un paso.
/// </summary>
internal static class VoiceOverDialog
{
    private const int CountdownSeconds = 3;
    private const string NewTrack = "\u0001nueva";

    private static string? _lastDevice;
    private static bool _muteTarget = true;

    public static async Task ShowAsync(EditorServices services, string? trackId = null)
    {
        EditorSession session = services.Session;
        if (services.Shell.XamlRoot is not { } root || DialogGate.IsOpen)
        {
            return;
        }

        IReadOnlyList<AudioCaptureDevice> devices = await Task.Run(AudioCapture.ListDevices);
        if (devices.Count == 0)
        {
            session.Notify(NotificationSeverity.Warning, "Grabar voz en off",
                "Windows no tiene ningún micrófono activo. Conéctalo o actívalo en Configuración › Sistema › Sonido.");
            return;
        }

        var panel = new StackPanel { Spacing = 8, Width = AnalysisUi.PreviewWidth };
        var microphone = new ComboBox { Header = "Micrófono", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (AudioCaptureDevice device in devices)
        {
            microphone.Items.Add(new ComboBoxItem { Content = device.IsDefault ? device.Name + " (predeterminado)" : device.Name, Tag = device.Id });
        }

        microphone.SelectedIndex = Math.Max(0, devices.ToList().FindIndex(d => d.Id == _lastDevice));
        panel.Children.Add(microphone);

        var track = new ComboBox { Header = "Pista", HorizontalAlignment = HorizontalAlignment.Stretch };
        track.Items.Add(new ComboBoxItem { Content = "Pista nueva «" + EditorSession.VoiceOverTrackName + "»", Tag = NewTrack });
        foreach (Track t in session.Sequence.AudioTracks.Where(t => !t.Locked))
        {
            track.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });
        }

        track.SelectedIndex = Math.Max(0, track.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == trackId));
        panel.Children.Add(track);
        var mute = new CheckBox { Content = "Silenciar la pista mientras grabo", IsChecked = _muteTarget };
        panel.Children.Add(mute);
        panel.Children.Add(new TextBlock
        {
            Text = $"Graba desde el cursor ({Timecode(session.EditPosition)}) tras una cuenta atrás de {CountdownSeconds} s, mientras suena el resto. " +
                "Usa auriculares para que el micrófono no recoja lo que suena.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
        });

        ContentDialog setup = AnalysisUi.Dialog(root, "Grabar voz en off", "Grabar", panel);
        if (await DialogGate.ShowAsync(setup) != ContentDialogResult.Primary)
        {
            return;
        }

        _lastDevice = (microphone.SelectedItem as ComboBoxItem)?.Tag as string;
        _muteTarget = mute.IsChecked == true;
        string? target = (track.SelectedItem as ComboBoxItem)?.Tag as string is { } tag && tag != NewTrack ? tag : null;
        await RecordAsync(services, root, _lastDevice, target, _muteTarget && target is not null);
    }

    private static async Task RecordAsync(EditorServices services, XamlRoot root, string? deviceId, string? trackId, bool muteTarget)
    {
        EditorSession session = services.Session;
        session.Pause();
        Time start = session.EditPosition;
        string folder = GeneratedMedia.Ensure(session.FilePath, services.Settings.Current.GeneratedMediaFolder);
        string path = Path.Combine(folder, "voz-en-off-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".wav");

        VoiceOverRecorder recorder;
        string deviceName;
        try
        {
            IAudioCapture capture = await Task.Run(() => AudioCapture.Open(deviceId));
            deviceName = capture.DeviceName;
            recorder = new VoiceOverRecorder(capture, path, start, qpc => services.Player.HeardPositionAt(qpc, start));
        }
        catch (Exception ex) when (ex is AudioDeviceUnavailableException or IOException or UnauthorizedAccessException)
        {
            session.Notify(NotificationSeverity.Error, "No se pudo abrir el micrófono", ex.Message);
            return;
        }

        var status = new TextBlock { FontSize = ThemeResources.Size("DisplayFontSize"), HorizontalAlignment = HorizontalAlignment.Center };
        var level = new ProgressBar { Maximum = 1, Width = AnalysisUi.PreviewWidth, Height = 6 };
        var note = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextStyle"], TextWrapping = TextWrapping.Wrap, Width = AnalysisUi.PreviewWidth };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(status);
        content.Children.Add(level);
        content.Children.Add(note);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Voz en off",
            Content = content,
            CloseButtonText = "Detener",
            RequestedTheme = ElementTheme.Dark,
        };

        bool recording = false;
        bool cancelled = false;
        int remaining = CountdownSeconds;
        status.Text = remaining.ToString(CultureInfo.InvariantCulture);
        note.Text = "Prepárate: empieza a sonar al llegar a cero.";
        DispatcherQueueTimer timer = root.Content.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        long ticks = 0;
        timer.Tick += (_, _) =>
        {
            ticks++;
            if (!recording)
            {
                if (ticks % 10 != 0)
                {
                    return;
                }

                remaining--;
                if (remaining > 0)
                {
                    status.Text = remaining.ToString(CultureInfo.InvariantCulture);
                    return;
                }

                recording = true;
                if (muteTarget && trackId is not null)
                {
                    Project muted = session.Project.Clone();
                    if (muted.Sequences.Find(s => s.Id == session.SequenceId)?.FindTrack(trackId) is { } t)
                    {
                        t.Muted = true;
                    }

                    services.Player.Reload(muted);
                }

                recorder.Begin();
                session.Play();
                note.Text = $"Micrófono: {deviceName}. «Detener» o Esc termina la toma.";
            }

            status.Text = "● " + Timecode(recorder.Recorded);
            level.Value = Math.Min(1, Math.Sqrt(recorder.TakePeak()));
        };
        timer.Start();
        dialog.Closing += (_, _) => cancelled = !recording;
        await DialogGate.ShowAsync(dialog);
        timer.Stop();
        session.Pause();
        if (muteTarget)
        {
            services.Player.Reload(session.Project.Clone());
        }

        VoiceOverTake take;
        try
        {
            take = recorder.Finish();
        }
        catch (IOException ex)
        {
            recorder.Dispose();
            session.Notify(NotificationSeverity.Error, "No se pudo guardar la voz en off", ex.Message);
            return;
        }

        recorder.Dispose();
        if (cancelled || take.Frames == 0)
        {
            TryDelete(take.Path);
            if (!cancelled)
            {
                session.Notify(NotificationSeverity.Informational, "Voz en off", "No llegó a grabarse nada.");
            }

            return;
        }

        if (!session.InsertVoiceOver(take.Path, take.Frames, take.SampleRate, take.Start, trackId))
        {
            session.Notify(NotificationSeverity.Warning, "Voz en off", $"La toma quedó guardada en {take.Path}, pero no se pudo poner en la línea de tiempo.");
        }
    }

    private static string Timecode(Time time)
    {
        double seconds = Math.Max(0, time.ToSeconds());
        return string.Create(CultureInfo.InvariantCulture, $"{(int)(seconds / 60):00}:{seconds % 60:00.0}").Replace('.', ',');
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
