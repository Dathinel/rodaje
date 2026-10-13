using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Media;
using Rodaje.App.Shell;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Shared;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Mientras se arrastra un roll o un slide, el visor muestra lado a lado los dos cuadros del corte (las miniaturas del
/// medio, sin efectos) con su rótulo; al soltar vuelve la imagen del programa.
/// </summary>
internal sealed partial class PlayerView
{
    private const int TrimPreviewHeight = 360;

    private void AttachTrimPreview(EditorServices services)
    {
        services.Session.TrimPreviewChanged += (_, _) => ShowTrimPreview();
        // Las miniaturas llegan en ráfagas: un solo aviso pendiente a la vez.
        var thumbnails = new CoalescedDispatch(DispatcherQueue, ShowTrimPreview);
        services.Visuals.Changed += (_, e) =>
        {
            if (e.Kind == ClipVisualKind.Thumbnails && Session?.CurrentTrimPreview is { } preview
                && (preview.Left.MediaId == e.MediaId || preview.Right.MediaId == e.MediaId))
            {
                thumbnails.Request();
            }
        };
    }

    private void ShowTrimPreview()
    {
        if (Session?.CurrentTrimPreview is not { } preview || _services is not { } services)
        {
            TrimPreviewHost.Visibility = Visibility.Collapsed;
            TrimLeftImage.Source = null;
            TrimRightImage.Source = null;
            return;
        }

        TrimPreviewHost.Visibility = Visibility.Visible;
        Show(services.Visuals, preview.Left, TrimLeftImage, TrimLeftCaption);
        Show(services.Visuals, preview.Right, TrimRightImage, TrimRightCaption);
    }

    private static void Show(IClipVisuals visuals, TrimPreviewFrame frame, Image image, TextBlock caption)
    {
        caption.Text = frame.Caption;
        if (frame.MediaId is not { } media)
        {
            image.Source = null;
            return;
        }

        // Mientras llega la miniatura exacta queda la anterior: el corte se sigue viendo al arrastrar rápido.
        if (visuals.GetThumbnail(media, frame.SourceTime, TrimPreviewHeight) is { } thumbnail)
        {
            image.Source = BgraBitmaps.From(thumbnail);
        }
    }
}
