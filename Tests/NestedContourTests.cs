using Dxf2Stl.Geometry;
using Dxf2Stl.Import;
using Dxf2Stl.Operations;
using Xunit;

namespace Dxf2Stl.Tests;

public sealed class NestedContourTests
{
    [Fact]
    public void ExtrusionPreservesRectangularHoleAndMultipleHoles()
    {
        var face = Classify(
            Ring("outer", Rectangle(0, 0, 10, 10)),
            Ring("hole-a", Rectangle(2, 2, 4, 4)),
            Ring("hole-b", Rectangle(6, 6, 8, 8))).Single();

        var mesh = new ExtrudeOperation().Build([face], new OperationSettings(3));
        MeshRepairer.Repair(mesh);

        Assert.Equal(2, face.Holes.Count);
        Assert.Equal(276, Math.Round(Volume(mesh), 6));
        MeshRepairer.EnsureWatertight(mesh);
    }

    [Fact]
    public void NegativeExtrusionPreservesHoles()
    {
        var face = Classify(
            Ring("outer", Rectangle(0, 0, 10, 10)),
            Ring("hole", Rectangle(2, 2, 8, 8))).Single();

        var mesh = new ExtrudeOperation().Build([face], new OperationSettings(-2));
        MeshRepairer.Repair(mesh);

        Assert.Equal(128, Math.Round(Volume(mesh), 6));
        MeshRepairer.EnsureWatertight(mesh);
    }

    [Fact]
    public void CircleInsideCircleCreatesHollowExtrusion()
    {
        var face = Classify(
            Ring("outer circle", Circle(0, 0, 5)),
            Ring("inner circle", Circle(0, 0, 2))).Single();

        var mesh = new ExtrudeOperation().Build([face], new OperationSettings(2));
        MeshRepairer.Repair(mesh);

        Assert.Single(face.Holes);
        Assert.InRange(Volume(mesh), 2 * Math.PI * (25 - 4) * 0.99, 2 * Math.PI * (25 - 4) * 1.01);
    }

    [Fact]
    public void IslandInsideHoleIsSeparateFace()
    {
        var faces = Classify(
            Ring("outer", Rectangle(0, 0, 10, 10)),
            Ring("hole", Rectangle(2, 2, 8, 8)),
            Ring("island", Rectangle(4, 4, 6, 6)));

        Assert.Equal(2, faces.Count);
        Assert.Single(faces[0].Holes);
        Assert.Equal("Face 2", faces[1].Name);
        Assert.Same(faces[1], Assert.Single(faces[0].Children));

        var mesh = new ExtrudeOperation().Build(faces, new OperationSettings(2));
        MeshRepairer.Repair(mesh);
        Assert.Equal(136, Math.Round(Volume(mesh), 6));
    }

    [Fact]
    public void ClassificationIgnoresInputWinding()
    {
        var outer = Rectangle(0, 0, 10, 10).Reverse().ToArray();
        var hole = Rectangle(2, 2, 8, 8);
        var face = Classify(Ring("outer", outer), Ring("hole", hole)).Single();

        Assert.True(PolygonTriangulator.SignedArea(face.Points) > 0);
        Assert.True(PolygonTriangulator.SignedArea(face.Holes[0]) < 0);
        Assert.Equal(64, Math.Round(TriangulatedArea(face), 6));
    }

    [Fact]
    public void ClassificationAndTriangulationAreStableAtLargeCoordinateOffsets()
    {
        var face = Classify(
            Ring("outer", Rectangle(1_000_000_000, 1_000_000_000, 1_000_000_010, 1_000_000_010)),
            Ring("hole", Rectangle(1_000_000_002, 1_000_000_002, 1_000_000_008, 1_000_000_008))).Single();

        Assert.Equal(64, Math.Round(TriangulatedArea(face), 6));
        var mesh = new ExtrudeOperation().Build([face], new OperationSettings(2));
        MeshRepairer.Repair(mesh);
        Assert.Equal(128, Math.Round(Volume(mesh), 6));
    }

    [Theory]
    [InlineData(180)]
    [InlineData(360)]
    public void RevolutionPreservesHoleAndWatertightness(double angle)
    {
        var face = Classify(
            Ring("outer", Rectangle(2, 0, 8, 6)),
            Ring("hole", Rectangle(4, 2, 6, 4))).Single();

        var mesh = new RevolveOperation().Build([face], new OperationSettings(angle, 'Y', 24));
        MeshRepairer.Repair(mesh);

        Assert.True(Volume(mesh) > 0);
        MeshRepairer.EnsureWatertight(mesh);
    }

