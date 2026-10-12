using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Voice;
using Rodaje.App.Shell;
using Rodaje.App.Views.Analysis;
using Rodaje.App.Views.Shared;

namespace Rodaje.App.Views.Voice;

/// <summary>
/// «Texto a voz»: texto escrito, pegado o tomado del clip de texto o subtítulo seleccionado; idioma, voz, velocidad, tono
/// y pausas; «Escuchar» sin tocar el proyecto e «Insertar en el cursor» en un paso del historial. Con un clip de voz
/// generada seleccionado se abre con sus ajustes y ofrece «Regenerar» (solo si cambió algo que se oye).
/// </summary>
internal static class VoiceDialog
{
    private static VoiceSettings _last = new("");

    public static async Task ShowAsync(EditorServices services)
    {
        EditorSession session = services.Session;
        if (services.Shell.XamlRoot is not { } root)
        {
            return;
        }

        IVoiceSynthesizer synth = services.Modules is { } modules ? new ModuleVoiceSynthesizer(modules) : new MissingSynthesizer();
        if (!synth.IsInstalled)
        {
            session.Notify(NotificationSeverity.Informational, "Texto a voz", ModuleVoiceSynthesizer.NotInstalled);
            return;
        }

        (Core.Model.Clip Clip, VoiceSettings Settings)? existing = session.SelectedGeneratedVoice();
        VoiceSettings start = existing?.Settings ?? (_last with { Text = session.SelectedClipText() ?? _last.Text });

        var panel = new StackPanel { Spacing = 4, Width = AnalysisUi.PreviewWidth };
        var text = new TextBox
        {
            Text = start.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 120,
            PlaceholderText = "Escribe o pega el texto. «[pausa 1]» agrega un silencio de 1 s.",
        };
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        panel.Children.Add(text);
        var fromClip = new HyperlinkButton { Content = "Usar el texto del clip seleccionado", IsEnabled = session.SelectedClipText() is not null, Padding = new Thickness(0) };
        fromClip.Click += (_, _) => text.Text = session.SelectedClipText() ?? text.Text;
        panel.Children.Add(fromClip);

        var language = new ComboBox { Header = "Idioma", Width = AnalysisUi.PreviewWidth, DisplayMemberPath = nameof(VoiceLanguage.Name) };
        var voice = new ComboBox { Header = "Voz", Width = AnalysisUi.PreviewWidth, DisplayMemberPath = nameof(VoiceChoice.Label) };
        panel.Children.Add(language);
        panel.Children.Add(voice);

        Slider speed = AnalysisUi.Slider(panel, "Velocidad", VoiceSettings.MinSpeed, VoiceSettings.MaxSpeed, start.Speed, 0.05, v => Fmt($"{v:0.00}×"), () => { });
        Slider pitch = AnalysisUi.Slider(panel, "Tono", -VoiceSettings.MaxPitch, VoiceSettings.MaxPitch, start.Pitch, 0.5, v => Fmt($"{v:+0.#;-0.#;0} semitonos"), () => { });
        Slider sentencePause = AnalysisUi.Slider(panel, "Pausa entre frases", 0, 2, start.SentencePause, 0.05, AnalysisUi.Sec, () => { });
        Slider commaPause = AnalysisUi.Slider(panel, "Pausa en comas", 0, 1, start.CommaPause, 0.05, AnalysisUi.Sec, () => { });

        var listen = new Button { Content = "Escuchar", IsEnabled = false };
        var busy = new ProgressRing { IsActive = false, Width = 20, Height = 20 };
        var status = new TextBlock { Text = "Cargando las voces…", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Width = 330 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(listen);
        row.Children.Add(busy);
        row.Children.Add(status);
        panel.Children.Add(row);
        var saveIn = new GeneratedFolderRow(services);
        panel.Children.Add(saveIn.Element);

        ContentDialog dialog = AnalysisUi.Dialog(root, "Texto a voz", existing is null ? "Insertar en el cursor" : "Regenerar", panel);
        if (existing is not null)
        {
            dialog.SecondaryButtonText = "Insertar nuevo";
        }

        VoiceCatalog catalog = VoiceCatalog.Empty;
        (VoiceSettings Settings, SynthesizedVoice Voice)? made = null;
        using var player = new VoicePreviewPlayer();
        using var cancel = new CancellationTokenSource();
        bool generating = false;
        int plays = 0;

        VoiceSettings Current() => new(
            text.Text,
            (language.SelectedItem as VoiceLanguage)?.Id ?? start.Language,
            (voice.SelectedItem as VoiceChoice)?.Voice.Id ?? start.Voice,
            speed.Value, pitch.Value, sentencePause.Value, commaPause.Value);

        void Refresh()
        {
            bool ready = !generating && catalog.Voices.Count > 0 && voice.SelectedItem is not null && !string.IsNullOrWhiteSpace(text.Text);
            listen.IsEnabled = ready || player.IsPlaying;
            dialog.IsPrimaryButtonEnabled = ready && (existing is null || existing.Value.Settings.SoundsDifferent(Current()));
            dialog.IsSecondaryButtonEnabled = ready;
        }

        void FillVoices()
        {
            if (language.SelectedItem is not VoiceLanguage lang)
            {
                return;
            }

            string wanted = (voice.SelectedItem as VoiceChoice)?.Voice.Id ?? start.Voice;
            VoiceChoice[] choices = [.. catalog.VoicesFor(lang.Id).Select(v => new VoiceChoice(v))];
            voice.ItemsSource = choices;
            voice.SelectedItem = choices.FirstOrDefault(c => c.Voice.Id == wanted) ?? choices.FirstOrDefault();
        }

        async Task<SynthesizedVoice?> Generate()
        {
            VoiceSettings wanted = Current().Clamped();
            if (made is { } m && !m.Settings.SoundsDifferent(wanted))
            {
                return m.Voice;
            }

            generating = true;
            busy.IsActive = true;
            status.Text = "Generando la voz…";
            Refresh();
            try
            {
                SynthesizedVoice v = await synth.SynthesizeAsync(wanted, "Texto a voz", cancel.Token);
                made = (wanted, v);
                status.Text = Fmt($"Listo: {v.Seconds:0.0} s de audio.");
                return v;
            }
            catch (OperationCanceledException)
            {
                status.Text = "Cancelado.";
                return null;
            }
#pragma warning disable CA1031 // Un fallo del módulo se explica en el diálogo; el proyecto no cambia.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                status.Text = ex is VoiceException ? ex.Message : "No se pudo generar la voz. El proyecto no cambió.";
                return null;
            }
            finally
            {
                generating = false;
                busy.IsActive = false;
                Refresh();
            }
        }

        text.TextChanged += (_, _) => Refresh();
        language.SelectionChanged += (_, _) => { FillVoices(); Refresh(); };
        voice.SelectionChanged += (_, _) => Refresh();
        foreach (Slider s in (Slider[])[speed, pitch, sentencePause, commaPause])
        {
            s.ValueChanged += (_, _) => Refresh();
        }

        listen.Click += async (_, _) =>
        {
            if (player.IsPlaying)
            {
                plays++;
                player.Stop();
                listen.Content = "Escuchar";
                Refresh();
                return;
            }

            if (await Generate() is { } v)
            {
                services.Player.Pause();
                player.Play(v);
                if (player.Problem is { } problem)
                {
                    status.Text = problem;
                }

                listen.Content = "Detener";
                int play = ++plays;
                await Task.Delay(TimeSpan.FromSeconds(v.Seconds + 0.3));
                if (play == plays)
                {
                    player.Stop();
                    listen.Content = "Escuchar";
                    Refresh();
                }
            }
        };

        // Insertar o regenerar espera a la voz dentro del diálogo; si falla, el diálogo queda abierto con el motivo.
        SynthesizedVoice? chosen = null;
        async void Accept(ContentDialog _, ContentDialogButtonClickEventArgs args)
        {
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            try
            {
                chosen = await Generate();
                args.Cancel = chosen is null;
            }
            finally
            {
                deferral.Complete();
            }
        }

        dialog.PrimaryButtonClick += Accept;
        dialog.SecondaryButtonClick += Accept;
        _ = LoadVoices();

        ContentDialogResult? answer = await DialogGate.ShowAsync(dialog);
        await cancel.CancelAsync();
        player.Stop();
        if (answer is not (ContentDialogResult.Primary or ContentDialogResult.Secondary) || chosen is null || made is not { } done)
        {
            return;
        }

        _last = done.Settings;
        string folder = GeneratedMedia.Folder(session.FilePath, services.Settings.Current.GeneratedMediaFolder);
        string wav;
        try
        {
            folder = saveIn.Folder;
            wav = await Task.Run(() => VoiceFiles.Save(folder, done.Settings, chosen));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            session.Notify(NotificationSeverity.Error, "No se pudo guardar la voz", $"{folder}: {ex.Message}");
            return;
        }

        if (answer == ContentDialogResult.Primary && existing is { } e)
        {
            session.ReplaceGeneratedVoice(e.Clip.Id, wav, done.Settings, chosen.Samples.Length, chosen.SampleRate);
        }
        else
        {
            session.InsertGeneratedVoice(wav, done.Settings, chosen.Samples.Length, chosen.SampleRate);
        }

        async Task LoadVoices()
        {
            try
            {
                catalog = await synth.GetVoicesAsync(cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // Sin voces el diálogo explica el motivo y no deja generar.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                status.Text = ex is VoiceException ? ex.Message : "No se pudieron leer las voces del módulo.";
                return;
            }

            if (catalog.Voices.Count == 0)
            {
                status.Text = "El módulo de voz no tiene voces instaladas.";
                return;
            }

            VoiceLanguage[] langs = [.. catalog.Languages.Where(l => catalog.VoicesFor(l.Id).Count > 0)];
            language.ItemsSource = langs;
            language.SelectedItem = langs.FirstOrDefault(l => l.Id == start.Language) ?? langs.FirstOrDefault();
            FillVoices();
            status.Text = existing is null ? "" : "Cambia el texto o la voz y pulsa «Regenerar».";
            Refresh();
        }
    }

    private static string Fmt(FormattableString s) => s.ToString(SpanishNumbers.Format);

    private sealed record VoiceChoice(VoiceInfo Voice)
    {
        public string Label => Voice.Name + (Voice.Gender switch { "f" => " (mujer)", "m" => " (hombre)", _ => "" });
    }

    private sealed class MissingSynthesizer : IVoiceSynthesizer
    {
        public bool IsInstalled => false;

        public Task<VoiceCatalog> GetVoicesAsync(CancellationToken cancellationToken) => Task.FromResult(VoiceCatalog.Empty);

        public Task<SynthesizedVoice> SynthesizeAsync(VoiceSettings settings, string title, CancellationToken cancellationToken) =>
            throw new VoiceException(ModuleVoiceSynthesizer.NotInstalled);
    }
}
