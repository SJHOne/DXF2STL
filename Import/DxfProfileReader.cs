using System.Globalization;
using Dxf2Stl.Geometry;

namespace Dxf2Stl.Import;

public sealed class DxfProfileReader
{
    private readonly record struct Pair(int Code, string Value);
    private readonly record struct Vertex(double X, double Y, double Z, double Bulge);

    public IReadOnlyList<Profile> Read(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length % 2 != 0)
            throw new InvalidDataException("This DXF file is incomplete or is not an ASCII DXF.");

        var pairs = new List<Pair>(lines.Length / 2);
        for (var i = 0; i < lines.Length; i += 2)
        {
            if (!int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                throw new InvalidDataException($"Invalid DXF group code on line {i + 1}.");
            pairs.Add(new Pair(code, lines[i + 1].Trim()));
        }

        var entities = ReadEntityChunks(pairs);
        var contours = new List<BoundaryContour>();
        for (var i = 0; i < entities.Count; i++)
        {
            var chunk = entities[i];
            switch (Value(chunk, 0))
            {
                case "LWPOLYLINE":
                    if (TryReadClosedPolyline(chunk, out var lightPoints))
                        AddContour(contours, $"LWPOLYLINE {contours.Count + 1}", lightPoints);
                    break;
                case "POLYLINE":
                    var vertices = new List<Vertex>();
                    var j = i + 1;
                    while (j < entities.Count && Value(entities[j], 0) == "VERTEX")
                        vertices.Add(ReadVertex(entities[j++]));
                    i = j < entities.Count && Value(entities[j], 0) == "SEQEND" ? j : j - 1;
                    var polylineFlags = Integer(chunk, 70);
                    if ((polylineFlags & 1) != 0 && (polylineFlags & (8 | 16 | 32 | 64)) == 0 &&
                        Math.Abs(Number(chunk, 30)) < 1e-9 && vertices.All(vertex => Math.Abs(vertex.Z) < 1e-9))
                        AddContour(contours, $"POLYLINE {contours.Count + 1}", ExpandBulges(vertices, true));
                    break;
                case "CIRCLE":
                    var radius = Number(chunk, 40);
                    if (Math.Abs(Number(chunk, 30)) < 1e-9)
                    {
                        if (!double.IsFinite(radius) || radius <= 0)
                            throw new InvalidDataException($"Circle {contours.Count + 1} has an invalid radius.");
                        var centerX = RequiredNumber(chunk, 10);
                        var centerY = RequiredNumber(chunk, 20);
                        if (!double.IsFinite(centerX) || !double.IsFinite(centerY))
                            throw new InvalidDataException($"Circle {contours.Count + 1} has a non-finite center.");
                        var points = Enumerable.Range(0, 96)
                            .Select(k => (centerX + radius * Math.Cos(2 * Math.PI * k / 96),
                                          centerY + radius * Math.Sin(2 * Math.PI * k / 96)))
                            .ToList();
                        AddContour(contours, $"CIRCLE {contours.Count + 1}", points);
                    }
                    break;
            }
        }

        if (contours.Count == 0)
            throw new InvalidDataException("No closed 2D polylines or circles were found in the DXF.");
        return ContourClassifier.Classify(contours);
    }

