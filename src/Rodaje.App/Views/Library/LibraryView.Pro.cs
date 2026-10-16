using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Shell;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Shared;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Video;

namespace Rodaje.App.Views.Library;

/// <summary>
/// La biblioteca profesional: valoración, marcas y etiquetas; filtros y colecciones inteligentes; metadatos (códec,
/// fecha, cámara) buscables; subclips; recorrido de la portada con el ratón; avisos de medio sin conexión o repetido; y
/// multicámaras sincronizadas por sonido. Las reglas están en Logic/ (sin WinUI, con pruebas).
/// </summary>
internal sealed partial class LibraryView
{
    private const int SkimHeight = 96;

    private int _minRating;
    private MediaMark? _mark;
    private string? _tag;
    private bool _hideRejected;
    private string? _collectionId;
    private HashSet<string> _offline = new(StringComparer.Ordinal);
    private Dictionary<string, string> _duplicates = new(StringComparer.Ordinal);
    private string _healthSignature = "";
    private int _healthScan;
    private MediaItem? _skimItem;
    private Time _skimTime;
    private DispatcherQueueTimer? _refilter;

    private readonly MenuFlyoutSubItem _ratingItem = new() { Text = "Valoración", Icon = new FontIcon { Glyph = "" } };
    private readonly MenuFlyoutItem _rejectItem = new() { Text = "Rechazar", Icon = new FontIcon { Glyph = "" } };
    private readonly MenuFlyoutItem _tagsItem = new() { Text = "Etiquetas…", Icon = new FontIcon { Glyph = "" } };
    private readonly MenuFlyoutItem _subclipItem = new() { Text = "Crear subclip con las marcas del origen", Icon = new FontIcon { Glyph = "" } };
    private readonly MenuFlyoutItem _removeSubclipItem = new() { Text = "Quitar el subclip", Icon = new FontIcon { Glyph = "" } };
    private readonly MenuFlyoutItem _relinkItem = new() { Text = "Volver a enlazar…", Icon = new FontIcon { Glyph = "" } };
    private readonly MenuFlyoutItem _multicamItem = new() { Text = "Crear multicámara con la selección", Icon = new FontIcon { Glyph = "" } };

    private LibraryQuery Query() => new(_folderId, SearchBox.Text ?? "", _kind, FavoritesToggle.IsChecked == true, _minRating, _mark, _tag, _hideRejected, _collectionId);

    /// <summary>Agrega al menú del medio lo profesional; se llama una vez desde el constructor.</summary>
    private void AddProMenuItems()
    {
        for (int stars = 0; stars <= LibraryOrganization.MaxRating; stars++)
        {
            int value = stars;
            var item = new RadioMenuFlyoutItem { Text = stars == 0 ? "Sin valorar" : new string('★', stars), GroupName = "valoracion", Tag = value };
            item.Click += (_, _) => ForTargets(id => _organization.SetRating(id, value));
            _ratingItem.Items.Add(item);
        }

        _rejectItem.Click += (_, _) =>
        {
            bool reject = _menuTarget is { } target && _organization.MarkOf(target.Id) != MediaMark.Rejected;
            ForTargets(id => _organization.SetMark(id, reject ? MediaMark.Rejected : MediaMark.None));
        };
        _tagsItem.Click += (_, _) => UiTask.Run(EditTagsAsync());
        _subclipItem.Click += (_, _) => UiTask.Run(CreateSubclipAsync());
        _removeSubclipItem.Click += (_, _) =>
        {
            if (_menuTarget?.Subclip is { } subclip && _organization.RemoveSubclip(subclip.Id))
            {
                OrganizationChanged();
            }
        };
        _relinkItem.Click += (_, _) => UiTask.Run(RelinkTargetAsync());
        _multicamItem.Click += (_, _) => UiTask.Run(CreateMulticamAsync());
        _mediaMenu.Items.Insert(_mediaMenu.Items.IndexOf(_moveItem), _ratingItem);
        _mediaMenu.Items.Insert(_mediaMenu.Items.IndexOf(_moveItem), _rejectItem);
        _mediaMenu.Items.Insert(_mediaMenu.Items.IndexOf(_moveItem), _tagsItem);
        _mediaMenu.Items.Add(new MenuFlyoutSeparator());
        _mediaMenu.Items.Add(_subclipItem);
        _mediaMenu.Items.Add(_removeSubclipItem);
        _mediaMenu.Items.Add(_multicamItem);
        _mediaMenu.Items.Add(_relinkItem);
    }

