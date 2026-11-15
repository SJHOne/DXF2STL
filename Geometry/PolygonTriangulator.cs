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

    public static double SignedArea(IReadOnlyList<(double X, double Y)> points)
    {
        var area = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var next = points[(i + 1) % points.Count];
            area += points[i].X * next.Y - next.X * points[i].Y;
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
