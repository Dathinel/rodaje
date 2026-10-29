using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;
using PlacementMode = Rodaje.Core.Editing.PlacementMode;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// Edición de tres y cuatro puntos desde la línea de tiempo: marcas I/O del visor activo (origen o programa), insertar
/// (,) y sobrescribir (.), los selectores de pista de origen V/A con su pista destino, y la pregunta de cuatro puntos.
/// </summary>
internal sealed partial class TimelineView
{
    private const string MarkInId = "edit.markIn";
    private const string MarkOutId = "edit.markOut";
    private const string ClearMarksId = "edit.clearMarks";
    private const string InsertId = "edit.insertSource";
    private const string OverwriteId = "edit.overwriteSource";
    private const string ToggleSourceId = "view.sourceProgram";

    private bool _fillingSourcePanel;

    private void RegisterThreePointActions(EditorServices services)
    {
        ShortcutRegistry registry = services.ShortcutRegistry;
        registry.Register(new ShortcutAction(MarkInId, "Marcar entrada", ShortcutGroups.Edit, ["I"]) { ShortLabel = "Entrada", Description = "Marca la entrada en el fotograma que se ve, en el origen o en la secuencia." });
        registry.Register(new ShortcutAction(MarkOutId, "Marcar salida", ShortcutGroups.Edit, ["O"]) { ShortLabel = "Salida", Description = "Marca la salida: el fotograma que se ve es el último que entra." });
        registry.Register(new ShortcutAction(ClearMarksId, "Quitar entrada y salida", ShortcutGroups.Edit, ["Alt+X"], classic: ["Ctrl+Mayús+X"]) { ShortLabel = "Quitar marcas", Description = "Borra las marcas del visor que se ve." });
        registry.Register(new ShortcutAction(InsertId, "Insertar desde el origen", ShortcutGroups.Edit, [","]) { ShortLabel = "Insertar", Description = "Pone el tramo marcado del origen en la secuencia y corre lo que sigue; con un borde elegido, lo recorta un fotograma a la izquierda." });
        registry.Register(new ShortcutAction(OverwriteId, "Sobrescribir desde el origen", ShortcutGroups.Edit, ["."]) { ShortLabel = "Sobrescribir", Description = "Pone el tramo marcado del origen encima de lo que haya; con un borde elegido, lo recorta un fotograma a la derecha." });
        registry.Register(new ShortcutAction(ToggleSourceId, "Visor de origen o de programa", ShortcutGroups.View, ["Mayús+2"]) { ShortLabel = "Origen", Description = "Cambia el reproductor entre el medio del origen y la secuencia." });

        ShortcutRouter router = services.Shortcuts;
        router.Bind(MarkInId, () => Session!.MarkIn());
        router.Bind(MarkOutId, () => Session!.MarkOut());
        router.Bind(ClearMarksId, () => Session!.ClearMarks());
        // Con un borde elegido, «,» y «.» lo recortan un cuadro; si no, insertan y sobrescriben desde el origen.
        router.Bind(InsertId, () => { if (!Session!.TrimSelectedEdge(-1)) { UiTask.Run(ThreePointAsync(PlacementMode.Insert)); } }, () => Session?.SourceMediaId is not null || Session?.SelectedEdge is not null);
        router.Bind(OverwriteId, () => { if (!Session!.TrimSelectedEdge(1)) { UiTask.Run(ThreePointAsync(PlacementMode.Overwrite)); } }, () => Session?.SourceMediaId is not null || Session?.SelectedEdge is not null);
        router.Bind(ToggleSourceId, () => Session!.ToggleSourceProgram(), () => Session?.SourceMediaId is not null);
        Session!.SourceChanged += (_, _) =>
        {
            RefreshContextBar();
            Invalidate();
        };
    }

    /// <summary>Mover el cursor desde la línea de tiempo vuelve al programa.</summary>
    private void SeekProgram(Time position)
    {
        Session!.ShowProgram();
        Session.Seek(position);
    }

    private async Task ThreePointAsync(PlacementMode mode)
    {
        EditorSession session = Session!;
        if (session.ThreePointEdit(mode) != ThreePointOutcome.NeedsDecision)
        {
            return;
        }

        FourPointFit? fit = await AskFourPointAsync(session);
        if (fit is { } chosen)
        {
            session.ThreePointEdit(mode, chosen);
        }
    }

