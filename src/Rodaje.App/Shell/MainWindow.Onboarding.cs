using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Onboarding;
using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Shell;

/// <summary>
/// El recorrido del primer arranque (una burbuja que va de la biblioteca a Exportar, con Siguiente y Saltar) y las pistas
/// discretas la primera vez que se usa algo. Ayuda › «Recorrido inicial» lo repite y «Volver a mostrar las pistas» las reactiva.
/// </summary>
#pragma warning disable CA1001 // Lo descartable se libera en OnClosed (MainWindow.xaml.cs).
internal sealed partial class MainWindow
#pragma warning restore CA1001
{
    private const string TourActionId = "help.tour";
    private const string ResetHintsActionId = "help.resetHints";

    private TeachingTip? _tourTip;
    private TeachingTip? _hintTip;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hintTimer;
    private int _tourStep = -1;

    private void AttachOnboarding()
    {
        ShortcutRegistry registry = _router.Registry;
        registry.Register(new ShortcutAction(TourActionId, "Recorrido inicial", ShortcutGroups.Windows, []));
        registry.Register(new ShortcutAction(ResetHintsActionId, "Volver a mostrar las pistas", ShortcutGroups.Windows, []));
        _bindings.Add(_router.Bind(TourActionId, () => StartTour()));
        _bindings.Add(_router.Bind(ResetHintsActionId, () =>
        {
            OnboardingGuide.ResetHints(_settings.Current);
            Session.Notify(NotificationSeverity.Informational, "Pistas", "Las pistas vuelven a aparecer la primera vez que uses cada cosa.");
        }));
        if (_options.IsAutomated)
        {
            return;
        }

        Session.MediaImported += (_, _) => ShowHint(OnboardingGuide.FirstImport);
        Session.HistoryChanged += (_, _) =>
        {
            if (Session.CanUndo && Session.UndoDescription is { } step && !step.StartsWith("Importar", StringComparison.Ordinal))
            {
                ShowHint(OnboardingGuide.FirstEdit);
            }
        };
        _exports.Changed += (_, _) =>
        {
            if (_exports.ActiveCount > 0)
            {
                ShowHint(OnboardingGuide.FirstExport);
            }
        };
    }

    /// <summary>Al primer arranque, cuando la ventana ya se ve y no hay otra pregunta abierta.</summary>
    private async Task OfferTourAsync()
    {
        if (_options.IsAutomated || _settings.Current.TutorialSeen)
        {
            return;
        }

        await Task.Delay(800).ConfigureAwait(true);
        while (DialogGate.IsOpen && !_closed)
        {
            await Task.Delay(500).ConfigureAwait(true);
        }

        if (!_closed)
        {
            StartTour();
        }
    }

    private void StartTour()
    {
        if (_playerFullScreen)
        {
            Player.SetFullScreen(false);
        }

        ClosePage();
        if (_tourTip is null)
        {
            _tourTip = new TeachingTip
            {
                ActionButtonContent = "Siguiente", CloseButtonContent = "Saltar el recorrido", IsLightDismissEnabled = false,
                PreferredPlacement = TeachingTipPlacementMode.Auto,
            };
            _tourTip.ActionButtonClick += (_, _) => ShowTourStep(_tourStep + 1);
            _tourTip.CloseButtonClick += (_, _) => EndTour();
            Root.Children.Add(_tourTip);
        }

        ShowTourStep(0);
    }

    private void ShowTourStep(int index)
    {
        if (_tourTip is null)
        {
            return;
        }

        if (index >= OnboardingGuide.Tour.Count)
        {
            EndTour();
            return;
        }

        _tourStep = index;
        TourStep step = OnboardingGuide.Tour[index];
        _tourTip.IsOpen = false;
        _tourTip.Target = step.Target switch
        {
            TourTarget.Library => Library,
            TourTarget.Player => Player,
            TourTarget.Tools => Tools,
            TourTarget.Timeline => Timeline,
            TourTarget.Inspector => Inspector,
            _ => TitleBar.ExportButton,
        };
        _tourTip.Title = step.Title;
        _tourTip.Subtitle = step.Text;
        _tourTip.ActionButtonContent = index == OnboardingGuide.Tour.Count - 1 ? "Empezar" : "Siguiente";
        _tourTip.IsOpen = true;
    }

    private void EndTour()
    {
        _tourStep = -1;
        if (_tourTip is not null)
        {
            _tourTip.IsOpen = false;
        }

        _settings.Current.TutorialSeen = true;
    }

    /// <summary>Una pista en la esquina, una sola vez por cosa; se cierra sola a los 8 s y nunca tapa el recorrido.</summary>
    private void ShowHint(string id)
    {
        if (_tourStep >= 0 || _closed || OnboardingGuide.TakeHint(_settings.Current, id) is not { } hint)
        {
            return;
        }

        if (_hintTip is null)
        {
            _hintTip = new TeachingTip
            {
                PreferredPlacement = TeachingTipPlacementMode.BottomRight, IsLightDismissEnabled = true, CloseButtonContent = "Entendido",
            };
            Root.Children.Add(_hintTip);
            _hintTimer = _dispatcher.CreateTimer();
            _hintTimer.Interval = TimeSpan.FromSeconds(8);
            _hintTimer.IsRepeating = false;
            _hintTimer.Tick += (_, _) => _hintTip.IsOpen = false;
        }

        _hintTip.Title = hint.Title;
        _hintTip.Subtitle = hint.Text;
        _hintTip.IsOpen = true;
        _hintTimer!.Stop();
        _hintTimer.Start();
    }
}
