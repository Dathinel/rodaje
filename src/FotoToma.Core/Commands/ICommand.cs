using FotoToma.Core.Model;

namespace FotoToma.Core.Commands;

/// <summary>Acción que se puede deshacer.</summary>
public interface ICommand
{
    /// <summary>Texto para el menú de deshacer, por ejemplo «Cortar clip».</summary>
    string Description { get; }

    /// <summary>Aplica la acción (también al rehacer); si falla, deja el proyecto sin cambios.</summary>
    void Apply(Project project);

    void Revert(Project project);

    /// <summary>Absorbe el comando siguiente para deshacer ambos en un paso; <c>true</c> si lo absorbió.</summary>
    bool TryMerge(ICommand following);
}
