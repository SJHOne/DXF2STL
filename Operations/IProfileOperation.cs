using Dxf2Stl.Geometry;

namespace Dxf2Stl.Operations;

public sealed record OperationSettings(double Amount, char Axis = 'Y', int Segments = 96);

public interface IProfileOperation
{
    string Name { get; }
    TriangleMesh Build(IReadOnlyList<Profile> profiles, OperationSettings settings);
}
