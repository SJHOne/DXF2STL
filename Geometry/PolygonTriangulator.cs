using EarcutDotNet;

namespace Dxf2Stl.Geometry;

public static class PolygonTriangulator
{
    public static List<int> Triangulate(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 3)
            throw new InvalidDataException("A profile must contain at least three points.");

        var area = SignedArea(points);
        if (Math.Abs(area) < 1e-12)
            throw new InvalidDataException("A profile has zero area.");

        var remaining = Enumerable.Range(0, points.Count).ToList();
        if (area < 0)
            remaining.Reverse();

        var result = new List<int>((points.Count - 2) * 3);
        var guard = points.Count * points.Count;
        while (remaining.Count > 3 && guard-- > 0)
        {
            var earFound = false;
            for (var i = 0; i < remaining.Count; i++)
            {
                var previous = remaining[(i + remaining.Count - 1) % remaining.Count];
                var current = remaining[i];
                var next = remaining[(i + 1) % remaining.Count];
                if (Cross(points[previous], points[current], points[next]) <= 1e-12)
                    continue;

                var containsPoint = remaining.Any(index =>
                    index != previous && index != current && index != next &&
                    InTriangle(points[index], points[previous], points[current], points[next]));
                if (containsPoint)
                    continue;

                result.Add(previous);
                result.Add(current);
                result.Add(next);
                remaining.RemoveAt(i);
                earFound = true;
                break;
            }

            if (!earFound)
                throw new InvalidDataException("A profile is self-intersecting or cannot be triangulated.");
        }

        result.AddRange(remaining);
        return result;
    }

    public static List<int> Triangulate(Profile profile)
    {
        var rings = new[] { profile.Points }.Concat(profile.Holes).ToArray();
        var pointCount = rings.Sum(ring => ring.Count);
        var coordinates = new double[pointCount * 2];
        var holeStarts = new int[profile.Holes.Count];
        var origin = profile.Points[0];
        var pointIndex = 0;
        for (var ringIndex = 0; ringIndex < rings.Length; ringIndex++)
        {
            if (ringIndex > 0)
                holeStarts[ringIndex - 1] = pointIndex;
            foreach (var point in rings[ringIndex])
            {
                coordinates[pointIndex * 2] = point.X - origin.X;
                coordinates[pointIndex * 2 + 1] = point.Y - origin.Y;
                pointIndex++;
            }
        }

        var indices = Earcut.Triangulate(coordinates, holeStarts).ToList();
        var expectedArea = Math.Abs(SignedArea(profile.Points)) -
                           profile.Holes.Sum(hole => Math.Abs(SignedArea(hole)));
        var scale = Math.Max(
            profile.Points.Max(point => point.X) - profile.Points.Min(point => point.X),
            profile.Points.Max(point => point.Y) - profile.Points.Min(point => point.Y));
        var tolerance = Math.Max(expectedArea * 1e-8, scale * scale * 1e-12);
        if (expectedArea <= tolerance || indices.Count == 0 || indices.Count % 3 != 0)
            throw new InvalidDataException($"Face '{profile.Name}' could not be triangulated.");

        var actualArea = 0.0;
        for (var i = 0; i < indices.Count; i += 3)
        {
            var a = ReadPoint(indices[i]);
            var b = ReadPoint(indices[i + 1]);
            var c = ReadPoint(indices[i + 2]);
            actualArea += Math.Abs(Cross(a, b, c)) / 2;
            var centroid = ((a.X + b.X + c.X) / 3 + origin.X, (a.Y + b.Y + c.Y) / 3 + origin.Y);
            if (!ContourClassifier.ContainsFilledRegion(profile, centroid))
                throw new InvalidDataException($"Triangulation for face '{profile.Name}' bridges a hole or leaves its boundary.");
        }
        if (Math.Abs(actualArea - expectedArea) > tolerance)
            throw new InvalidDataException($"Triangulation for face '{profile.Name}' does not cover its filled region.");
        return indices;

        (double X, double Y) ReadPoint(int index) =>
            (coordinates[index * 2], coordinates[index * 2 + 1]);
    }

    public static double SignedArea(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 3)
            return 0;
        var origin = points[0];
        var area = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var next = points[(i + 1) % points.Count];
            var currentX = points[i].X - origin.X;
            var currentY = points[i].Y - origin.Y;
            var nextX = next.X - origin.X;
            var nextY = next.Y - origin.Y;
            area += currentX * nextY - nextX * currentY;
        }
        return area / 2;
    }

    private static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool InTriangle(
        (double X, double Y) p,
        (double X, double Y) a,
        (double X, double Y) b,
        (double X, double Y) c)
    {
        const double epsilon = 1e-12;
        return Cross(a, b, p) > epsilon && Cross(b, c, p) > epsilon && Cross(c, a, p) > epsilon;
    }
}
