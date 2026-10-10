using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Views.Shortcuts;

/// <summary>Una tecla de una fila: se pulsa para cambiarla y tiene su botón para quitarla.</summary>
internal sealed partial class KeyChip(ShortcutRow row, KeyChord chord, string text)
{
    public ShortcutRow Row { get; } = row;

    public KeyChord Chord { get; } = chord;

    public string Text { get; } = text;

    public string ChangeName => $"{Text}, tecla de {Row.Label}. Pulsa para cambiarla";

    public string RemoveName => $"Quitar {Text} de {Row.Label}";

    public string ChangeTip => $"Cambiar «{Text}»";

    public string RemoveTip => $"Quitar «{Text}»";
}

/// <summary>Una acción de la página de atajos tal como se muestra: texto, teclas, si está cambiada y si se está capturando.</summary>
internal sealed partial class ShortcutRow : INotifyPropertyChanged
{
    private bool _isCustomized;
    private bool _isCapturing;
    private string _captureText = "";
    private string _conflictText = "";

    public ShortcutRow(ShortcutAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ActionId = action.Id;
        Label = action.Label;
        Group = action.Group;
        Description = action.Description;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ActionId { get; }

    public string Label { get; }

    public string Group { get; }

    public string Description { get; }

    public Visibility DescriptionVisibility => string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;

    public ObservableCollection<KeyChip> Chips { get; } = [];

    public IReadOnlyList<KeyChord> Keys { get; private set; } = [];

    public bool IsCustomized
    {
        get => _isCustomized;
        private set => Set(ref _isCustomized, value, nameof(IsCustomized), nameof(CustomizedOpacity), nameof(ResetVisibility), nameof(StateName));
    }

    /// <summary>La barra de acento a la izquierda de las filas cambiadas por la persona.</summary>
    public double CustomizedOpacity => _isCustomized ? 1 : 0;

    public Visibility ResetVisibility => _isCustomized ? Visibility.Visible : Visibility.Collapsed;

    public bool IsCapturing
    {
        get => _isCapturing;
        private set => Set(ref _isCapturing, value, nameof(IsCapturing), nameof(CaptureVisibility), nameof(ChipsVisibility), nameof(NoKeysVisibility));
    }

    public Visibility CaptureVisibility => _isCapturing ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ChipsVisibility => _isCapturing || Chips.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NoKeysVisibility => !_isCapturing && Chips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string CaptureText
    {
        get => _captureText;
        set => Set(ref _captureText, value, nameof(CaptureText));
    }

    /// <summary>Aviso cuando otra acción registrada comparte una tecla con esta.</summary>
    public string ConflictText
    {
        get => _conflictText;
        private set => Set(ref _conflictText, value, nameof(ConflictText), nameof(ConflictVisibility));
    }

    public Visibility ConflictVisibility => string.IsNullOrEmpty(_conflictText) ? Visibility.Collapsed : Visibility.Visible;

    public string ChangeName => $"Cambiar la tecla de {Label}";

    public string AddName => $"Añadir otra tecla a {Label}";

    public string ResetName => $"Volver a las teclas de fábrica de {Label}";

    /// <summary>Nombre de la fila para el lector de pantalla: acción, teclas y si está cambiada.</summary>
    public string StateName =>
        $"{Label}. {(Chips.Count == 0 ? "Sin tecla" : string.Join(", ", Chips.Select(c => c.Text)))}{(_isCustomized ? ". Cambiada por ti" : "")}";

    public void Update(IReadOnlyList<KeyChord> keys, bool customized, IReadOnlyList<string> conflictLabels, Func<KeyChord, string> keyText)
    {
        if (!keys.SequenceEqual(Keys) || Chips.Any(c => c.Text != keyText(c.Chord)))
        {
            Keys = keys;
            Chips.Clear();
            foreach (KeyChord key in keys)
            {
                Chips.Add(new KeyChip(this, key, keyText(key)));
            }

            Raise(nameof(ChipsVisibility), nameof(NoKeysVisibility), nameof(StateName));
        }

        IsCustomized = customized;
        ConflictText = conflictLabels.Count == 0 ? "" : $"Comparte una tecla con «{string.Join("», «", conflictLabels)}»; cámbiala en una de las dos";
    }

    public void BeginCapture(string text)
    {
        CaptureText = text;
        IsCapturing = true;
    }

    public void EndCapture() => IsCapturing = false;

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(names);
    }

    private void Raise(params string[] names)
    {
        foreach (string name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
