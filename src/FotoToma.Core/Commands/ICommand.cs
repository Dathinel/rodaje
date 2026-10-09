using FotoToma.Core.Model;

namespace FotoToma.Core.Commands;

/// <summary>
/// Una acción que se puede deshacer (patrón comando). <see cref="Apply"/> se llama al ejecutarla y en
/// cada rehacer; <see cref="Revert"/> deja el proyecto exactamente como estaba antes de aplicarla.
/// </summary>
public interface ICommand
{
    /// <summary>Texto corto para el menú de deshacer, por ejemplo «Cortar clip».</summary>
    string Description { get; }

    /// <summary>Aplica la acción. Si falla, lanza una excepción y deja el proyecto sin cambios.</summary>
    void Apply(Project project);

    /// <summary>Revierte la acción ya aplicada.</summary>
    void Revert(Project project);

    /// <summary>
    /// Intenta absorber el comando siguiente para que ambos se deshagan en un solo paso, como los
    /// muchos movimientos pequeños de un arrastre. Devuelve <c>true</c> si lo absorbió.
    /// </summary>
    bool TryMerge(ICommand following);
}
