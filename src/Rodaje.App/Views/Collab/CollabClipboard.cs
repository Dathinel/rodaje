namespace Rodaje.App.Views.Collab;

internal static class CollabClipboard
{
    /// <summary>Pone el texto en el portapapeles; <c>false</c> si Windows no lo deja (otro programa lo tiene abierto).</summary>
    public static bool TrySetText(string text)
    {
        try
        {
            var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>El texto del portapapeles, o <c>null</c> si no hay texto o Windows no deja leerlo.</summary>
    public static async Task<string?> TryGetTextAsync()
    {
        try
        {
            var content = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            return content.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text) ? await content.GetTextAsync() : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
