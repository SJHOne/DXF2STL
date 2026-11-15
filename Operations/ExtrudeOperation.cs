using Dxf2Stl.Geometry;

namespace Dxf2Stl.Operations;

public sealed class ExtrudeOperation : IProfileOperation
{
    public string Name => "Extrude selected faces";

    public TriangleMesh Build(IReadOnlyList<Profile> profiles, OperationSettings settings)
    {
        if (profiles.Count == 0)
            throw new InvalidDataException("Select at least one closed face to extrude.");
        if (!double.IsFinite(settings.Amount) || Math.Abs(settings.Amount) < 1e-9)
            throw new InvalidDataException("Extrusion distance must be a non-zero number.");

        var result = new TriangleMesh();
        foreach (var profile in profiles)
        {
            var points = profile.Points;
            var triangles = PolygonTriangulator.Triangulate(points);
            var bottom = points.Select(p => result.AddVertex(new Vector3(p.X, p.Y, 0))).ToArray();
            var top = points.Select(p => result.AddVertex(new Vector3(p.X, p.Y, settings.Amount))).ToArray();

            for (var i = 0; i < triangles.Count; i += 3)
            {
                var a = triangles[i];
                var b = triangles[i + 1];
                var c = triangles[i + 2];
                result.AddTriangle(top[a], top[b], top[c]);
                result.AddTriangle(bottom[c], bottom[b], bottom[a]);
            }

            var counterClockwise = PolygonTriangulator.SignedArea(points) > 0;
            for (var i = 0; i < points.Count; i++)
            {
                var next = (i + 1) % points.Count;
                var a = counterClockwise ? i : next;
                var b = counterClockwise ? next : i;
                result.AddTriangle(bottom[a], bottom[b], top[b]);
                result.AddTriangle(bottom[a], top[b], top[a]);
            }
        }

        return result;
    }
}
