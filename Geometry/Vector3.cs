namespace Dxf2Stl.Geometry;

public readonly record struct Vector3(double X, double Y, double Z)
{
    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3 operator *(Vector3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vector3 Cross(Vector3 a, Vector3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
}

public sealed record Profile(string Name, IReadOnlyList<(double X, double Y)> Points)
{
    public override string ToString() => Name;
}
