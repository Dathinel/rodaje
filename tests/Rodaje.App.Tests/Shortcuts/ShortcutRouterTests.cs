using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

public class ShortcutRouterTests
{
    private static KeyChord K(string text) => KeyChord.Parse(text);

    private static ShortcutRouter Router()
    {
        var registry = new ShortcutRegistry();
        registry.Register(new ShortcutAction("play", "Reproducir", "R", ["Espacio"]));
        registry.Register(new ShortcutAction("step", "Cuadro", "R", ["→"]) { Repeats = true });
        registry.Register(new ShortcutAction("save", "Guardar", "A", ["Ctrl+S"]) { WorksInTextInput = true });
        registry.Register(new ShortcutAction("delete", "Borrar", "E", ["Supr"]));
        return new ShortcutRouter(registry);
    }

    [Fact]
    public void Dispatch_LanzaLaAccionDeLaTecla()
    {
        ShortcutRouter router = Router();
        var invoked = new List<string>();
        int plays = 0;
        router.Bind("play", () => plays++);
        router.Invoked += (_, id) => invoked.Add(id);

        Assert.True(router.Dispatch(K("Espacio"), isRepeat: false, isTextInputFocused: false));
        Assert.False(router.Dispatch(K("Ctrl+Espacio"), false, false));

        Assert.Equal(1, plays);
        Assert.Equal(["play"], invoked);
    }

    [Fact]
    public void Dispatch_SinEjecutor_DejaPasarLaTecla()
    {
        Assert.False(Router().Dispatch(K("Supr"), false, false));
    }

    [Fact]
    public void ConFocoEnTexto_SoloPasanLasQueLoPermiten()
    {
        ShortcutRouter router = Router();
        int plays = 0;
        int saves = 0;
        router.Bind("play", () => plays++);
        router.Bind("save", () => saves++);

        Assert.False(router.Dispatch(K("Espacio"), false, isTextInputFocused: true));
        Assert.True(router.Dispatch(K("Ctrl+S"), false, isTextInputFocused: true));

        Assert.Equal(0, plays);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void EnUnMenuConTeclado_LasTeclasSinModificadorSonDelMenu()
    {
        ShortcutRouter router = Router();
        int plays = 0;
        int saves = 0;
        router.Bind("play", () => plays++);
        router.Bind("save", () => saves++);

        Assert.False(router.Dispatch(K("Espacio"), false, KeyFocus.KeyboardMenu));
        Assert.True(router.Dispatch(K("Ctrl+S"), false, KeyFocus.KeyboardMenu));

        Assert.Equal(0, plays);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void EnUnaLista_SuprYCtrlASonDeLaLista()
    {
        ShortcutRouter router = Router();
        router.Registry.Register(new ShortcutAction("all", "Seleccionar todo", "E", ["Ctrl+A"]));
        var invoked = new List<string>();
        router.Bind("delete", () => invoked.Add("delete"));
        router.Bind("all", () => invoked.Add("all"));
        router.Bind("play", () => invoked.Add("play"));

        Assert.False(router.Dispatch(K("Supr"), false, KeyFocus.List));
        Assert.False(router.Dispatch(K("Ctrl+A"), false, KeyFocus.List));
        Assert.True(router.Dispatch(K("Espacio"), false, KeyFocus.List));
        Assert.True(router.Dispatch(K("Supr"), false, KeyFocus.Other));
        Assert.True(router.Dispatch(K("Ctrl+A"), false, KeyFocus.Other));

        Assert.Equal(["play", "delete", "all"], invoked);
    }

    [Fact]
    public void EspacioConElFocoEnUnBotonOEnNada_Reproduce()
    {
        ShortcutRouter router = Router();
        int plays = 0;
        router.Bind("play", () => plays++);

        Assert.True(router.Dispatch(K("Espacio"), false, KeyFocus.Other));
        Assert.True(router.Dispatch(K("Espacio"), false, isTextInputFocused: false));

        Assert.Equal(2, plays);
    }

    [Fact]
    public void LaRepeticion_SeConsumePeroSoloRepiteLasQueRepiten()
    {
        ShortcutRouter router = Router();
        int plays = 0;
        int steps = 0;
        router.Bind("play", () => plays++);
        router.Bind("step", () => steps++);

        Assert.True(router.Dispatch(K("Espacio"), isRepeat: true, false));
        Assert.True(router.Dispatch(K("→"), isRepeat: true, false));
        Assert.True(router.Dispatch(K("→"), isRepeat: true, false));

        Assert.Equal(0, plays);
        Assert.Equal(2, steps);
    }

    [Fact]
    public void GanaElUltimoEjecutorQuePuede()
    {
        ShortcutRouter router = Router();
        var calls = new List<string>();
        bool libraryFocused = false;
        router.Bind("delete", () => calls.Add("línea de tiempo"));
        IDisposable library = router.Bind("delete", () => calls.Add("biblioteca"), () => libraryFocused);

        router.Invoke("delete");
        libraryFocused = true;
        router.Invoke("delete");
        library.Dispose();
        router.Invoke("delete");

        Assert.Equal(["línea de tiempo", "biblioteca", "línea de tiempo"], calls);
    }

    [Fact]
    public void SiNadiePuedeEjecutar_LaTeclaSigueSuCamino()
    {
        ShortcutRouter router = Router();
        router.Bind("delete", () => { }, () => false);
        Assert.False(router.CanInvoke("delete"));
        Assert.False(router.Dispatch(K("Supr"), false, false));
        Assert.False(router.Invoke("delete"));
    }

    [Fact]
    public void Suspend_DetieneElTecladoPeroNoLosMenus()
    {
        ShortcutRouter router = Router();
        int plays = 0;
        router.Bind("play", () => plays++);

        using (router.Suspend())
        {
            using (router.Suspend())
            {
                Assert.False(router.Dispatch(K("Espacio"), false, false));
            }

            Assert.True(router.IsSuspended);
            Assert.True(router.Invoke("play"));
        }

        Assert.False(router.IsSuspended);
        Assert.True(router.Dispatch(K("Espacio"), false, false));
        Assert.Equal(2, plays);
    }

    [Fact]
    public void Restrict_SoloDejaPasarLasAccionesPermitidas()
    {
        ShortcutRouter router = Router();
        int plays = 0;
        int saves = 0;
        router.Bind("play", () => plays++);
        router.Bind("save", () => saves++);

        using (router.Restrict(a => a.Group == "A"))
        {
            Assert.False(router.Dispatch(K("Espacio"), false, false));
            Assert.True(router.Dispatch(K("Ctrl+S"), false, false));
            Assert.True(router.Invoke("play"));
        }

        Assert.True(router.Dispatch(K("Espacio"), false, false));
        Assert.Equal(2, plays);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void Rebind_CambiaLaTeclaQueLanza()
    {
        ShortcutRouter router = Router();
        int plays = 0;
        router.Bind("play", () => plays++);
        router.Registry.Rebind("play", K("K"), replacing: K("Espacio"));

        Assert.False(router.Dispatch(K("Espacio"), false, false));
        Assert.True(router.Dispatch(K("K"), false, false));
        Assert.Equal(1, plays);
    }
}
