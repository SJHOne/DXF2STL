using Dxf2Stl.Geometry;

namespace Dxf2Stl.Operations;

public sealed class RevolveOperation : IProfileOperation
{
    public string Name => "Revolve drawing";

    public TriangleMesh Build(IReadOnlyList<Profile> profiles, OperationSettings settings)
    {
        if (profiles.Count == 0)
            throw new InvalidDataException("Load at least one closed profile to revolve.");
        if (!double.IsFinite(settings.Amount) || Math.Abs(settings.Amount) < 1e-6 || Math.Abs(settings.Amount) > 360)
            throw new InvalidDataException("Revolution angle must be between -360 and 360 degrees, and non-zero.");
        if (settings.Axis is not ('X' or 'Y'))
            throw new InvalidDataException("A profile in the XY plane cannot produce a 3D solid when revolved around Z. Choose X or Y.");

        var result = new TriangleMesh();
        foreach (var profile in profiles)
            AddRevolvedProfile(result, profile, settings);
        return result;
    }

    private static void AddRevolvedProfile(TriangleMesh mesh, Profile profile, OperationSettings settings)
    {
        var points = profile.Points;
        var triangulation = PolygonTriangulator.Triangulate(points);
        var fullTurn = Math.Abs(Math.Abs(settings.Amount) - 360) < 1e-8;
        var segments = Math.Clamp(settings.Segments, 8, 512);
        var sliceCount = fullTurn ? segments : segments + 1;
        var bounds = points.SelectMany(p => new[] { Math.Abs(p.X), Math.Abs(p.Y) }).DefaultIfEmpty(1).Max();
        var quantum = Math.Max(bounds * 1e-9, 1e-10);
        var vertices = new Dictionary<(long X, long Y, long Z), int>();

        int At(int pointIndex, int slice)
        {
            var angle = settings.Amount * Math.PI / 180 * slice / segments;
            var p = points[pointIndex];
            var rotated = Rotate(p.X, p.Y, settings.Axis, angle);
            var key = ((long)Math.Round(rotated.X / quantum),
                       (long)Math.Round(rotated.Y / quantum),
                       (long)Math.Round(rotated.Z / quantum));
            if (!vertices.TryGetValue(key, out var index))
            {
                index = mesh.AddVertex(rotated);
                vertices.Add(key, index);
            }
            return index;
        }

        for (var slice = 0; slice < segments; slice++)
        {
            var nextSlice = fullTurn ? (slice + 1) % segments : slice + 1;
            for (var i = 0; i < points.Count; i++)
            {
                var next = (i + 1) % points.Count;
                var a = At(i, slice);
                var b = At(next, slice);
                var c = At(next, nextSlice);
                var d = At(i, nextSlice);
                mesh.AddTriangle(a, b, c);
                mesh.AddTriangle(a, c, d);
            }
        }

        if (!fullTurn)
        {
            var start = Enumerable.Range(0, points.Count).Select(i => At(i, 0)).ToArray();
            var end = Enumerable.Range(0, points.Count).Select(i => At(i, segments)).ToArray();
            for (var i = 0; i < triangulation.Count; i += 3)
            {
                var a = triangulation[i];
                var b = triangulation[i + 1];
                var c = triangulation[i + 2];
                mesh.AddTriangle(start[a], start[b], start[c]);
                mesh.AddTriangle(end[c], end[b], end[a]);
            }
        }
    }

    private static Vector3 Rotate(double x, double y, char axis, double angle)
    {
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        return axis switch
        {
            'X' => new Vector3(x, y * cos, y * sin),
            'Y' => new Vector3(x * cos, y, -x * sin),
            _ => throw new ArgumentOutOfRangeException(nameof(axis))
        };
    }
}
