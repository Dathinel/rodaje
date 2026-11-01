using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Settings;

// MediaRef.DefaultStillImageDuration es global: Normalize() lo toca, así que no corre a la vez que PreferencesViewModelTests.
[Collection("StillImageDefaults")]
public sealed class PersonalizationTests
{
    [Fact]
    public void Contraste_WcagYAjusteDelAcento()
    {
        Assert.Equal(21, Personalization.Contrast(Personalization.White, 0xFF000000), 1);
        // El naranja grafito: 7:1 sobre el fondo oscuro, 2,4:1 sobre el panel claro.
        Assert.InRange(Personalization.Contrast(0xFFE8833A, 0xFF101010), 6.5, 7.5);
        Assert.InRange(Personalization.Contrast(0xFFE8833A, 0xFFE2E2E2), 1.9, 2.3);

        (uint fitted, bool adjusted) = Personalization.EnsureContrast(0xFFE8833A, 0xFFE2E2E2, Personalization.UiContrast);
        Assert.True(adjusted);
        Assert.True(Personalization.Contrast(fitted, 0xFFE2E2E2) >= Personalization.UiContrast);
        Assert.Equal((0xFFE8833Au, false), Personalization.EnsureContrast(0xFFE8833A, 0xFF101010, Personalization.UiContrast));
        Assert.Equal(Personalization.Ink, Personalization.ForegroundOn(0xFFE8833A));
        Assert.Equal(Personalization.White, Personalization.ForegroundOn(0xFF1D1D1D));
    }

    [Fact]
    public void Colores_SeLeenYEscribenEnHex()
    {
        Assert.True(Personalization.TryParseColor("#E8833A", out uint opaque));
        Assert.Equal(0xFFE8833Au, opaque);
        Assert.True(Personalization.TryParseColor("4de8833a", out uint translucent));
        Assert.Equal("#4DE8833A", Personalization.ToHex(translucent));
        Assert.Equal("#E8833A", Personalization.ToHex(opaque));
        Assert.False(Personalization.TryParseColor("#GGG", out _));
        Assert.False(Personalization.TryParseColor("", out _));
        Assert.True(Personalization.IsAchromatic(0xFF1D1D1D));
        Assert.False(Personalization.IsAchromatic(0xFFE8833A));
        Assert.Equal(0xB3EFEFEFu, Personalization.Invert(0xB3101010));
    }

    [Fact]
    public void Ajustes_AcotanEscalaFuenteTemaYFormato()
    {
        var s = new AppSettings
        {
            UiScale = 97, TimelineFontSize = (TimelineFontSize)9, Theme = "  ‮ ", AccentColor = "rojo",
            DefaultProjectAspect = "3:2", DefaultProjectShortSide = 999, DefaultProjectFrameRate = new Rational(7, 1), DefaultProjectFolder = "relativa\\carpeta",
        };
        s.Normalize();
        Assert.Equal(100, s.UiScale);
        Assert.Equal(TimelineFontSize.Normal, s.TimelineFontSize);
        Assert.Equal(Themes.GraphiteName, s.Theme);
        Assert.Null(s.AccentColor);
        Assert.Equal("16:9", s.DefaultProjectAspect);
        Assert.Equal(1080, s.DefaultProjectShortSide);
        Assert.Equal(Rational.Fps30, s.DefaultProjectFrameRate);
        Assert.Null(s.DefaultProjectFolder);
        Assert.Equal(new NewProjectFormat(1920, 1080, Rational.Fps30), s.NewProjectFormat);

        s.DefaultProjectAspect = "9:16";
        s.DefaultProjectShortSide = 720;
        s.DefaultProjectFrameRate = Rational.Fps25;
        s.AccentColor = "4f9dde";
        s.UiScale = 125;
        s.Normalize();
        Assert.Equal(new NewProjectFormat(720, 1280, Rational.Fps25), s.NewProjectFormat);
        Assert.Equal("#4F9DDE", s.AccentColor);
        Assert.Equal(125, s.UiScale);
    }

