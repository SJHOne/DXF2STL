using Dxf2Stl.Export;
using Dxf2Stl.Geometry;
using Dxf2Stl.Operations;
using Xunit;

namespace Dxf2Stl.Tests;

public sealed class StlWriterTests
{
    [Fact]
    public void WritesPreparedMeshWithoutMutatingPreviewGeometry()
    {
        var profile = new Profile("square", [(0d, 0d), (2d, 0d), (2d, 2d), (0d, 2d)], []);
        var mesh = new ExtrudeOperation().Build([profile], new OperationSettings(3));
        MeshRepairer.Repair(mesh);
        var vertices = mesh.Vertices.ToArray();
        var triangles = mesh.Triangles.ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"dxf2stl-{Guid.NewGuid():N}.stl");

        try
        {
            StlWriter.WritePreparedBinary(path, mesh);

            Assert.Equal(84 + triangles.Length * 50, new FileInfo(path).Length);
            Assert.Equal(vertices, mesh.Vertices);
            Assert.Equal(triangles, mesh.Triangles);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
