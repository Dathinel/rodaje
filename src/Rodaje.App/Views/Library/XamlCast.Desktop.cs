namespace Rodaje.App.Views.Library;

/// <summary>Conversión de un objeto de XAML que llega como <c>object</c>: en Uno los controles son objetos de .NET.</summary>
internal static class XamlCast
{
    public static T As<T>(object value) => (T)value;
}
