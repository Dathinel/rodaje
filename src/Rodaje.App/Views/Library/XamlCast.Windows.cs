namespace Rodaje.App.Views.Library;

/// <summary>Conversión de un objeto de XAML que llega como <c>object</c>: en Windows pasa por CsWinRT, que con AOT no admite el molde directo.</summary>
internal static class XamlCast
{
    public static T As<T>(object value) => WinRT.CastExtensions.As<T>(value);
}
