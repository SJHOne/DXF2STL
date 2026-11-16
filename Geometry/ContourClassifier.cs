namespace Dxf2Stl.Geometry;

public sealed record BoundaryContour(string Name, IReadOnlyList<(double X, double Y)> Points);

public static class ContourClassifier
{
    private sealed record Ring(string Name, List<(double X, double Y)> Points, double Area, Bounds Bounds);
    private readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY)
    {
        public bool Contains(Bounds other, double tolerance) =>
            MinX <= other.MinX + tolerance && MinY <= other.MinY + tolerance &&
            MaxX >= other.MaxX - tolerance && MaxY >= other.MaxY - tolerance;

        public bool Overlaps(Bounds other, double tolerance) =>
            MinX <= other.MaxX + tolerance && MaxX + tolerance >= other.MinX &&
            MinY <= other.MaxY + tolerance && MaxY + tolerance >= other.MinY;
    }

    public static IReadOnlyList<Profile> Classify(IReadOnlyList<BoundaryContour> contours)
    {
        if (contours.Count == 0)
            return [];

        var allPoints = contours.SelectMany(contour => contour.Points).ToArray();
        if (allPoints.Length == 0 || allPoints.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            throw new InvalidDataException("A contour contains a non-finite coordinate.");

        var scale = Math.Max(
            allPoints.Max(point => point.X) - allPoints.Min(point => point.X),
            allPoints.Max(point => point.Y) - allPoints.Min(point => point.Y));
        if (!double.IsFinite(scale))
            throw new InvalidDataException("The drawing extent is too large to classify safely.");
        var tolerance = Math.Max(scale * 1e-9, 1e-12);
        var areaTolerance = Math.Max(scale * tolerance, 1e-24);

        var rings = contours.Select(contour => CreateRing(contour, tolerance, areaTolerance)).ToArray();
        for (var i = 0; i < rings.Length; i++)
        {
            for (var j = i + 1; j < rings.Length; j++)
            {
                if (!rings[i].Bounds.Overlaps(rings[j].Bounds, tolerance))
                    continue;
                if (BoundariesIntersect(rings[i].Points, rings[j].Points, tolerance, areaTolerance))
                    throw new InvalidDataException(
                        $"Contours '{rings[i].Name}' and '{rings[j].Name}' touch, cross, or duplicate each other.");
            }
        }

        var parents = Enumerable.Repeat(-1, rings.Length).ToArray();
        var children = Enumerable.Range(0, rings.Length).Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < rings.Length; i++)
        {
            var parentArea = double.PositiveInfinity;
            for (var j = 0; j < rings.Length; j++)
            {
                if (i == j || rings[j].Area <= rings[i].Area || rings[j].Area >= parentArea ||
                    !rings[j].Bounds.Contains(rings[i].Bounds, tolerance))
                    continue;
                if (PointInRing(rings[i].Points[0], rings[j].Points))
                {
                    parents[i] = j;
                    parentArea = rings[j].Area;
                }
            }
            if (parents[i] >= 0)
                children[parents[i]].Add(i);
        }

        var depths = new int[rings.Length];
        for (var i = 0; i < rings.Length; i++)
        {
            var parent = parents[i];
            var guard = 0;
            while (parent >= 0)
            {
                depths[i]++;
                parent = parents[parent];
                if (++guard > rings.Length)
                    throw new InvalidDataException($"Containment of contour '{rings[i].Name}' is ambiguous.");
            }
        }

        var faceNumber = 0;
        var roots = Enumerable.Range(0, rings.Length)
            .Where(index => parents[index] < 0)
            .Select(BuildFace)
            .ToArray();
        return Flatten(roots).ToArray();

        static IEnumerable<Profile> Flatten(IEnumerable<Profile> profiles)
        {
            foreach (var profile in profiles)
            {
                yield return profile;
                foreach (var child in Flatten(profile.Children))
                    yield return child;
            }
        }

        Profile BuildFace(int index)
        {
            var outer = NormalizeWinding(rings[index].Points, counterClockwise: true);
            var holes = children[index]
                .Where(child => depths[child] == depths[index] + 1)
                .Select(child => (IReadOnlyList<(double X, double Y)>)
                    NormalizeWinding(rings[child].Points, counterClockwise: false))
                .ToArray();
            var number = ++faceNumber;
            var name = holes.Length == 0 ? $"Face {number}" : $"Face {number} ({holes.Length} hole{(holes.Length == 1 ? "" : "s")})";
            var nestedFaces = children[index]
                .Where(child => depths[child] == depths[index] + 1)
                .SelectMany(child => children[child]
                    .Where(grandchild => depths[grandchild] == depths[index] + 2)
                    .Select(BuildFace))
                .ToArray();
            return new Profile(name, outer, holes) { Children = nestedFaces };
        }
    }

    public static bool ContainsFilledRegion(Profile profile, (double X, double Y) point)
    {
        if (!PointInRing(point, profile.Points))
            return false;
        return profile.Holes.All(hole => !PointInRing(point, hole));
    }

    private static Ring CreateRing(BoundaryContour contour, double tolerance, double areaTolerance)
    {
        var points = new List<(double X, double Y)>();
        foreach (var point in contour.Points)
        {
            if (points.Count == 0 || DistanceSquared(points[^1], point) > tolerance * tolerance)
                points.Add(point);
        }
        while (points.Count > 1 && DistanceSquared(points[0], points[^1]) <= tolerance * tolerance)
            points.RemoveAt(points.Count - 1);

        if (points.Count < 3)
            throw new InvalidDataException($"Contour '{contour.Name}' has fewer than three distinct points.");

        for (var i = 0; i < points.Count; i++)
        {
            var previous = points[(i + points.Count - 1) % points.Count];
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            var firstX = current.X - previous.X;
            var firstY = current.Y - previous.Y;
            var secondX = next.X - current.X;
            var secondY = next.Y - current.Y;
            var cross = firstX * secondY - firstY * secondX;
            if (Math.Abs(cross) <= areaTolerance && firstX * secondX + firstY * secondY < 0)
                throw new InvalidDataException($"Contour '{contour.Name}' doubles back over itself.");
        }

        if (SelfIntersects(points, tolerance, areaTolerance))
            throw new InvalidDataException($"Contour '{contour.Name}' is self-intersecting.");

        var area = PolygonTriangulator.SignedArea(points);
        if (!double.IsFinite(area) || Math.Abs(area) <= areaTolerance)
            throw new InvalidDataException($"Contour '{contour.Name}' has zero or negligible area.");

        return new Ring(contour.Name, points, Math.Abs(area), new Bounds(
            points.Min(point => point.X), points.Min(point => point.Y),
            points.Max(point => point.X), points.Max(point => point.Y)));
    }

    private static bool SelfIntersects(
        IReadOnlyList<(double X, double Y)> points,
        double tolerance,
        double areaTolerance)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var nextI = (i + 1) % points.Count;
            for (var j = i + 1; j < points.Count; j++)
            {
                var nextJ = (j + 1) % points.Count;
                if (i == j || nextI == j || nextJ == i)
                    continue;
                if (SegmentsIntersect(points[i], points[nextI], points[j], points[nextJ], tolerance, areaTolerance))
                    return true;
            }
        }
        return false;
    }

    private static bool BoundariesIntersect(
        IReadOnlyList<(double X, double Y)> first,
        IReadOnlyList<(double X, double Y)> second,
        double tolerance,
        double areaTolerance)
    {
        for (var i = 0; i < first.Count; i++)
            for (var j = 0; j < second.Count; j++)
                if (SegmentsIntersect(first[i], first[(i + 1) % first.Count],
                        second[j], second[(j + 1) % second.Count], tolerance, areaTolerance))
                    return true;
        return false;
    }

    private static bool SegmentsIntersect(
        (double X, double Y) a,
        (double X, double Y) b,
        (double X, double Y) c,
        (double X, double Y) d,
        double tolerance,
        double areaTolerance)
    {
        if (Math.Max(a.X, b.X) + tolerance < Math.Min(c.X, d.X) ||
            Math.Max(c.X, d.X) + tolerance < Math.Min(a.X, b.X) ||
            Math.Max(a.Y, b.Y) + tolerance < Math.Min(c.Y, d.Y) ||
            Math.Max(c.Y, d.Y) + tolerance < Math.Min(a.Y, b.Y))
            return false;

        var abC = Cross(a, b, c);
        var abD = Cross(a, b, d);
        var cdA = Cross(c, d, a);
        var cdB = Cross(c, d, b);
        if (((abC > areaTolerance && abD < -areaTolerance) || (abC < -areaTolerance && abD > areaTolerance)) &&
            ((cdA > areaTolerance && cdB < -areaTolerance) || (cdA < -areaTolerance && cdB > areaTolerance)))
            return true;

        return Math.Abs(abC) <= areaTolerance && OnSegment(a, b, c, tolerance) ||
               Math.Abs(abD) <= areaTolerance && OnSegment(a, b, d, tolerance) ||
               Math.Abs(cdA) <= areaTolerance && OnSegment(c, d, a, tolerance) ||
               Math.Abs(cdB) <= areaTolerance && OnSegment(c, d, b, tolerance);
    }

    private static bool OnSegment(
        (double X, double Y) a,
        (double X, double Y) b,
        (double X, double Y) point,
        double tolerance) =>
        point.X >= Math.Min(a.X, b.X) - tolerance && point.X <= Math.Max(a.X, b.X) + tolerance &&
        point.Y >= Math.Min(a.Y, b.Y) - tolerance && point.Y <= Math.Max(a.Y, b.Y) + tolerance;

    private static bool PointInRing((double X, double Y) point, IReadOnlyList<(double X, double Y)> ring)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static List<(double X, double Y)> NormalizeWinding(
        IReadOnlyList<(double X, double Y)> points,
        bool counterClockwise)
    {
        var result = points.ToList();
        if ((PolygonTriangulator.SignedArea(points) > 0) != counterClockwise)
            result.Reverse();
        return result;
    }

    private static double Cross(
        (double X, double Y) a,
        (double X, double Y) b,
        (double X, double Y) c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static double DistanceSquared((double X, double Y) a, (double X, double Y) b)
    {
        var x = a.X - b.X;
        var y = a.Y - b.Y;
        return x * x + y * y;
    }
}