    [Fact]
    public void ProyectoNuevo_TomaElFormatoDeLosAjustes()
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), newProjectFormat: new NewProjectFormat(1080, 1080, Rational.Fps24));
        Assert.Equal((1080, 1080, Rational.Fps24), (session.Sequence.Width, session.Sequence.Height, session.Sequence.FrameRate));
    }

    [Fact]
    public void CarpetaDeProyectos_MandaSobreLaUltimaUsada()
    {
        using var dir = new TempDirectory();
        var s = new AppSettings { DefaultProjectFolder = dir.Path, LastProjectSaveFolder = Path.GetTempPath() };
        Assert.Equal(dir.Path, FileDialogFolderPolicy.SaveFolder(s, SaveDialogKind.Project));
        s.DefaultProjectFolder = Path.Combine(dir.Path, "no-existe");
        Assert.Equal(Path.GetTempPath(), FileDialogFolderPolicy.SaveFolder(s, SaveDialogKind.Project));
    }

    [Fact]
    public void Temas_DeSerieCumplenAAYElClaroInvierteLosGrises()
    {
        Assert.Empty(Themes.ContrastWarnings(Themes.Graphite));
        Assert.Empty(Themes.ContrastWarnings(Themes.HighContrast));
        Assert.True(Themes.IsLight(Themes.Light));
        Assert.False(Themes.IsLight(Themes.Graphite));
        Assert.Equal(0xFFEFEFEFu, Themes.Light.Color("BackgroundBrush"));
        Assert.Equal(Themes.Graphite.Color("VideoClipBrush"), Themes.Light.Color("VideoClipBrush"));
        Assert.True(Personalization.Contrast(Themes.Light.Color("AccentBrush"), Themes.Light.Color("PanelBrush")) >= Personalization.UiContrast);
        Assert.True(Personalization.Contrast(Themes.Light.Color("TextBrush"), Themes.Light.Color("PanelBrush")) >= Personalization.TextContrast);
        Assert.Equal(["Grafito", "Claro", "Alto contraste"], Themes.BuiltIn.Select(t => t.Name));
    }

    [Fact]
    public void Temas_ElTemaDeWinUISigueAlFondoYElTextoDelAcentoCumpleAA()
    {
        // ThemeResources.RequestedTheme es Light justo cuando IsLight: Claro y los propios de fondo claro.
        Assert.False(Themes.IsLight(Themes.HighContrast));
        ThemeDefinition own = Themes.Graphite.Copy("Papel");
        own.Set("BackgroundBrush", 0xFFF5F0E6);
        Assert.True(Themes.IsLight(own));
        own.Set("BackgroundBrush", 0xFF2A2420);
        Assert.False(Themes.IsLight(own));

        foreach (ThemeDefinition theme in Themes.BuiltIn)
        {
            foreach ((string name, uint accent) in Personalization.AccentPalette)
            {
                ThemeDefinition themed = Themes.WithAccent(theme, Themes.FitAccent(theme, accent).Accent);
                double contrast = Personalization.Contrast(themed.Color("AccentForegroundBrush"), themed.Color("AccentBrush"));
                Assert.True(contrast >= Personalization.TextContrast, $"{theme.Name}, {name}: {contrast:0.00}");
            }
        }
    }

    [Fact]
    public void Temas_UnTemaRotoAvisaYSeAplicaGrafito()
    {
        ThemeDefinition broken = Themes.Graphite.Copy("Mío");
        broken.Colors["TextBrush"] = "#1D1D1D";
        broken.Colors["AccentBrush"] = "no es un color";
        broken.Colors["Inventado"] = "#FFFFFF";
        ThemeDefinition normalized = Themes.Normalize(broken)!;
        Assert.Equal(Themes.Graphite.Color("AccentBrush"), normalized.Color("AccentBrush"));
        Assert.DoesNotContain("Inventado", normalized.Colors.Keys);
        Assert.Contains(Themes.ContrastWarnings(normalized), w => w.StartsWith("«Texto principal» sobre «Paneles»", StringComparison.Ordinal));

        Assert.Null(Themes.Parse("{ esto no es json"));
        Assert.Null(Themes.Parse("""{"version": 99, "name": "Futuro", "colors": {}}"""));
        Assert.Null(Themes.Parse("""{"version": 1, "name": "   ", "colors": {}}"""));
        Assert.Equal(Themes.Graphite, Themes.Resolve("Desconocido", [], windowsIsLight: false, out string? warning));
        Assert.Contains("Desconocido", warning);
        Assert.Equal(Themes.Light, Themes.Resolve(Personalization.FollowWindowsTheme, [], windowsIsLight: true, out warning));
        Assert.Null(warning);
    }

    [Fact]
    public void Temas_LaVersion1SubeALa2ConLasPistasDelColorDeLosPaneles()
    {
        Assert.Equal(2, Themes.CurrentVersion);
        Assert.Contains("VideoTrackBrush", Themes.Slots.Select(s => s.Key));
        Assert.Contains("AudioTrackBrush", Themes.Slots.Select(s => s.Key));
        Assert.Equal(Themes.Graphite.Color("PanelBrush"), Themes.Graphite.Color("VideoTrackBrush"));
        Assert.Equal(Themes.Light.Color("PanelBrush"), Themes.Light.Color("AudioTrackBrush"));

        // Un tema guardado por la 1.0.2: sin pistas, con los paneles azulados.
        ThemeDefinition v1 = Themes.Parse("""{"version": 1, "name": "Azul", "colors": {"PanelBrush": "#1A2A3A", "BackgroundBrush": "#0A1420"}}""")!;
        Assert.Equal(2, v1.Version);
        Assert.Equal(0xFF1A2A3Au, v1.Color("VideoTrackBrush"));
        Assert.Equal(0xFF1A2A3Au, v1.Color("AudioTrackBrush"));
        Assert.Equal(Themes.Graphite.Color("ControlBrush"), v1.Color("ControlBrush"));
        Assert.Contains("\"version\": 2", Themes.Serialize(v1), StringComparison.Ordinal);

        // Uno de la versión 2 al que le falte una pista la toma de Grafito, como cualquier otra clave; uno sin paneles tampoco falla.
        ThemeDefinition v2 = Themes.Parse("""{"version": 2, "name": "Azul", "colors": {"PanelBrush": "#1A2A3A", "VideoTrackBrush": "#223344"}}""")!;
        Assert.Equal(0xFF223344u, v2.Color("VideoTrackBrush"));
        Assert.Equal(Themes.Graphite.Color("AudioTrackBrush"), v2.Color("AudioTrackBrush"));
        ThemeDefinition bare = Themes.Parse("""{"version": 1, "name": "Vacío", "colors": {}}""")!;
        Assert.Equal(Themes.Graphite.Color("PanelBrush"), bare.Color("VideoTrackBrush"));
        Assert.Null(Themes.Parse("""{"version": 3, "name": "Futuro", "colors": {}}"""));

        // El perfil lleva el tema propio dentro, con su propia versión: también se migra.
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        var old = new ThemeDefinition { Version = 1, Name = "Azul", Colors = new Dictionary<string, string>(StringComparer.Ordinal) { ["PanelBrush"] = "#1A2A3A" } };
        string profile = PersonalProfile.Serialize(new AppSettings { Theme = "Azul" }, registry, old);
        // Dos «version»: la del perfil y la del tema, las dos en 1.
        Assert.Equal(3, profile.Split("\"version\": 1").Length);
        using var dir = new TempDirectory();
        var themes = new ThemeStore(dir.Path);
        Assert.True(PersonalProfile.Apply(profile, new AppSettings(), registry, themes));
        Assert.Equal(2, themes.Find("Azul")!.Version);
        Assert.Equal(0xFF1A2A3Au, themes.Find("Azul")!.Color("AudioTrackBrush"));

        // Las pistas entran en la comprobación AA: texto principal sobre ellas.
        ThemeDefinition dark = Themes.Graphite.Copy("Pistas claras");
        dark.Set("VideoTrackBrush", 0xFFDDDDDD);
        Assert.Contains(Themes.ContrastWarnings(dark), w => w.StartsWith("«Texto principal» sobre «Fondo de las pistas de video»", StringComparison.Ordinal));
    }

    [Fact]
    public void SeguirAWindows_SoloVuelveAAplicarSiCambiaElModo()
    {
        var follower = new WindowsThemeFollower();
        // Sin «Seguir a Windows», ningún aviso de Windows toca el tema.
        follower.Applied(Themes.GraphiteName, windowsIsLight: false);
        Assert.False(follower.ShouldReapply(Themes.GraphiteName, windowsIsLight: true));

        follower.Applied(Personalization.FollowWindowsTheme, windowsIsLight: false);
        Assert.False(follower.ShouldReapply(Personalization.FollowWindowsTheme, windowsIsLight: false));
        Assert.True(follower.ShouldReapply(Personalization.FollowWindowsTheme, windowsIsLight: true));
        follower.Applied(Personalization.FollowWindowsTheme, windowsIsLight: true);
        Assert.False(follower.ShouldReapply(Personalization.FollowWindowsTheme, windowsIsLight: true));
        Assert.True(follower.ShouldReapply(Personalization.FollowWindowsTheme, windowsIsLight: false));

        // Se eligió «Seguir a Windows» en Preferencias con la app ya aplicada en Grafito: el primer aviso aplica.
        follower.Applied(Themes.GraphiteName, windowsIsLight: true);
        Assert.True(follower.ShouldReapply(Personalization.FollowWindowsTheme, windowsIsLight: true));
    }

    [Fact]
    public void TemaConAcento_YNombresDeArchivoSeguros()
    {
        ThemeDefinition themed = Themes.WithAccent(Themes.Graphite, 0xFF4F9DDE);
        Assert.Equal(0xFF4F9DDEu, themed.Color("AccentBrush"));
        Assert.Equal(0x4D4F9DDEu, themed.Color("AccentSubtleBrush"));
        Assert.Equal(Personalization.Ink, themed.Color("AccentForegroundBrush"));

        Assert.Equal("CON_ mi_tema.rodaje-tema", Themes.FileName("CON: mi/tema"));
        Assert.Equal("_nul.rodaje-tema", Themes.FileName("nul"));
        Assert.Equal("Tema ñandú.rodaje-tema", Themes.FileName("Tema ñandú"));
        Assert.Null(Themes.CleanName(" ‏ "));
        Assert.Equal(40, Themes.CleanName(new string('a', 60))!.Length);
    }

    [Fact]
    public void AlmacenDeTemas_GuardaLeeYSaltaLosRotos()
    {
        using var dir = new TempDirectory();
        var store = new ThemeStore(dir.Path);
        Assert.Empty(store.Load());
        ThemeDefinition own = Themes.Graphite.Copy("Mi tema");
        own.Set("AccentBrush", 0xFF2FB7A6);
        store.Save(own);
        File.WriteAllText(Path.Combine(dir.Path, "roto.rodaje-tema"), "{ roto");
        File.WriteAllText(Path.Combine(dir.Path, "serie.rodaje-tema"), Themes.Serialize(Themes.Graphite.Copy("Grafito")));
        Assert.Throws<InvalidOperationException>(() => store.Save(Themes.Light));

        var again = new ThemeStore(dir.Path);
        IReadOnlyList<string> warnings = again.Load();
        Assert.Equal(2, warnings.Count);
        Assert.Equal(["Mi tema"], again.Own.Select(t => t.Name));
        Assert.Equal(0xFF2FB7A6u, again.Find("mi tema")!.Color("AccentBrush"));
        Assert.Equal("Mi tema (copia)", again.FreeName("Mi tema"));
        Assert.Equal("Grafito (copia)", again.FreeName("Grafito"));
        Assert.True(again.Delete("Mi tema"));
        Assert.False(File.Exists(Path.Combine(dir.Path, "Mi tema.rodaje-tema")));
    }

    [Fact]
    public void Perfil_ExportaImportaYLimpiaLoQueVieneDeFuera()
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        var settings = new AppSettings { Theme = "Mi tema", AccentColor = "#4F9DDE", UiScale = 110, TimelineFontSize = TimelineFontSize.Large, DefaultVideoTracks = 4, DefaultProjectAspect = "9:16", ShortcutPreset = ShortcutPreset.Agile };
        settings.Normalize();
        WorkspaceProfiles.SaveCurrent(settings, "Subtitulos", new WorkspaceProfile { LibraryWidth = 300 });
        ThemeDefinition own = Themes.Graphite.Copy("Mi tema");
        string json = PersonalProfile.Serialize(settings, registry, own);
        Assert.Contains("\"version\": 1", json);

        using var dir = new TempDirectory();
        var themes = new ThemeStore(dir.Path);
        var target = new AppSettings();
        Assert.True(PersonalProfile.Apply(json, target, registry, themes));
        Assert.Equal("Mi tema", target.Theme);
        Assert.NotNull(themes.Find("Mi tema"));
        Assert.False(File.Exists(Path.Combine(dir.Path, "Mi tema.rodaje-tema")));
        Assert.Equal("#4F9DDE", target.AccentColor);
        Assert.Equal(110, target.UiScale);
        Assert.Equal(TimelineFontSize.Large, target.TimelineFontSize);
        Assert.Equal(4, target.DefaultVideoTracks);
        Assert.Equal("9:16", target.DefaultProjectAspect);
        Assert.Equal(ShortcutPreset.Agile, target.ShortcutPreset);
        Assert.Equal(["Subtitulos"], target.Workspaces.Select(w => w.Name));
        Assert.Equal(300, target.LibraryWidth);

        string hostile = json.Replace("\"Subtitulos\"", "\"Sub\\u202Etitulos\"", StringComparison.Ordinal)
            .Replace("\"uiScale\": 110", "\"uiScale\": 7", StringComparison.Ordinal)
            .Replace("\"defaultProjectFolder\": null", "\"defaultProjectFolder\": \"..\\\\relativa\"", StringComparison.Ordinal);
        var cleaned = new AppSettings();
        Assert.True(PersonalProfile.Apply(hostile, cleaned, registry, null));
        Assert.Equal(["Subtitulos"], cleaned.Workspaces.Select(w => w.Name));
        Assert.Equal(100, cleaned.UiScale);
        Assert.Null(cleaned.DefaultProjectFolder);

        Assert.False(PersonalProfile.Apply("{ roto", new AppSettings(), registry, null));
        Assert.False(PersonalProfile.Apply("""{"version": 2}""", new AppSettings(), registry, null));
        Assert.Contains("No se pudo leer", new PersonalizationDraft(new AppSettings(), registry).Import(Path.Combine(dir.Path, "x.rodaje-perfil"), null) ?? "", StringComparison.Ordinal);

        PersonalProfile.ResetAll(target, registry);
        Assert.Equal(Themes.GraphiteName, target.Theme);
        Assert.Empty(target.Workspaces);
        Assert.Equal(ShortcutPreset.Rodaje, registry.Preset);
    }

    [Fact]
    public void Borrador_ImportarYRestablecerNoTocanLoVivoHastaGuardar()
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        KeyChord x = KeyChord.Parse("X");
        registry.Rebind(ActionIds.Split, x);
        var live = new AppSettings { UiScale = 125, Theme = Themes.LightName };
        live.Normalize();
        WorkspaceProfiles.SaveCurrent(live, "Mío", new WorkspaceProfile { LibraryWidth = 300 });

        using var dir = new TempDirectory();
        var exported = new AppSettings { UiScale = 90, ShortcutPreset = ShortcutPreset.Classic };
        exported.Normalize();
        string path = Path.Combine(dir.Path, "p.rodaje-perfil");
        File.WriteAllText(path, PersonalProfile.Serialize(exported, new ShortcutRegistry(), null));

        var draft = new PersonalizationDraft(live, registry);
        Assert.Null(draft.Import(path, null));
        Assert.Equal(90, draft.Settings.UiScale);
        Assert.Equal(125, live.UiScale);
        draft.ResetAll();
        Assert.Empty(draft.Settings.Workspaces);
        Assert.Equal(ShortcutPreset.Rodaje, draft.Registry.Preset);
        Assert.False(draft.Registry.IsCustomized(ActionIds.Split));

        // Cancelar: lo vivo sigue igual.
        Assert.Equal(["Mío"], live.Workspaces.Select(w => w.Name));
        Assert.Equal(ActionIds.Split, registry.FindAction(x));
        Assert.Equal(Themes.LightName, live.Theme);

        // Guardar: se repiten los pasos en orden sobre lo vivo.
        Assert.True(draft.HasProfileChanges);
        draft.Commit(live, registry);
        Assert.False(draft.HasProfileChanges);
        Assert.Empty(live.Workspaces);
        Assert.Equal(Personalization.DefaultScale, live.UiScale);
        Assert.Null(registry.FindAction(x));
    }

    [Fact]
    public void Borrador_LosTemasBorradosYRenombradosSoloSeEscribenAlGuardar()
    {
        using var dir = new TempDirectory();
        var store = new ThemeStore(dir.Path);
        store.Save(Themes.Light.Copy("Mi claro"));
        store.Save(Themes.Graphite.Copy("Otro"));
        var registry = new ShortcutRegistry();
        var draft = new PersonalizationDraft(new AppSettings(), registry);

        draft.RenameTheme(store.Find("Mi claro")!, "Claro mío");
        draft.DeleteTheme(store, "Otro");
        Assert.Equal(["Claro mío"], store.Own.Select(t => t.Name));
        Assert.True(File.Exists(Path.Combine(dir.Path, "Mi claro.rodaje-tema")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "Otro.rodaje-tema")));

        // Cancelar relee la carpeta: vuelve todo como estaba.
        Assert.Empty(store.Load());
        Assert.Equal(["Mi claro", "Otro"], store.Own.Select(t => t.Name));

        draft = new PersonalizationDraft(new AppSettings(), registry);
        draft.RenameTheme(store.Find("Mi claro")!, "Claro mío");
        draft.DeleteTheme(store, "Otro");
        draft.CommitThemes(store);
        Assert.Equal(["Claro mío.rodaje-tema"], Directory.GetFiles(dir.Path).Select(f => Path.GetFileName(f)));
        Assert.Empty(store.Load());
        Assert.Equal(Themes.Light.Color("BackgroundBrush"), store.Find("Claro mío")!.Color("BackgroundBrush"));
    }

    [Fact]
    public void Ajustes_LaCopiaEsIndependiente()
    {
        var live = new AppSettings { Theme = "Mío" };
        WorkspaceProfiles.SaveCurrent(live, "A", new WorkspaceProfile());
        AppSettings copy = live.Clone();
        copy.Theme = Themes.LightName;
        copy.Workspaces.Clear();
        Assert.Equal("Mío", live.Theme);
        Assert.Single(live.Workspaces);
    }

    [Fact]
    public void FormatoDePreferencias_ElPrimerVideoNoLoPisa() => UiThread.Run(async () =>
    {
        var prober = new FakeProber().Video("a.mp4", 10, 640, 360, new Rational(25, 1));
        var vertical = new EditorSession(new FakePlayback(), prober, new FakeDialogs { Choice = UnsavedChangesChoice.Discard }, new SequentialIdGenerator(), newProjectFormat: new NewProjectFormat(1080, 1920, Rational.Fps60));
        Assert.True(vertical.KeepsTemplateFormat);
        await vertical.ImportMediaAsync([Path.GetFullPath("a.mp4")]);
        Assert.Equal((1080, 1920, Rational.Fps60), (vertical.Sequence.Width, vertical.Sequence.Height, vertical.Sequence.FrameRate));

        Assert.True(await vertical.NewProjectAsync());
        Assert.True(vertical.KeepsTemplateFormat);

        // Con el de fábrica, el primer video sigue adaptando la secuencia, como antes.
        var factory = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10, 640, 360, new Rational(25, 1)), new FakeDialogs(), new SequentialIdGenerator(), newProjectFormat: NewProjectFormat.Factory);
        Assert.False(factory.KeepsTemplateFormat);
        await factory.ImportMediaAsync([Path.GetFullPath("a.mp4")]);
        Assert.Equal((640, 360), (factory.Sequence.Width, factory.Sequence.Height));
    });
}
