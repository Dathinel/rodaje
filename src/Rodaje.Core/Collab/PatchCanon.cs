namespace Rodaje.Core.Collab;

/// <summary>Orden común de los cambios al crear y aplicar parches.</summary>
internal static class PatchCanon
{
    internal static int DeleteRank(ElementKind kind) => kind switch
    {
        ElementKind.Clip => 0,
        ElementKind.Marker => 1,
        ElementKind.Region => 2,
        ElementKind.Track => 3,
        ElementKind.Sequence => 4,
        ElementKind.Transcript => 5,
        ElementKind.Media => 6,
        _ => 7,
    };

    internal static int UpsertRank(ElementKind kind) => kind switch
    {
        ElementKind.Media => 0,
        ElementKind.Transcript => 1,
        ElementKind.Sequence => 2,
        ElementKind.Track => 3,
        ElementKind.Marker => 4,
        ElementKind.Region => 5,
        ElementKind.Clip => 6,
        _ => 7,
    };

    internal static int Compare(PatchOp left, PatchOp right)
    {
        if (left.IsDelete != right.IsDelete)
        {
            return left.IsDelete ? -1 : 1;
        }

        int rank = left.IsDelete
            ? DeleteRank(left.Kind).CompareTo(DeleteRank(right.Kind))
            : UpsertRank(left.Kind).CompareTo(UpsertRank(right.Kind));
        return rank != 0 ? rank : StringComparer.Ordinal.Compare(left.Id, right.Id);
    }
}
