using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.MotionTemplates;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Titles;
using Rodaje.App.Shell;
using Rodaje.App.Views.Inspector;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Views.Library;

/// <summary>
/// Categoría «Plantillas» (D-064): las plantillas de movimiento instaladas y las propias. Cada una de títulos lleva su
/// miniatura animada, compuesta de fondo la primera vez y guardada en la caché; quieta enseña el cuadro del medio y se
/// anima con el puntero encima, con el mismo reloj que las transiciones.
/// </summary>
internal sealed partial class LibraryView
{
    private static readonly Lock TemplateRenderGate = new();
    private CancellationTokenSource? _templateThumbs;

    private void LoadTemplatePreviews()
    {
        _templateThumbs?.Cancel();
        _templateThumbs?.Dispose();
        _templateThumbs = null;
        if (_services is not { } services || _category != LibraryCategory.MotionTemplates || Elements.Count + OwnElements.Count == 0)
        {
            return;
        }

        var cancel = new CancellationTokenSource();
        _templateThumbs = cancel;
        var items = Elements.Concat(OwnElements).ToDictionary(i => i.Element.Reference, StringComparer.Ordinal);
        PackageScan installed = services.Packages.Installed;
        DispatcherQueue dispatcher = DispatcherQueue;
        string folder = TemplateThumbnails.CacheFolder;
        CancellationToken token = cancel.Token;
        void Show(MotionTemplateEntry entry, TemplateStrip strip)
        {
            // La conversión a BGRA (cerca de 1 MB por plantilla) se hace aquí, en el hilo de fondo.
            var frames = new List<byte[]>(strip.Frames);
            for (int i = 0; i < strip.Frames; i++)
            {
                frames.Add(strip.FrameBgra(i));
            }

            dispatcher.RunLater(() =>
            {
                if (!token.IsCancellationRequested && items.TryGetValue(entry.Reference, out ElementItem? item))
                {
                    ShowTemplateStrip(item, entry, strip, frames);
                }
            });
        }

        var thread = new Thread(() =>
        {
            try
            {
                // Una composición a la vez: la que espera encuentra en la caché lo que la anterior ya guardó.
                lock (TemplateRenderGate)
                {
                    var pending = new List<MotionTemplateEntry>();
                    foreach (MotionTemplateEntry entry in MotionTemplateCatalog.List(installed).Where(e => e.Kind == TemplateKind.Title))
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        if (TemplateThumbnails.Load(folder, entry.DocumentText) is { } cached)
                        {
                            Show(entry, cached);
                        }
                        else
                        {
                            pending.Add(entry);
                        }
                    }

                    TemplateThumbnails.Render(pending, TitleFontFiles.Find(installed), (entry, strip) =>
                    {
                        Show(entry, strip);
                        try
                        {
                            TemplateThumbnails.Save(folder, entry.DocumentText, strip);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // Sin caché la miniatura se vuelve a componer la próxima vez.
                        }
                    }, token);
                }
            }
#pragma warning disable CA1031 // Sin GPU o sin shaders las plantillas se quedan con su ícono; nada más depende de la miniatura.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Rodaje: miniaturas de plantillas",
        };
        thread.Start();
    }

    private void ShowTemplateStrip(ElementItem item, MotionTemplateEntry entry, TemplateStrip strip, List<byte[]> frames)
    {
        var animation = new TransitionAnimation(strip.FrameWidth, strip.FrameHeight, frames, Math.Clamp(entry.Document.DurationSeconds, 1, 4));
        var bitmap = new WriteableBitmap(strip.FrameWidth, strip.FrameHeight);
        frames[frames.Count / 2].CopyTo(bitmap.PixelBuffer);
        bitmap.Invalidate();
        _transitionThumbs[item] = (animation, bitmap);
        item.Preview = bitmap;
    }

    private void AddTemplateItems(MenuFlyout menu, LibraryElement element)
    {
        menu.Items.Add(MenuItem("Usar en la línea de tiempo", "", (_, _) => UiTask.Run(ApplyElementAsync(element))));
        if (element.Origin == PackageOrigin.Own)
        {
            menu.Items.Add(MenuItem("Exportar…", "", (_, _) => UiTask.Run(ExportTemplateAsync(element))));
        }
    }

    /// <summary>«Exportar…»: la plantilla propia como <c>.paquete</c> de un elemento, para instalarla en otro equipo.</summary>
    private async Task ExportTemplateAsync(LibraryElement element)
    {
        if (_services is not { } services)
        {
            return;
        }

        PackageScan installed = services.Packages.Installed;
        if (await Task.Run(() => MotionTemplateCatalog.Find(installed, element.Reference)) is not { } entry)
        {
            services.Session.Notify(NotificationSeverity.Warning, $"No se pudo exportar «{element.Name}»", "La plantilla ya no está o no se puede leer.");
            return;
        }

        string? path = FileDialogs.Save(services.Player.WindowHandle, "Exportar plantilla", TemplateAuthoring.Identifier(entry.Name) + ".paquete", "paquete", null,
            ("Paquete de Rodaje", "*.paquete"));
        if (path is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => File.WriteAllBytes(path, TemplateAuthoring.Export(entry, Environment.UserName)));
            services.Session.Notify(NotificationSeverity.Success, "Plantilla exportada", $"«{entry.Name}» quedó en {path}. Se instala desde Complementos o soltándola en la biblioteca.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            services.Session.Notify(NotificationSeverity.Error, "No se pudo exportar la plantilla", ex.Message);
        }
    }
}
