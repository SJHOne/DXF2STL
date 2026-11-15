using System.Globalization;
using Dxf2Stl.Geometry;

namespace Dxf2Stl.Import;

public sealed class DxfProfileReader
{
    private readonly record struct Pair(int Code, string Value);
    private readonly record struct Vertex(double X, double Y, double Bulge);

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
        var profiles = new List<Profile>();
        for (var i = 0; i < entities.Count; i++)
        {
            var chunk = entities[i];
            switch (Value(chunk, 0))
            {
                case "LWPOLYLINE":
                    if (TryReadClosedPolyline(chunk, out var lightPoints))
                        AddProfile(profiles, $"Polyline {profiles.Count + 1}", lightPoints);
                    break;
                case "POLYLINE":
                    var vertices = new List<Vertex>();
                    var j = i + 1;
                    while (j < entities.Count && Value(entities[j], 0) == "VERTEX")
                        vertices.Add(ReadVertex(entities[j++]));
                    i = j < entities.Count && Value(entities[j], 0) == "SEQEND" ? j : j - 1;
                    if ((Integer(chunk, 70) & 1) != 0 && Math.Abs(Number(chunk, 30)) < 1e-9)
                        AddProfile(profiles, $"Polyline {profiles.Count + 1}", ExpandBulges(vertices, true));
                    break;
                case "CIRCLE":
                    var radius = Number(chunk, 40);
                    if (radius > 0 && Math.Abs(Number(chunk, 30)) < 1e-9)
                    {
                        var centerX = Number(chunk, 10);
                        var centerY = Number(chunk, 20);
                        var points = Enumerable.Range(0, 96)
                            .Select(k => (centerX + radius * Math.Cos(2 * Math.PI * k / 96),
                                          centerY + radius * Math.Sin(2 * Math.PI * k / 96)))
                            .ToList();
                        AddProfile(profiles, $"Circle {profiles.Count + 1}", points);
                    }
                    break;
            }
        }

        if (profiles.Count == 0)
            throw new InvalidDataException("No closed 2D polylines or circles were found in the DXF.");
        return profiles;
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
        if ((Integer(chunk, 70) & 1) == 0 || Math.Abs(Number(chunk, 38)) > 1e-9 || Math.Abs(Number(chunk, 30)) > 1e-9)
            return false;

        var vertices = new List<Vertex>();
        for (var i = 0; i < chunk.Count; i++)
        {
            if (chunk[i].Code != 10)
                continue;
            var x = ParseDouble(chunk[i].Value);
            var y = i + 1 < chunk.Count && chunk[i + 1].Code == 20
                ? ParseDouble(chunk[++i].Value)
                : 0;
            vertices.Add(new Vertex(x, y, 0));
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
        return points.Count >= 3;
    }

    private static int CountCode(List<Pair> chunk, int code, int beforeIndex) =>
        chunk.Take(beforeIndex).Count(p => p.Code == code);

    private static Vertex ReadVertex(List<Pair> chunk) =>
        new(Number(chunk, 10), Number(chunk, 20), Number(chunk, 42));

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

    private static void AddProfile(List<Profile> profiles, string name, List<(double X, double Y)> points)
    {
        RemoveDuplicateClosingPoint(points);
        if (points.Count >= 3 && Math.Abs(PolygonTriangulator.SignedArea(points)) > 1e-12)
            profiles.Add(new Profile(name, points));
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
    private static double Number(List<Pair> pairs, int code) =>
        double.TryParse(Value(pairs, code), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new InvalidDataException($"Invalid numeric value in DXF: {value}");
}
