using UIMaps.Protocol.V1;

namespace UIMaps.Protocol;

/// <summary>Geometry can contradict an identity, but cannot establish one.</summary>
public static class NativeControlLayout
{
    public static bool Conflicts(RectPx box, RectPx owner, RectPx otherBox, RectPx otherOwner)
    {
        if (owner.Width <= 0 || owner.Height <= 0 || otherOwner.Width <= 0 || otherOwner.Height <= 0) return false;
        // Fixed native forms often keep their controls in place when only the
        // enclosing window is resized. Normalizing by that window alone turns
        // an unchanged field into a false contradiction.
        double[] Fixed(RectPx b, RectPx o) => [b.X - (double)o.X, b.Y - (double)o.Y, b.Width, b.Height];
        var a = Fixed(box, owner); var b = Fixed(otherBox, otherOwner);
        if (a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= 4)) return false;
        double[] Relative(double[] value, RectPx o) => [value[0] / o.Width, value[1] / o.Height, value[2] / o.Width, value[3] / o.Height];
        return Relative(a, owner).Zip(Relative(b, otherOwner)).Any(p => Math.Abs(p.First - p.Second) > .035);
    }
}
