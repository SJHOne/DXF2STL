namespace Dxf2Stl.Geometry;

public sealed class TriangleMesh
{
    public List<Vector3> Vertices { get; } = [];
    public List<(int A, int B, int C)> Triangles { get; } = [];

    public int AddVertex(Vector3 point)
    {
        Vertices.Add(point);
        return Vertices.Count - 1;
    }

    public void AddTriangle(int a, int b, int c)
    {
        if (a != b && b != c && c != a)
            Triangles.Add((a, b, c));
    }

    public void Append(TriangleMesh other)
    {
        var offset = Vertices.Count;
        Vertices.AddRange(other.Vertices);
        Triangles.AddRange(other.Triangles.Select(t => (t.A + offset, t.B + offset, t.C + offset)));
    }
}
