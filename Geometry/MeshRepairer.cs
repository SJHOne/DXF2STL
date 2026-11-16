namespace Dxf2Stl.Geometry;

public static class MeshRepairer
{
    public static void Repair(TriangleMesh mesh)
    {
        if (mesh.Triangles.Count == 0)
            throw new InvalidDataException("The operation produced no triangles.");

        WeldVertices(mesh);
        RemoveInvalidTriangles(mesh);
        OrientComponents(mesh);

        var boundaries = FindBoundaryLoops(mesh);
        foreach (var loop in boundaries)
        {
            var points = loop.Select(i => mesh.Vertices[i]).ToArray();
            var normal = Vector3.Cross(points[1] - points[0], points[2] - points[0]);
            var normalLength = normal.Length;
            if (normalLength < 1e-12)
                throw new InvalidDataException("A mesh boundary is degenerate and cannot be repaired.");
            var scale = points.Max(p => (p - points[0]).Length);
            if (points.Any(p => Math.Abs(Vector3.Dot(p - points[0], normal)) / normalLength > Math.Max(scale * 1e-6, 1e-9)))
                throw new InvalidDataException("A non-planar mesh boundary cannot be repaired automatically.");

            var dropAxis = Math.Abs(normal.X) >= Math.Abs(normal.Y) && Math.Abs(normal.X) >= Math.Abs(normal.Z)
                ? 'X'
                : Math.Abs(normal.Y) >= Math.Abs(normal.Z) ? 'Y' : 'Z';
            var projected = points.Select(p => dropAxis switch
            {
                'X' => (p.Y, p.Z),
                'Y' => (p.X, p.Z),
                _ => (p.X, p.Y)
            }).ToArray();
            var cap = PolygonTriangulator.Triangulate(projected);
            for (var i = 0; i < cap.Count; i += 3)
                mesh.AddTriangle(loop[cap[i]], loop[cap[i + 1]], loop[cap[i + 2]]);
        }

        RemoveInvalidTriangles(mesh);
        OrientComponents(mesh);
        EnsureWatertight(mesh);
    }

    public static void EnsureWatertight(TriangleMesh mesh)
    {
        var counts = BuildEdgeCounts(mesh);
        var open = counts.Count(edge => edge.Value == 1);
        var nonManifold = counts.Count(edge => edge.Value > 2);
        if (open != 0 || nonManifold != 0)
            throw new InvalidDataException($"Mesh is not watertight ({open} open edges, {nonManifold} non-manifold edges).");
    }

    private static void WeldVertices(TriangleMesh mesh)
    {
        var quantum = Math.Max(Extent(mesh.Vertices) * 1e-9, 1e-12);
        var lookup = new Dictionary<(long X, long Y, long Z), int>();
        var welded = new List<Vector3>();
        var map = new int[mesh.Vertices.Count];
        for (var i = 0; i < mesh.Vertices.Count; i++)
        {
            var p = mesh.Vertices[i];
            var key = ((long)Math.Round(p.X / quantum), (long)Math.Round(p.Y / quantum), (long)Math.Round(p.Z / quantum));
            if (!lookup.TryGetValue(key, out var index))
            {
                index = welded.Count;
                lookup.Add(key, index);
                welded.Add(p);
            }
            map[i] = index;
        }

        for (var i = 0; i < mesh.Triangles.Count; i++)
        {
            var t = mesh.Triangles[i];
            mesh.Triangles[i] = (map[t.A], map[t.B], map[t.C]);
        }
        mesh.Vertices.Clear();
        mesh.Vertices.AddRange(welded);
    }

    private static void RemoveInvalidTriangles(TriangleMesh mesh)
    {
        var scale = Extent(mesh.Vertices);
        var areaTolerance = Math.Max(scale * scale * 1e-14, 1e-24);
        var clean = new List<(int A, int B, int C)>();
        var seen = new HashSet<(int, int, int)>();
        foreach (var triangle in mesh.Triangles)
        {
            if (triangle.A == triangle.B || triangle.B == triangle.C || triangle.C == triangle.A)
                continue;
            var a = mesh.Vertices[triangle.A];
            var b = mesh.Vertices[triangle.B];
            var c = mesh.Vertices[triangle.C];
            if (Vector3.Cross(b - a, c - a).Length < areaTolerance)
                continue;
            var sorted = new[] { triangle.A, triangle.B, triangle.C };
            Array.Sort(sorted);
            if (seen.Add((sorted[0], sorted[1], sorted[2])))
                clean.Add(triangle);
        }
        mesh.Triangles.Clear();
        mesh.Triangles.AddRange(clean);
    }

