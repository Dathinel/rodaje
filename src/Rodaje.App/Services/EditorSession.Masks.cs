using Rodaje.Render;

namespace Rodaje.App.Services;

/// <summary>Herramienta del visor con la pestaña «Máscaras» a la vista.</summary>
public enum MaskTool
{
    /// <summary>Elegir, mover la máscara, cambiar su caja y editar vértices y manejadores.</summary>
    Select,
    Rectangle,
    Ellipse,

    /// <summary>Pluma Bézier: clic pone una esquina, arrastrar saca manejadores, clic en el primer punto cierra.</summary>
    Pen,

    /// <summary>Pluma de trazo: una línea abierta con grosor; Intro la termina.</summary>
    StrokePen,

    /// <summary>A mano alzada: al soltar queda una Bézier suave.</summary>
    Freehand,
    Star,
}

/// <summary>
/// Las máscaras entre la pestaña «Máscaras» y el visor: cuál se edita, con qué herramienta, qué vértices están elegidos y
/// cómo se ve la máscara mientras tanto.
/// </summary>
public sealed partial class EditorSession
{
    private string? _editedMaskId;
    private bool _maskEditing;
    private MaskTool _maskTool;
    private IReadOnlyList<int> _maskVertices = [];
    private MaskViewMode _maskView;

    /// <summary>Cambió la máscara que se edita, o se abrió o cerró su pestaña.</summary>
    public event EventHandler? MaskEditingChanged;

    /// <summary>Cambió la herramienta, los vértices elegidos o la vista de la máscara.</summary>
    public event EventHandler? MaskToolChanged;

    /// <summary>El visor eligió (o creó) otra máscara: la pestaña la muestra.</summary>
    public event EventHandler<string>? MaskPicked;

    /// <summary>Se pidió la pestaña «Máscaras» (menú del clip).</summary>
    public event EventHandler? MaskPanelRequested;

    /// <summary>La pestaña de máscaras está a la vista: el visor muestra las asas de <see cref="EditedMaskId"/>.</summary>
    public bool IsMaskEditing => _maskEditing;

    /// <summary>La máscara (efecto <c>rodaje.mask</c>) elegida en la pestaña; nula si ninguna.</summary>
    public string? EditedMaskId => _editedMaskId;

    public MaskTool MaskTool => _maskTool;

    /// <summary>Índices de los vértices elegidos de la máscara que se edita.</summary>
    public IReadOnlyList<int> SelectedMaskVertices => _maskVertices;

    /// <summary>Solo vista previa: la máscara elegida en gris o superpuesta.</summary>
    public MaskViewMode MaskView => _maskView;

    public void SetMaskEditing(bool active, string? maskId)
    {
        VerifyAccess();
        if (_maskEditing == active && _editedMaskId == maskId)
        {
            return;
        }

        if (_editedMaskId != maskId)
        {
            _maskVertices = [];
        }

        _maskEditing = active;
        _editedMaskId = maskId;
        MaskEditingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetMaskTool(MaskTool tool)
    {
        VerifyAccess();
        if (_maskTool != tool)
        {
            _maskTool = tool;
            MaskToolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SelectMaskVertices(IReadOnlyList<int> vertices)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(vertices);
        if (!_maskVertices.SequenceEqual(vertices))
        {
            _maskVertices = [.. vertices.Distinct().Order()];
            MaskToolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetMaskView(MaskViewMode mode)
    {
        VerifyAccess();
        if (_maskView != mode)
        {
            _maskView = mode;
            MaskToolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>El visor eligió <paramref name="maskId"/>: la pestaña la muestra y la deja para editar.</summary>
    public void PickMask(string maskId)
    {
        VerifyAccess();
        MaskPicked?.Invoke(this, maskId);
    }

    public void ShowMaskPanel()
    {
        VerifyAccess();
        MaskPanelRequested?.Invoke(this, EventArgs.Empty);
    }
}
