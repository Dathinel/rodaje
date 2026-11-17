namespace Rodaje.Render.Text;

internal readonly record struct TitleRect(float Left, float Top, float Right, float Bottom)
{
    public bool IsEmpty => !(Right > Left && Bottom > Top);

    public TitleRect Union(TitleRect other) => IsEmpty ? other : other.IsEmpty ? this
        : new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    public TitleRect Inflate(float by) => Inflate(by, by);

    public TitleRect Inflate(float x, float y) => new(Left - x, Top - y, Right + x, Bottom + y);

    public TitleRect Offset(float x, float y) => new(Left + x, Top + y, Right + x, Bottom + y);

    public float Width => Right - Left;

    public float Height => Bottom - Top;

    public System.Numerics.Vector2 Center => new((Left + Right) / 2, (Top + Bottom) / 2);
}