    /// <summary>Pone al día lo profesional del menú para el medio pulsado.</summary>
    private void UpdateProMenu(MediaItem item)
    {
        int rating = _organization.RatingOf(item.Id);
        foreach (RadioMenuFlyoutItem radio in _ratingItem.Items.OfType<RadioMenuFlyoutItem>())
        {
            radio.IsChecked = radio.Tag is int value && value == rating;
        }

        _rejectItem.Text = _organization.MarkOf(item.Id) == MediaMark.Rejected ? "Quitar el rechazo" : "Rechazar";
        EditorSession? session = _services?.Session;
        _subclipItem.IsEnabled = session?.SourceMediaId == item.Id && session.SourceIn is not null && session.SourceOut is not null;
        _subclipItem.Visibility = item.IsSubclip ? Visibility.Collapsed : Visibility.Visible;
        _removeSubclipItem.Visibility = item.IsSubclip ? Visibility.Visible : Visibility.Collapsed;
        _relinkItem.Visibility = item.IsOffline ? Visibility.Visible : Visibility.Collapsed;
        _multicamItem.IsEnabled = SelectedMedia().Count(m => m.HasVideo) >= 2;
    }

    /// <summary>Los medios elegidos en la vista que se ve (sin subclips, sin repetir).</summary>
    private List<MediaItem> SelectedMedia()
    {
        ListViewBase view = DetailsToggle.IsChecked == true ? MediaList : MediaGrid;
        return [.. view.SelectedItems.OfType<MediaItem>().Where(i => !i.IsSubclip).DistinctBy(i => i.Id)];
    }

    /// <summary>Aplica a los medios elegidos (o al del menú, si no está entre ellos) y guarda.</summary>
    private void ForTargets(Action<string> change)
    {
        List<string> ids = [.. SelectedMedia().Select(i => i.Id)];
        if (_menuTarget is { } target && !ids.Contains(target.Id))
        {
            ids = [target.Id];
        }

        foreach (string id in ids)
        {
            change(id);
        }

        OrganizationChanged();
    }

    private void OrganizationChanged()
    {
        SaveOrganization();
        RefreshMarks();
        ApplyFilter();
    }

    /// <summary>Valoración, marca, etiquetas y avisos en cada ficha.</summary>
    private void RefreshMarks()
    {
        foreach (MediaItem item in _allMedia.Concat(_subclipItems.Values))
        {
            item.IsFavorite = _organization.IsFavorite(item.Id);
            item.SetMarks(_organization.RatingOf(item.Id), _organization.MarkOf(item.Id), _organization.TagsOf(item.Id), _offline.Contains(item.Id),
                _duplicates.GetValueOrDefault(item.Id, ""));
        }

        if (_services is not null)
        {
            _services.Session.Multicams = [.. _organization.Multicams.Select(g => (IReadOnlyList<MulticamAngle>)[.. g.Angles.Select(a => new MulticamAngle(a.MediaId, a.Offset))])];
        }
    }

    private readonly Dictionary<string, MediaItem> _subclipItems = new(StringComparer.Ordinal);

    /// <summary>La ficha de un subclip (se reutiliza mientras el subclip no cambie).</summary>
    private MediaItem SubclipItem(Subclip subclip, MediaRef media)
    {
        if (!_subclipItems.TryGetValue(subclip.Id, out MediaItem? item) || item.Subclip != subclip)
        {
            item = new MediaItem(media, isUsed: true, subclip);
            _subclipItems[subclip.Id] = item;
        }

        return item;
    }