    private async Task<FourPointFit?> AskFourPointAsync(EditorSession session)
    {
        if (XamlRoot is null)
        {
            return null;
        }

        Time source = session.SourceOut!.Value - session.SourceIn!.Value;
        Time target = session.SequenceOut!.Value - session.SequenceIn!.Value;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Cuatro puntos",
            Content = new TextBlock
            {
                Text = $"El tramo del origen dura {Timecode.Format(source)} y el de la secuencia {Timecode.Format(target)}. " +
                    "Ajustar la velocidad hace que el origen entero llene el tramo; recortar usa el origen desde su entrada.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Ajustar velocidad",
            SecondaryButtonText = "Recortar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await DialogGate.ShowAsync(dialog) switch
        {
            ContentDialogResult.Primary => FourPointFit.FitToFill,
            ContentDialogResult.Secondary => FourPointFit.Trim,
            _ => null,
        };
    }

    /// <summary>El origen (nombre, marcas, selectores V/A y destino) y las marcas de la secuencia.</summary>
    private void RefreshSourcePanel()
    {
        EditorSession session = Session!;
        bool marks = session.SequenceIn is not null || session.SequenceOut is not null;
        _overlay.TimeRange = session.SequenceIn is { } i && session.SequenceOut is { } o ? (i, o) : _overlay.TimeRange;
        if (session.SourceMedia is null && !marks)
        {
            SourcePanel.Visibility = Visibility.Collapsed;
            return;
        }

        _fillingSourcePanel = true;
        SourcePanel.Children.Clear();
        SourcePanel.Visibility = Visibility.Visible;
        if (session.SourceMedia is { } source)
        {
            var toggle = new ToggleButton
            {
                Content = session.IsSourceActive ? "Viendo el origen" : "Ver el origen",
                IsChecked = session.IsSourceActive,
                Padding = new Thickness(8, 2, 8, 2),
                MinHeight = 0,
            };
            toggle.Click += (_, _) => Session?.ToggleSourceProgram();
            SourcePanel.Children.Add(toggle);
            SourcePanel.Children.Add(Caption($"{source.Name} · E {Mark(session.SourceIn)} · S {Mark(session.SourceOut)}"));
            AddTrackSelector("V", session.SourceVideoEnabled && source.HasVideo, source.HasVideo, session.Sequence.VideoTracks,
                session.TargetVideoTrackId ?? session.MainTrack?.Id, isVideo: true);
            AddTrackSelector("A", session.SourceAudioEnabled && source.HasAudio, source.HasAudio, session.Sequence.AudioTracks,
                session.TargetAudioTrackId ?? session.Sequence.AudioTracks.FirstOrDefault()?.Id, isVideo: false);
            SourcePanel.Children.Add(ActionButton("Insertar (,)", () => UiTask.Run(ThreePointAsync(PlacementMode.Insert))));
            SourcePanel.Children.Add(ActionButton("Sobrescribir (.)", () => UiTask.Run(ThreePointAsync(PlacementMode.Overwrite))));
            SourcePanel.Children.Add(ActionButton("Reemplazar", () => Session?.SourceAction(ThreePointAction.Replace)));
            SourcePanel.Children.Add(ActionButton("Ajustar a rellenar", () => Session?.SourceAction(ThreePointAction.FitToFill)));
            SourcePanel.Children.Add(ActionButton("Superponer", () => Session?.SourceAction(ThreePointAction.Superimpose)));
        }

        if (marks)
        {
            SourcePanel.Children.Add(Caption($"Secuencia · E {Mark(session.SequenceIn)} · S {Mark(session.SequenceOut)}"));
        }

        _fillingSourcePanel = false;
    }

    private void AddTrackSelector(string label, bool enabled, bool available, List<Track> tracks, string? target, bool isVideo)
    {
        var toggle = new ToggleButton { Content = label, IsChecked = enabled, IsEnabled = available, Padding = new Thickness(8, 2, 8, 2), MinHeight = 0 };
        ToolTipService.SetToolTip(toggle, isVideo ? "Selector de origen: el video entra en la edición" : "Selector de origen: el audio entra en la edición");
        var box = new ComboBox { MinWidth = 110, Height = 26, MinHeight = 0, Padding = new Thickness(6, 0, 0, 0), IsEnabled = available };
        foreach (Track track in tracks)
        {
            box.Items.Add(new ComboBoxItem { Content = track.Name, Tag = track.Id });
        }

        box.SelectedIndex = tracks.FindIndex(t => t.Id == target);
        toggle.Click += (_, _) => UpdateSourceTracks(isVideo, toggle.IsChecked == true, null);
        box.SelectionChanged += (_, _) =>
        {
            if (!_fillingSourcePanel && box.SelectedItem is ComboBoxItem { Tag: string id })
            {
                UpdateSourceTracks(isVideo, toggle.IsChecked == true, id);
            }
        };
        SourcePanel.Children.Add(toggle);
        SourcePanel.Children.Add(box);
    }

    private void UpdateSourceTracks(bool isVideo, bool enabled, string? trackId)
    {
        EditorSession session = Session!;
        session.SetSourceTracks(
            isVideo ? enabled : session.SourceAudioEnabled,
            isVideo ? session.SourceVideoEnabled : enabled,
            isVideo ? trackId ?? session.TargetVideoTrackId : session.TargetVideoTrackId,
            isVideo ? session.TargetAudioTrackId : trackId ?? session.TargetAudioTrackId);
    }

    private static TextBlock Caption(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    private static Button ActionButton(string text, Action click)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), MinHeight = 0 };
        button.Click += (_, _) => click();
        return button;
    }

    private static string Mark(Time? time) => time is { } t ? Timecode.Format(t) : "—";
}
