using Microsoft.UI.Xaml.Controls;

namespace Rodaje.App.Views.Player;

/// <summary>En Linux no hay cadenas de intercambio de DXGI: el visor va por <c>PlayerView.Desktop.cs</c> y la otra pantalla no está.</summary>
internal static class SwapChainPanelNative
{
    public static void SetSwapChain(SwapChainPanel panel, nint swapChain) =>
        throw new PlatformNotSupportedException("El visor en otra pantalla todavía no está en Linux.");
}
