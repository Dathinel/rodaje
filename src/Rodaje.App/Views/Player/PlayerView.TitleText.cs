using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.Core.Model;
using Windows.Foundation;
using Windows.System;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Escribir sobre el visor: doble clic en un título abre un <c>TextBox</c> encima, con la fuente y el tamaño aproximados
/// (el IME, la selección y el portapapeles son los de Windows); lo escrito va al título como desde la pestaña Texto, una
/// tanda de tecleo por paso del historial. Esc, Ctrl+Intro o salir del cuadro lo cierran.
/// </summary>
internal sealed partial class PlayerView
{
    private TitleEditor? _inlineEditor;
    private string? _inlineClipId;
    private bool _inlineUpdating;

    /// <summary>Abre el cuadro de texto si <paramref name="position"/> cae sobre un título dibujado.</summary>
    private bool TryEditTitleAt(Point position)
    {
        if (Session is null || _engine is null || Image() is not { } frame)
        {
            return false;
        }

        IReadOnlyList<TitleBox> boxes = _engine.TitleBoxes;
        for (int i = boxes.Count - 1; i >= 0; i--)
        {
            if (TitleHandles.Corners(boxes[i], Session.Sequence.Width, Session.Sequence.Height, frame) is { } corners
                && TitleHandles.Hit(corners, position.X, position.Y, withCorners: false) == TitleGrip.Body)
            {
                return BeginInlineEdit(boxes[i], corners, frame);
            }
        }

        return false;
    }

    private bool BeginInlineEdit(TitleBox box, Vector2[] corners, ImageFrame frame)
    {
        if (Session!.Sequence.FindClip(box.ClipId) is not { Clip.Generator: { } generator })
        {
            return false;
        }

        Session.Select([box.ClipId]);
        _inlineEditor ??= new TitleEditor(Session);
        _inlineClipId = box.ClipId;
        TextStyle style = generator.Style ?? TitleStyles.Default();
        (double left, double top, double width, double height) = TitleHandles.Bounds(corners);
        double unit = Math.Min((double)Session.Sequence.Width / style.Reference.Width, (double)Session.Sequence.Height / style.Reference.Height);
        TitleEditBox.FontFamily = new FontFamily(style.Font.Family);
        TitleEditBox.FontSize = Math.Clamp(style.Size * unit * box.Transform.Scale * frame.Scale, 8, 300);
        TitleEditBox.FontWeight = new Windows.UI.Text.FontWeight((ushort)style.Font.Weight);
        TitleEditBox.FontStyle = style.Font.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        TitleEditBox.TextAlignment = style.Align switch
        {
            TextAlign.Left => TextAlignment.Left,
            TextAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Center,
        };
        TitleEditBox.FlowDirection = TitleHandles.IsRightToLeft(generator.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        TitleEditBox.MaxLength = ModelValidator.MaxTextLength;
        TitleEditBox.Width = Math.Max(width, 160);
        TitleEditBox.MinHeight = Math.Max(height, 32);
        _inlineUpdating = true;
        TitleEditBox.Text = generator.Text;
        _inlineUpdating = false;
        Canvas.SetLeft(TitleEditBox, left - ((TitleEditBox.Width - width) / 2));
        Canvas.SetTop(TitleEditBox, top);
        TitleEditCanvas.Visibility = Visibility.Visible;
        TitleEditBox.Focus(FocusState.Programmatic);
        TitleEditBox.SelectAll();
        return true;
    }

    private void OnTitleEditChanged(object sender, TextChangedEventArgs e)
    {
        if (!_inlineUpdating && _inlineEditor is { } editor && Session?.SelectedTitle?.Id == _inlineClipId)
        {
            editor.SetText(TitleEditor.FromTextBox(TitleEditBox.Text));
        }
    }

    private void OnTitleEditLostFocus(object sender, RoutedEventArgs e) => EndInlineEdit();

    private void OnTitleEditKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape || (e.Key == VirtualKey.Enter && control))
        {
            e.Handled = true;
            EndInlineEdit();
        }
    }

    private void EndInlineEdit()
    {
        if (_inlineClipId is null)
        {
            return;
        }

        _inlineClipId = null;
        _inlineEditor?.EndTyping();
        TitleEditCanvas.Visibility = Visibility.Collapsed;
    }
}