    private static void OrientComponents(TriangleMesh mesh)
    {
        var edges = BuildEdges(mesh);
        if (edges.Values.Any(items => items.Count > 2))
            throw new InvalidDataException("Mesh contains non-manifold edges and cannot be repaired automatically.");

        var adjacency = Enumerable.Range(0, mesh.Triangles.Count)
            .Select(_ => new List<(int Other, bool MustFlip)>()).ToArray();
        foreach (var edge in edges.Values.Where(items => items.Count == 2))
        {
            var first = edge[0];
            var second = edge[1];
            var mustFlip = first.Forward == second.Forward;
            adjacency[first.Triangle].Add((second.Triangle, mustFlip));
            adjacency[second.Triangle].Add((first.Triangle, mustFlip));
        }

        var assigned = new bool[mesh.Triangles.Count];
        var flip = new bool[mesh.Triangles.Count];
        var components = new List<List<int>>();
        for (var seed = 0; seed < mesh.Triangles.Count; seed++)
        {
            if (assigned[seed])
                continue;
            var component = new List<int>();
            var queue = new Queue<int>();
            assigned[seed] = true;
            queue.Enqueue(seed);
            while (queue.TryDequeue(out var current))
            {
                component.Add(current);
                foreach (var (other, mustFlip) in adjacency[current])
                {
                    var expected = flip[current] ^ mustFlip;
                    if (!assigned[other])
                    {
                        assigned[other] = true;
                        flip[other] = expected;
                        queue.Enqueue(other);
                    }
                    else if (flip[other] != expected)
                    {
                        throw new InvalidDataException("Mesh has inconsistent face orientation and cannot be repaired.");
                    }
                }
            }
            components.Add(component);
        }

        for (var i = 0; i < flip.Length; i++)
            if (flip[i])
                Flip(mesh, i);

        foreach (var component in components)
        {
            var origin = mesh.Vertices[mesh.Triangles[component[0]].A];
            var volume = component.Sum(index =>
            {
                var t = mesh.Triangles[index];
                var a = mesh.Vertices[t.A] - origin;
                var b = mesh.Vertices[t.B] - origin;
                var c = mesh.Vertices[t.C] - origin;
                return Vector3.Dot(a, Vector3.Cross(b, c)) / 6;
            });
            if (volume < 0)
                foreach (var index in component)
                    Flip(mesh, index);
        }
    }

    private static List<List<int>> FindBoundaryLoops(TriangleMesh mesh)
    {
        var boundaryEdges = BuildEdges(mesh).Where(e => e.Value.Count == 1).Select(e => e.Key).ToList();
        var neighbors = new Dictionary<int, List<int>>();
        foreach (var (a, b) in boundaryEdges)
        {
            if (!neighbors.TryGetValue(a, out var listA))
                neighbors[a] = listA = [];
            if (!neighbors.TryGetValue(b, out var listB))
                neighbors[b] = listB = [];
            listA.Add(b);
            listB.Add(a);
        }

        if (neighbors.Values.Any(list => list.Count != 2))
            throw new InvalidDataException("Open mesh boundaries could not be repaired into closed loops.");

        var remaining = new HashSet<(int, int)>(boundaryEdges);
        var loops = new List<List<int>>();
        while (remaining.Count > 0)
        {
            var first = remaining.First();
            var loop = new List<int> { first.Item1 };
            var previous = first.Item1;
            var current = first.Item2;
            remaining.Remove(first);
            while (current != loop[0])
            {
                loop.Add(current);
                var next = neighbors[current].First(v => v != previous);
                remaining.Remove((Math.Min(current, next), Math.Max(current, next)));
                previous = current;
                current = next;
                if (loop.Count > boundaryEdges.Count)
                    throw new InvalidDataException("Open mesh boundary repair did not converge.");
            }
            if (loop.Count < 3)
                throw new InvalidDataException("A mesh boundary is too small to repair.");
            loops.Add(loop);
        }
        return loops;
    }

    private static Dictionary<(int A, int B), List<(int Triangle, bool Forward)>> BuildEdges(TriangleMesh mesh)
    {
        var edges = new Dictionary<(int A, int B), List<(int Triangle, bool Forward)>>();
        for (var i = 0; i < mesh.Triangles.Count; i++)
        {
            var t = mesh.Triangles[i];
            Add(t.A, t.B, i);
            Add(t.B, t.C, i);
            Add(t.C, t.A, i);
        }
        return edges;

        void Add(int from, int to, int triangle)
        {
            var key = (Math.Min(from, to), Math.Max(from, to));
            if (!edges.TryGetValue(key, out var list))
                edges[key] = list = [];
            list.Add((triangle, from < to));
        }
    }

    private static Dictionary<(int A, int B), int> BuildEdgeCounts(TriangleMesh mesh) =>
        BuildEdges(mesh).ToDictionary(edge => edge.Key, edge => edge.Value.Count);

    private static double Extent(IReadOnlyList<Vector3> points)
    {
        if (points.Count == 0)
            return 1;
        return Math.Max(
            Math.Max(points.Max(point => point.X) - points.Min(point => point.X),
                     points.Max(point => point.Y) - points.Min(point => point.Y)),
            points.Max(point => point.Z) - points.Min(point => point.Z));
    }

    private static void Flip(TriangleMesh mesh, int index)
    {
        var t = mesh.Triangles[index];
        mesh.Triangles[index] = (t.A, t.C, t.B);
    }
}