    private static List<List<Pair>> ReadEntityChunks(IReadOnlyList<Pair> pairs)
    {
        var chunks = new List<List<Pair>>();
        var inEntities = false;
        List<Pair>? current = null;
        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            if (pair.Code == 0 && pair.Value == "SECTION" && i + 1 < pairs.Count &&
                pairs[i + 1].Code == 2 && pairs[i + 1].Value == "ENTITIES")
            {
                inEntities = true;
                i++;
                continue;
            }

            if (!inEntities)
                continue;
            if (pair.Code == 0 && pair.Value == "ENDSEC")
            {
                if (current is not null)
                    chunks.Add(current);
                break;
            }
            if (pair.Code == 0)
            {
                if (current is not null)
                    chunks.Add(current);
                current = [pair];
            }
            else
            {
                current?.Add(pair);
            }
        }
        return chunks;
    }

    private static bool TryReadClosedPolyline(List<Pair> chunk, out List<(double X, double Y)> points)
    {
        points = [];
        var flags = Integer(chunk, 70);
        if ((flags & 1) == 0 || (flags & (8 | 16 | 32 | 64)) != 0 ||
            Math.Abs(Number(chunk, 38)) > 1e-9 || Math.Abs(Number(chunk, 30)) > 1e-9)
            return false;

        var vertices = new List<Vertex>();
        for (var i = 0; i < chunk.Count; i++)
        {
            if (chunk[i].Code != 10)
                continue;
            var x = ParseDouble(chunk[i].Value);
            if (i + 1 >= chunk.Count || chunk[i + 1].Code != 20)
                throw new InvalidDataException("A closed polyline vertex is missing its Y coordinate.");
            var y = ParseDouble(chunk[++i].Value);
            vertices.Add(new Vertex(x, y, 0, 0));
        }

        for (var i = 0; i < chunk.Count; i++)
        {
            if (chunk[i].Code == 42 && vertices.Count > 0)
            {
                var vertexIndex = Math.Min(vertices.Count - 1, CountCode(chunk, 10, i) - 1);
                vertices[vertexIndex] = vertices[vertexIndex] with { Bulge = ParseDouble(chunk[i].Value) };
            }
        }
        points = ExpandBulges(vertices, true);
        return true;
    }

    private static int CountCode(List<Pair> chunk, int code, int beforeIndex) =>
        chunk.Take(beforeIndex).Count(p => p.Code == code);

    private static Vertex ReadVertex(List<Pair> chunk) =>
        new(RequiredNumber(chunk, 10), RequiredNumber(chunk, 20), Number(chunk, 30), Number(chunk, 42));

    private static List<(double X, double Y)> ExpandBulges(IReadOnlyList<Vertex> vertices, bool closed)
    {
        var result = new List<(double X, double Y)>();
        if (vertices.Count < 2)
            return result;

        var edgeCount = closed ? vertices.Count : vertices.Count - 1;
        for (var i = 0; i < edgeCount; i++)
        {
            var start = vertices[i];
            var end = vertices[(i + 1) % vertices.Count];
            result.Add((start.X, start.Y));
            if (Math.Abs(start.Bulge) < 1e-9)
                continue;

            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-12)
                continue;
            var sweep = 4 * Math.Atan(start.Bulge);
            var offset = chord * (1 - start.Bulge * start.Bulge) / (4 * start.Bulge);
            var centerX = (start.X + end.X) / 2 - dy / chord * offset;
            var centerY = (start.Y + end.Y) / 2 + dx / chord * offset;
            var radius = Math.Sqrt((start.X - centerX) * (start.X - centerX) +
                                   (start.Y - centerY) * (start.Y - centerY));
            var startAngle = Math.Atan2(start.Y - centerY, start.X - centerX);
            var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 24)));
            for (var step = 1; step < steps; step++)
            {
                var angle = startAngle + sweep * step / steps;
                result.Add((centerX + radius * Math.Cos(angle), centerY + radius * Math.Sin(angle)));
            }
        }
        RemoveDuplicateClosingPoint(result);
        return result;
    }

    private static void AddContour(List<BoundaryContour> contours, string name, List<(double X, double Y)> points)
    {
        RemoveDuplicateClosingPoint(points);
        contours.Add(new BoundaryContour(name, points));
    }

    private static void RemoveDuplicateClosingPoint(List<(double X, double Y)> points)
    {
        while (points.Count > 1)
        {
            var first = points[0];
            var last = points[^1];
            if (Math.Abs(first.X - last.X) > 1e-10 || Math.Abs(first.Y - last.Y) > 1e-10)
                break;
            points.RemoveAt(points.Count - 1);
        }
    }

    private static string Value(List<Pair> pairs, int code) => pairs.FirstOrDefault(p => p.Code == code).Value ?? "";
    private static int Integer(List<Pair> pairs, int code) =>
        int.TryParse(Value(pairs, code), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static double Number(List<Pair> pairs, int code)
    {
        var text = Value(pairs, code);
        if (text.Length == 0)
            return 0;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new InvalidDataException($"Invalid numeric value for DXF group code {code}: {text}");
    }

    private static double RequiredNumber(List<Pair> pairs, int code)
    {
        if (!pairs.Any(pair => pair.Code == code))
            throw new InvalidDataException($"DXF entity is missing required group code {code}.");
        return Number(pairs, code);
    }
    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new InvalidDataException($"Invalid numeric value in DXF: {value}");
}
