using System.IO;
using System.Text;
using Dxf2Stl.Geometry;

namespace Dxf2Stl.Export;

public static class StlWriter
{
    public static void WriteBinary(string path, TriangleMesh mesh)
    {
        MeshRepairer.Repair(mesh);
        WriteMesh(path, mesh);
    }

    public static void WritePreparedBinary(string path, TriangleMesh mesh)
    {
        MeshRepairer.EnsureWatertight(mesh);
        WriteMesh(path, mesh);
    }

    private static void WriteMesh(string path, TriangleMesh mesh)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        var header = new byte[80];
        Encoding.ASCII.GetBytes("DXF2STL watertight mesh").CopyTo(header, 0);
        writer.Write(header);
        writer.Write((uint)mesh.Triangles.Count);
        foreach (var triangle in mesh.Triangles)
        {
            var a = mesh.Vertices[triangle.A];
            var b = mesh.Vertices[triangle.B];
            var c = mesh.Vertices[triangle.C];
            var normal = Vector3.Cross(b - a, c - a);
            var length = normal.Length;
            if (length > 0)
                normal *= 1 / length;
            WriteVector(writer, normal);
            WriteVector(writer, a);
            WriteVector(writer, b);
            WriteVector(writer, c);
            writer.Write((ushort)0);
        }
    }

    private static void WriteVector(BinaryWriter writer, Vector3 value)
    {
        writer.Write((float)value.X);
        writer.Write((float)value.Y);
        writer.Write((float)value.Z);
    }
}
