namespace Rodaje.Playback;

/// <summary>Dónde corre el bucle de decodificación. Debe ejecutar el trabajo en el orden en que llega.</summary>
/// <remarks>En producción es <see cref="DecodeThread"/>; las pruebas lo ejecutan en línea para ser deterministas.</remarks>
public interface IDecodeExecutor
{
    void Post(Action work);
}
