using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.App.Views.Timeline;

/// <summary>Clips compuestos en la línea de tiempo: atajos, menú, doble clic para entrar y migas de pan para volver.</summary>
internal sealed partial class TimelineView
{
    private const int DoubleClickMilliseconds = 500;

    private string? _lastPressedClip;
    private long _lastPressTicks;

    private void RegisterCompoundActions(EditorServices services)
    {
        ShortcutRegistry registry = services.ShortcutRegistry;
        registry.Register(new ShortcutAction(TimelineActionIds.CreateCompound, "Crear clip compuesto", ShortcutGroups.Edit, ["Alt+G"], agile: ["Ctrl+G"])
        {
            ShortLabel = "Compuesto",
            Description = "Junta los clips seleccionados en un solo clip; doble clic para editar lo de dentro.",
        });
        registry.Register(new ShortcutAction(TimelineActionIds.DecomposeCompound, "Descomponer clip compuesto", ShortcutGroups.Edit, ["Alt+Mayús+G"], agile: ["Ctrl+Mayús+G"])
        {
            ShortLabel = "Descomponer",
            Description = "Devuelve a la línea de tiempo los clips que había dentro del compuesto seleccionado.",
        });
        registry.Register(new ShortcutAction(TimelineActionIds.LeaveCompound, "Salir del clip compuesto", ShortcutGroups.Timeline, [])
        {
            ShortLabel = "Salir",
            Description = "Vuelve a la secuencia que contiene el compuesto que se está editando.",
        });

        ShortcutRouter router = services.Shortcuts;
        router.Bind(TimelineActionIds.CreateCompound, () => Session!.CreateCompoundFromSelection(), () => Session?.CanCreateCompound == true);
        router.Bind(TimelineActionIds.DecomposeCompound, () => Session!.DecomposeCompound(), () => Session?.SelectedCompoundId is not null);
        router.Bind(TimelineActionIds.LeaveCompound, () => Session!.LeaveCompound(), () => Session?.SequencePath.Count > 1);
    }

    private void AddCompoundItems(MenuFlyout menu)
    {
        EditorSession session = Session!;
        menu.Items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = "Crear clip compuesto", IsEnabled = session.CanCreateCompound };
        create.Click += (_, _) => session.CreateCompoundFromSelection();
        menu.Items.Add(create);
        if (session.SelectedCompoundId is { } compound)
        {
            var open = new MenuFlyoutItem { Text = "Abrir clip compuesto" };
            open.Click += (_, _) => session.EnterCompound(compound);
            menu.Items.Add(open);
            var decompose = new MenuFlyoutItem { Text = "Descomponer clip compuesto" };
            decompose.Click += (_, _) => session.DecomposeCompound(compound);
            menu.Items.Add(decompose);
        }
    }

    /// <summary>Segundo clic seguido sobre el mismo compuesto: entra a editarlo.</summary>
    private bool IsCompoundDoubleClick(Clip? clip)
    {
        long now = Environment.TickCount64;
        bool twice = clip is not null && clip.Id == _lastPressedClip && now - _lastPressTicks <= DoubleClickMilliseconds;
        _lastPressedClip = twice ? null : clip?.Id;
        _lastPressTicks = now;
        return twice && clip!.IsCompound;
    }

    /// <summary>Las migas de pan (solo dentro de un compuesto) y el estado del visor de origen.</summary>
    private void RefreshContextBar()
    {
        if (Session is not { } session)
        {
            return;
        }

        IReadOnlyList<Sequence> path = session.SequencePath;
        BreadcrumbPanel.Children.Clear();
        BreadcrumbPanel.Visibility = path.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; path.Count > 1 && i < path.Count; i++)
        {
            if (i > 0)
            {
                BreadcrumbPanel.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
            }

            string name = path[i].Name.Length > 0 ? path[i].Name : $"Secuencia {i + 1}";
            if (i == path.Count - 1)
            {
                BreadcrumbPanel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                continue;
            }

            int depth = i;
            var link = new HyperlinkButton { Content = name, Padding = new Thickness(4, 0, 4, 0), MinHeight = 0 };
            link.Click += (_, _) => Session?.LeaveCompound(depth);
            BreadcrumbPanel.Children.Add(link);
        }

        RefreshSourcePanel();
        RefreshMulticamPanel();
        ContextBar.Visibility = BreadcrumbPanel.Visibility == Visibility.Visible || SourcePanel.Visibility == Visibility.Visible || MulticamPanel.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RefreshMulticamPanel()
    {
        if (Session is not { } session || session.MainTrack?.ClipAt(session.EditPosition) is not { } clip
            || session.Multicams.FirstOrDefault(g => MulticamEdits.IndexOf(g, clip.MediaId) >= 0) is not { } group)
        {
            _multicamKey = null;
            _multicamButtons.Clear();
            MulticamPanel.Children.Clear();
            MulticamPanel.Visibility = Visibility.Collapsed;
            return;
        }

        string key = string.Join("|", group.Select(a => a.MediaId));
        if (_multicamKey != key)
        {
            _multicamKey = key;
            _multicamButtons.Clear();
            MulticamPanel.Children.Clear();
            MulticamPanel.Children.Add(new TextBlock { Text = "Multicámara", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            for (int i = 0; i < group.Count; i++)
            {
                int angle = i;
                string name = session.Project.FindMedia(group[i].MediaId)?.Name ?? $"Cámara {i + 1}";
                var button = new ToggleButton
                {
                    Content = $"{i + 1} {name}",
                    Style = (Style)Application.Current.Resources["ToolToggleButtonStyle"],
                    MinWidth = 0,
                    Padding = new Thickness(8, 0, 8, 0),
                    Tag = i,
                };
                button.Click += (_, _) => SwitchAngle(angle);
                _multicamButtons.Add(button);
                MulticamPanel.Children.Add(button);
            }
        }

        int active = MulticamEdits.IndexOf(group, clip.MediaId);
        for (int i = 0; i < _multicamButtons.Count; i++)
        {
            _multicamButtons[i].IsChecked = i == active;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_multicamButtons[i], $"Ángulo {i + 1}: {session.Project.FindMedia(group[i].MediaId)?.Name}; {(i == active ? "activo" : "inactivo")}");
        }

        MulticamPanel.Visibility = Visibility.Visible;
    }}