    [Fact]
    public void ReaderGroupsNestedAsciiDxfLoops()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dxf2stl-{Guid.NewGuid():N}.dxf");
        try
        {
            File.WriteAllText(path, MakeDxf(
                PolylineEntity(Rectangle(0, 0, 10, 10)),
                PolylineEntity(Rectangle(2, 2, 8, 8))));

            var face = new DxfProfileReader().Read(path).Single();

            Assert.Single(face.Holes);
            Assert.Contains("hole", face.Name);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ReaderGroupsNestedCircles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dxf2stl-{Guid.NewGuid():N}.dxf");
        try
        {
            File.WriteAllText(path, MakeDxf(CircleEntity(0, 0, 5), CircleEntity(0, 0, 2)));

            var face = new DxfProfileReader().Read(path).Single();

            Assert.Single(face.Holes);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Theory]
    [InlineData("touching")]
    [InlineData("crossing")]
    public void RejectsTouchingOrCrossingContours(string kind)
    {
        var second = kind == "touching"
            ? Rectangle(10, 2, 12, 4)
            : Rectangle(8, 4, 12, 6);

        var error = Assert.Throws<InvalidDataException>(() =>
            Classify(Ring("first", Rectangle(0, 0, 10, 10)), Ring("second", second)));

        Assert.Contains("touch, cross, or duplicate", error.Message);
    }

    [Fact]
    public void RejectsSelfIntersectingContour()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            Classify(Ring("bow tie", [(0, 0), (4, 4), (0, 4), (4, 0)])));

        Assert.Contains("Contour 'bow tie'", error.Message);
    }

    [Fact]
    public void RevolutionRejectsAxisContact()
    {
        var face = Classify(Ring("axis touch", Rectangle(0, 1, 4, 3))).Single();

        var error = Assert.Throws<InvalidDataException>(() =>
            new RevolveOperation().Build([face], new OperationSettings(360, 'Y')));

        Assert.Contains("touches or crosses the Y-axis", error.Message);
    }

    [Fact]
    public void RevolutionRejectsAxisCrossing()
    {
        var face = Classify(Ring("axis crossing", Rectangle(-1, 1, 1, 3))).Single();

        var error = Assert.Throws<InvalidDataException>(() =>
            new RevolveOperation().Build([face], new OperationSettings(360, 'Y')));

        Assert.Contains("touches or crosses the Y-axis", error.Message);
    }

    private static IReadOnlyList<Profile> Classify(params BoundaryContour[] contours) =>
        ContourClassifier.Classify(contours);

    private static BoundaryContour Ring(string name, IReadOnlyList<(double X, double Y)> points) =>
        new(name, points);

    private static (double X, double Y)[] Rectangle(double minX, double minY, double maxX, double maxY) =>
        [(minX, minY), (maxX, minY), (maxX, maxY), (minX, maxY)];

    private static (double X, double Y)[] Circle(double centerX, double centerY, double radius) =>
        Enumerable.Range(0, 96)
            .Select(index => (centerX + radius * Math.Cos(2 * Math.PI * index / 96),
                              centerY + radius * Math.Sin(2 * Math.PI * index / 96)))
            .ToArray();

    private static double Volume(TriangleMesh mesh)
    {
        var origin = mesh.Vertices[0];
        return mesh.Triangles.Sum(triangle =>
        {
            var a = mesh.Vertices[triangle.A] - origin;
            var b = mesh.Vertices[triangle.B] - origin;
            var c = mesh.Vertices[triangle.C] - origin;
            return Vector3.Dot(a, Vector3.Cross(b, c)) / 6;
        });
    }

    private static double TriangulatedArea(Profile profile)
    {
        var rings = new[] { profile.Points }.Concat(profile.Holes).ToArray();
        var coordinates = rings.SelectMany(ring => ring.SelectMany(point => new[] { point.X, point.Y })).ToArray();
        var triangles = PolygonTriangulator.Triangulate(profile);
        var area = 0.0;
        for (var i = 0; i < triangles.Count; i += 3)
        {
            (double X, double Y) Point(int index) => (coordinates[index * 2], coordinates[index * 2 + 1]);
            var a = Point(triangles[i]);
            var b = Point(triangles[i + 1]);
            var c = Point(triangles[i + 2]);
            area += Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) / 2;
        }
        return area;
    }

    private static string MakeDxf(params string[] entities) =>
        "0\nSECTION\n2\nENTITIES\n" + string.Concat(entities) + "0\nENDSEC\n0\nEOF\n";

    private static string PolylineEntity(IReadOnlyList<(double X, double Y)> points)
    {
        var lines = "0\nLWPOLYLINE\n70\n1\n";
        foreach (var point in points)
            lines += $"10\n{point.X}\n20\n{point.Y}\n";
        return lines;
    }

    private static string CircleEntity(double centerX, double centerY, double radius) =>
        $"0\nCIRCLE\n10\n{centerX}\n20\n{centerY}\n30\n0\n40\n{radius}\n";
}
