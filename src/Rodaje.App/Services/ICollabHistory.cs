namespace Rodaje.App.Services;

/// <summary>Deshacer y rehacer de una sala de colaboración: solo lo propio, por parches inversos. Lo implementa <c>CollabEngine</c>.</summary>
public interface ICollabHistory
{
    bool CanUndo { get; }

    bool CanRedo { get; }

    string? UndoDescription { get; }

    string? RedoDescription { get; }

    bool Undo();

    bool Redo();
}