    /// <summary>Lo que se sabe de cada medio para filtrar: códecs, fecha y cámara leídos, sin conexión y si se usa.</summary>
    private List<MediaFacts> Facts(Project project)
    {
        var facts = new List<MediaFacts>(project.Media.Count);
        foreach (MediaRef media in project.Media)
        {
            _itemsById.TryGetValue(media.Id, out MediaItem? item);
            string? codecs = item is { CodecsKnown: true, CodecsFailed: false } ? item.Codecs : null;
            facts.Add(new MediaFacts(media, _organization, codecs, item?.Camera, item?.Created, _offline.Contains(media.Id), item?.IsUsed ?? false));
        }

        return facts;
    }

    /// <summary>Las fichas que se ven: los medios filtrados y, tras cada uno, sus subclips.</summary>
    private List<MediaItem> VisibleItems(Project project, LibraryQuery query)
    {
        IReadOnlyList<MediaFacts> shown = LibraryFilter.Apply(Facts(project), _organization, query, DateTimeOffset.Now);
        // Los subclips se agrupan por medio una vez por lista, no se filtran por cada medio visible.
        List<MediaItem> items = SubclipsByMedia.Interleave(shown, facts => facts.Media.Id, _organization.Subclips,
            facts => _itemsById.GetValueOrDefault(facts.Media.Id), (subclip, facts) => SubclipItem(subclip, facts.Media));

        foreach (string stale in _subclipItems.Keys.Where(id => _organization.FindSubclip(id) is null).ToList())
        {
            _subclipItems.Remove(stale);
        }

        return items;
    }

