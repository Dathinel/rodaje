using Rodaje.Playback.Composition;

namespace Rodaje.App.Views.Player;

internal sealed partial class PlayerEngine
{
    internal static int Signature(CompositionFrame frame)
    {
        var hash = new HashCode();
        AddLayers(ref hash, frame.Layers);
        return hash.ToHashCode();
    }

    private static void AddLayers(ref HashCode hash, IReadOnlyList<LayerFrame> layers)
    {
        hash.Add(layers.Count);
        foreach (LayerFrame layer in layers)
        {
            AddLayer(ref hash, layer);
        }
    }

    private static void AddLayer(ref HashCode hash, LayerFrame layer)
    {
        hash.Add(layer.TrackIndex);
        hash.Add(layer.ClipId);
        hash.Add(layer.Frame.Index);
        hash.Add(layer.Next?.Index ?? -1);
        hash.Add(layer.Fraction);
        hash.Add(layer.Sampling);
        hash.Add(layer.IsProxy);
        hash.Add(layer.Transform);
        if (layer.Nested is { } nested)
        {
            AddLayers(ref hash, nested);
        }

        if (layer.Transition is { } transition)
        {
            hash.Add(transition.Progress);
            AddLayer(ref hash, transition.Incoming);
        }
    }
}