    /// <summary>
    /// El botón de filtros: tipo, valoración, marca, etiqueta, esconder rechazados y colecciones inteligentes. Se arma al
    /// abrirlo, con las etiquetas y colecciones del momento.
    /// </summary>
    private MenuFlyout FilterFlyout()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => FillFilterMenu(menu);
        return menu;
    }

    private void FillFilterMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        var kinds = new MenuFlyoutSubItem { Text = "Tipo" };
        foreach ((LibraryKind kind, string label) in LibraryFilter.Kinds)
        {
            kinds.Items.Add(Radio(label, "tipo", kind == _kind, () => _kind = kind));
        }

        var ratings = new MenuFlyoutSubItem { Text = "Valoración" };
        for (int stars = 0; stars <= LibraryOrganization.MaxRating; stars++)
        {
            int value = stars;
            ratings.Items.Add(Radio(stars == 0 ? "Todas" : $"{new string('★', stars)} o más", "estrellas", _minRating == stars, () => _minRating = value));
        }

        var marks = new MenuFlyoutSubItem { Text = "Marca" };
        marks.Items.Add(Radio("Todas", "marca", _mark is null, () => _mark = null));
        marks.Items.Add(Radio("Favoritos", "marca", _mark == MediaMark.Favorite, () => _mark = MediaMark.Favorite));
        marks.Items.Add(Radio("Rechazados", "marca", _mark == MediaMark.Rejected, () => _mark = MediaMark.Rejected));
        marks.Items.Add(Radio("Sin marca", "marca", _mark == MediaMark.None, () => _mark = MediaMark.None));

        var tags = new MenuFlyoutSubItem { Text = "Etiqueta", IsEnabled = _organization.AllTags().Count > 0 };
        tags.Items.Add(Radio("Todas", "etiqueta", _tag is null, () => _tag = null));
        foreach (string tag in _organization.AllTags())
        {
            tags.Items.Add(Radio(tag, "etiqueta", string.Equals(_tag, tag, StringComparison.Ordinal), () => _tag = tag));
        }

        var hide = new ToggleMenuFlyoutItem { Text = "Esconder rechazados", IsChecked = _hideRejected };
        hide.Click += (_, _) =>
        {
            _hideRejected = hide.IsChecked;
            FiltersChanged();
        };

        var collections = new MenuFlyoutSubItem { Text = "Colecciones inteligentes" };
        collections.Items.Add(Radio("Ninguna", "coleccion", _collectionId is null, () => _collectionId = null));
        foreach (SmartBin bin in _organization.Collections)
        {
            var radio = Radio(bin.Name, "coleccion", bin.Id == _collectionId, () => _collectionId = bin.Id);
            ToolTipService.SetToolTip(radio, string.Join(bin.MatchAll ? " y " : " o ", bin.Rules.Select(r => r.Describe())));
            collections.Items.Add(radio);
        }

        collections.Items.Add(new MenuFlyoutSeparator());
        var save = new MenuFlyoutItem { Text = "Guardar estos filtros como colección…", IsEnabled = Query().HasFilters || !string.IsNullOrWhiteSpace(SearchBox.Text) };
        save.Click += (_, _) => UiTask.Run(SaveCollectionAsync());
        collections.Items.Add(save);
        if (_organization.FindCollection(_collectionId) is { } open)
        {
            var delete = new MenuFlyoutItem { Text = $"Borrar «{open.Name}»" };
            delete.Click += (_, _) =>
            {
                _organization.RemoveCollection(open.Id);
                _collectionId = null;
                OrganizationChanged();
            };
            collections.Items.Add(delete);
        }

        var clear = new MenuFlyoutItem { Text = "Quitar todos los filtros", IsEnabled = Query().HasFilters || _collectionId is not null };
        clear.Click += (_, _) =>
        {
            (_kind, _minRating, _mark, _tag, _hideRejected, _collectionId) = (LibraryKind.All, 0, null, null, false, null);
            FiltersChanged();
        };

        menu.Items.Add(kinds);
        menu.Items.Add(ratings);
        menu.Items.Add(marks);
        menu.Items.Add(tags);
        menu.Items.Add(hide);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(collections);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(clear);
    }

    private RadioMenuFlyoutItem Radio(string text, string group, bool isChecked, Action select)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = group, IsChecked = isChecked };
        item.Click += (_, _) =>
        {
            select();
            FiltersChanged();
        };
        return item;
    }

    private void FiltersChanged()
    {
        LibraryQuery query = Query();
        string? collection = _organization.FindCollection(_collectionId)?.Name;
        var parts = new List<string>();
        if (collection is not null)
        {
            parts.Add($"colección «{collection}»");
        }

        if (query.HasFilters)
        {
            parts.Add("filtros activos");
        }

        ToolTipService.SetToolTip(KindButton, parts.Count == 0 ? "Filtros y colecciones inteligentes" : "Filtros: " + string.Join(", ", parts));
        KindButton.Foreground = parts.Count == 0 ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextBrush"] : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentBrush"];
        ApplyFilter();
    }

    /// <summary>Guarda los filtros y la búsqueda actuales como una colección inteligente con nombre.</summary>
    private async Task SaveCollectionAsync()
    {
        string? name = await AskTextAsync("Colección inteligente nueva", "Nombre", "Colección", "Guardar");
        if (name is null)
        {
            return;
        }

        var rules = new List<SmartRule>();
        LibraryQuery query = Query();
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            rules.Add(new SmartRule(SmartField.Text, query.Text.Trim()));
        }

        if (query.Kind != LibraryKind.All)
        {
            rules.Add(new SmartRule(SmartField.Kind, query.Kind.ToString()));
        }

        if (query.MinRating > 0)
        {
            rules.Add(new SmartRule(SmartField.MinRating, query.MinRating.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (query.Mark is { } mark)
        {
            rules.Add(new SmartRule(SmartField.Mark, mark.ToString()));
        }

        if (query.Tag is { } tag)
        {
            rules.Add(new SmartRule(SmartField.Tag, tag));
        }

        SmartBin saved = _organization.SaveCollection(new SmartBin("", name, rules));
        (_kind, _minRating, _mark, _tag, _collectionId) = (LibraryKind.All, 0, null, null, saved.Id);
        SearchBox.Text = "";
        OrganizationChanged();
        FiltersChanged();
    }

    private async Task EditTagsAsync()
    {
        if (_menuTarget is not { } item)
        {
            return;
        }

        string? text = await AskTextAsync("Etiquetas", "Separadas por comas (por ejemplo: boda, exteriores)", string.Join(", ", _organization.TagsOf(item.Id)), "Guardar");
        if (text is not null)
        {
            IReadOnlyList<string> tags = LibraryOrganization.ParseTags(text);
            ForTargets(id => _organization.SetTags(id, tags));
        }
    }

    /// <summary>Un subclip con la entrada y la salida marcadas en el visor de origen.</summary>
    private async Task CreateSubclipAsync()
    {
        if (_menuTarget is not { } item || _services?.Session is not { SourceIn: { } a, SourceOut: { } b } session || session.SourceMediaId != item.Id)
        {
            return;
        }

        string? name = await AskTextAsync("Subclip nuevo", "Nombre", $"{item.Name} ({Rodaje.Engine.Presentation.Timecode.Format(a)})", "Crear");
        if (name is not null && b > a)
        {
            _organization.AddSubclip(item.Id, name, a, b);
            OrganizationChanged();
        }
    }

    private async Task<string?> AskTextAsync(string title, string label, string initial, string accept)
    {
        if (XamlRoot is null || DialogGate.IsOpen)
        {
            return null;
        }

        var box = new TextBox { Header = label, Text = initial, MinWidth = 320 };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = accept,
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        return await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary ? box.Text : null;
    }

    /// <summary>Abre el diálogo de volver a enlazar con los medios sin conexión.</summary>
    private async Task RelinkTargetAsync()
    {
        if (_services is not null)
        {
            await Relink.RelinkDialog.ReopenAsync();
        }
    }

    /// <summary>
    /// Revisa en segundo plano qué medios no tienen su archivo y cuáles están repetidos (misma ruta o mismo contenido);
    /// solo cuando cambia la lista de archivos.
    /// </summary>
    private void StartHealthScan(Project project)
    {
        if (_dispatcher is not { } dispatcher)
        {
            return;
        }

        List<MediaFile> files = [.. project.Media.Select(m => new MediaFile(m.Id, m.Path))];
        string signature = string.Join("|", files.Select(f => f.MediaId + "=" + f.Path));
        if (signature == _healthSignature)
        {
            return;
        }

        _healthSignature = signature;
        int scan = Interlocked.Increment(ref _healthScan);
        var names = project.Media.ToDictionary(m => m.Id, m => m.Name, StringComparer.Ordinal);
        UiTask.Run(Task.Run(() =>
        {
            HashSet<string> offline = [.. MediaDuplicates.Offline(files)];
            IReadOnlyList<IReadOnlyList<string>> groups = MediaDuplicates.Find(files.Where(f => !offline.Contains(f.MediaId)));
            var duplicates = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (IReadOnlyList<string> group in groups)
            {
                foreach (string id in group)
                {
                    duplicates[id] = string.Join(", ", group.Where(o => o != id).Select(o => $"«{names.GetValueOrDefault(o, o)}»"));
                }
            }

            dispatcher.RunLater(DispatcherQueuePriority.Low, () =>
            {
                if (Volatile.Read(ref _healthScan) != scan)
                {
                    return;
                }

                _offline = offline;
                _duplicates = duplicates;
                RefreshMarks();
            });
        }));
    }

    /// <summary>Después de leer metadatos nuevos, se vuelve a filtrar si la búsqueda o una colección los usan.</summary>
    private void ScheduleRefilter()
    {
        if (_dispatcher is null || (string.IsNullOrWhiteSpace(SearchBox.Text) && _collectionId is null))
        {
            return;
        }

        _refilter ??= _dispatcher.CreateTimer();
        _refilter.Interval = TimeSpan.FromMilliseconds(400);
        _refilter.IsRepeating = false;
        _refilter.Tick -= OnRefilterTick;
        _refilter.Tick += OnRefilterTick;
        _refilter.Start();
    }

    private void OnRefilterTick(DispatcherQueueTimer sender, object args) => ApplyFilter();

    /// <summary>Recorrido de la portada: pasar el ratón muestra el cuadro de esa parte del medio.</summary>
    private void OnTilePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_services is null || sender is not FrameworkElement { DataContext: MediaItem { HasVideo: true } item } tile || tile.ActualWidth <= 0)
        {
            return;
        }

        double fraction = Math.Clamp(e.GetCurrentPoint(tile).Position.X / tile.ActualWidth, 0, 0.999);
        Time at = item.Offset + Time.FromTicks((long)(item.Duration.Ticks * fraction));
        if (item.Duration.Ticks > 0)
        {
            // Pasos de un cuarto de segundo: el ratón no pide una miniatura por píxel.
            long step = Time.FromSeconds(0.25).Ticks;
            at = Time.FromTicks(at.Ticks / step * step);
        }

        if (ReferenceEquals(_skimItem, item) && _skimTime == at)
        {
            return;
        }

        if (_skimItem is { } previous && !ReferenceEquals(previous, item))
        {
            previous.Skim(null);
        }

        _skimItem = item;
        _skimTime = at;
        if (_services.Visuals.GetThumbnail(item.Id, at, SkimHeight) is { } image)
        {
            item.Skim(BgraBitmaps.From(image));
        }
    }

    private void OnTilePointerExited(object sender, PointerRoutedEventArgs e)
    {
        _skimItem?.Skim(null);
        _skimItem = null;
    }

    /// <summary>Una miniatura pedida al recorrer llegó: si el ratón sigue ahí, se muestra.</summary>
    private void OnSkimThumbnailReady(string mediaId)
    {
        if (_services is not null && _skimItem is { } item && item.Id == mediaId
            && _services.Visuals.GetThumbnail(item.Id, _skimTime, SkimHeight) is { } image)
        {
            item.Skim(BgraBitmaps.From(image));
        }
    }

    /// <summary>
    /// Sincroniza por sonido los medios elegidos (el primero es la referencia), guarda la multicámara en la biblioteca
    /// y pone la toma de referencia en el cursor de la pista principal: 1 a 9 cortan a cada ángulo.
    /// </summary>
    private async Task CreateMulticamAsync()
    {
        if (_services is null)
        {
            return;
        }

        List<MediaItem> angles = [.. SelectedMedia().Where(m => m.HasVideo)];
        if (angles.Count < 2)
        {
            return;
        }

        EditorSession session = _services.Session;
        List<(string MediaId, string Path, bool HasAudio)> inputs = [.. angles.Select(a => (a.Id, a.Path, session.Project.FindMedia(a.Id)?.HasAudio == true))];
        IReadOnlyList<MulticamSync.AngleResult> results = [];
        bool ran = await _services.LongActions.RunAsync("multicam.sync", "Sincronización multicámara", async scope =>
        {
            results = await Task.Run(() => MulticamSync.Run(inputs, progress: (done, total) =>
                scope.Report((double)done / total, $"{done} de {total} pasos")));
        });
        if (!ran)
        {
            return;
        }

        List<MulticamAngle> synced = [.. results.Where(r => r.Offset is not null).Select(r => new MulticamAngle(r.MediaId, r.Offset!.Value))];
        List<string> failed = [.. results.Where(r => r.Offset is null).Select(r => angles.First(a => a.Id == r.MediaId).Name)];
        if (synced.Count < 2)
        {
            session.Notify(NotificationSeverity.Warning, "No se pudo sincronizar", "Los ángulos no comparten sonido suficiente para alinearlos.");
            return;
        }

        MulticamGroup group = _organization.AddMulticam($"Multicámara {_organization.Multicams.Count + 1}", [.. synced.Select(a => new MulticamAngleEntry(a.MediaId, a.Offset))]);
        OrganizationChanged();
        session.AppendToTimeline(synced[0].MediaId);
        string detail = string.Join(", ", synced.Select((a, i) => $"{i + 1}: «{angles.First(m => m.Id == a.MediaId).Name}» {Rodaje.App.Services.Titles.ClipLabels.FrameShift(a.Offset, session.Sequence.FrameRate)}"));
        session.Notify(NotificationSeverity.Success, $"«{group.Name}» lista", $"Pulsa 1 a {synced.Count} sobre la toma para cortar a cada ángulo. {detail}"
            + (failed.Count > 0 ? $" Sin sonido común: {string.Join(", ", failed)}." : ""));
    }
}
