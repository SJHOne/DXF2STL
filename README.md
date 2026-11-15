# DXF2STL

DXF2STL is a Windows desktop application that imports supported 2D DXF
boundaries, creates 3D meshes, checks and repairs the meshes, and exports
binary STL files.

## Requirements and running

- Windows
- .NET 8 SDK

From this directory, launch the WPF application with:

```powershell
dotnet run --project .\DXF2STL.csproj
```

To build without launching it:

```powershell
dotnet build .\DXF2STL.csproj
```

Open a DXF from the application. The preview shows the imported profiles;
Ctrl-click or Shift-click to select multiple profiles for extrusion. Choose an
operation, enter its distance or angle, and export an STL file.

## Implemented functionality

### DXF import and preview

The importer reads **ASCII DXF** and recognizes:

- Closed 2D `LWPOLYLINE` entities, including bulged arc segments (tessellated
  during import).
- Closed 2D `POLYLINE` entities.
- `CIRCLE` entities (approximated as 96 straight segments).

Imported contours appear in a list and a 2D preview. The importer does not
currently read binary DXF, open curves, 3D entities, blocks, or nested contour
relationships. Each closed contour is currently considered an independent
filled profile.

### Extrusion

Select one or more profiles, enter a non-zero extrusion distance, and export.
The profiles are extruded from Z=0 by the entered distance; a negative distance
extrudes in the negative Z direction. Concave simple polygons are supported.

### Revolution

The revolution operation acts on **all loaded profiles** and sweeps them around
the selected origin axis:

- **Y** is the default axis; **X** is also available.
- The angle must be non-zero and within -360 to 360 degrees.
- Use 360 degrees for a full turn. Partial turns receive start and end caps.
- Z is intentionally unavailable: revolving an XY profile around Z leaves it
  flat and does not create a printable 3D solid.

Profiles that cross an axis or otherwise create self-intersecting geometry may
not form valid printable solids. Such input should be validated before use.

### Mesh processing and STL export

The operations create a triangle mesh. Before writing the binary STL, the mesh
pipeline:

1. Welds coincident vertices within a scale-relative tolerance.
2. Removes degenerate and duplicate triangles.
3. Makes triangle winding consistent and orients components.
4. Attempts to close simple, planar boundary loops by triangulating the loop.
5. Checks that every mesh edge belongs to exactly two triangles; export fails
   with an error if the check does not pass.

This boundary-loop repair is for gaps in an output mesh; it does **not** make
nested DXF contours into designed holes. The topology check also does not
guarantee the absence of all geometric self-intersections.

DXF coordinates are written unchanged. STL does not encode units; DXF2STL
assumes downstream slicers interpret the coordinates as millimetres. Scale the
input appropriately before export if its coordinates use another unit.

## Architecture and source map

The application targets `net8.0-windows` and uses WPF for its interface. It has
no third-party package dependencies.

| Area | Source |
| --- | --- |
| WPF app startup and window | `App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs` |
| ASCII DXF parsing and profile extraction | `Import/DxfProfileReader.cs` |
| Profile and vector types | `Geometry/Vector3.cs` |
| Polygon triangulation | `Geometry/PolygonTriangulator.cs` |
| Triangle mesh representation | `Geometry/TriangleMesh.cs` |
| Mesh repair and watertightness validation | `Geometry/MeshRepairer.cs` |
| Operation contract and settings | `Operations/IProfileOperation.cs` |
| Extrusion and revolution | `Operations/ExtrudeOperation.cs`, `Operations/RevolveOperation.cs` |
| Binary STL writer | `Export/StlWriter.cs` |

`IProfileOperation` is the common contract for mesh-building operations. Each
implementation receives profiles and `OperationSettings` and returns a
`TriangleMesh`. The available operations are registered in the `_operations`
collection in `MainWindow`.

## Extending functionality

### Add a mesh operation

1. Add a class under `Operations` that implements `IProfileOperation`.
2. Give the operation a user-facing `Name`.
3. Validate its inputs and build a `TriangleMesh`; report invalid or unsupported
   geometry with a clear exception rather than returning an empty mesh.
4. Register the implementation in `MainWindow`'s `_operations` collection so
   it appears in the operation selector.
5. Update the UI handling in `MainWindow.xaml.cs` if the operation requires
   different parameters, a different axis, or different profile-selection
   behavior. The current UI distinguishes extrusion from revolution directly,
   so registration alone is sufficient only for operations compatible with
   the existing shared amount/axis controls and input selection.
6. Add focused tests for geometry, mesh closure, and exported STL structure.

For a larger plugin system, replace the current operation-specific UI checks
with operation-provided parameter metadata and a consistent selection policy.
Keep mesh validation and STL writing shared across operations.

### Add DXF entities or nested contours

Extend `DxfProfileReader` to parse the desired entities into normalized contour
data, then keep import separate from face classification and meshing. Nested
contours require grouping rings into faces, determining containment independent
of winding, validating touching/crossing boundaries, and using a
hole-aware triangulator for caps. Extrusion walls must follow both outer and
hole rings; revolution also needs explicit handling for rings that meet or cross
the axis.

The proposed implementation steps and acceptance criteria are in
[`NESTED_CONTOURS_PLAN.md`](NESTED_CONTOURS_PLAN.md). Nested holes are planned,
not currently implemented.

## Verification

The project has been built successfully with `dotnet build`. During development,
temporary smoke checks exercised DXF profile loading, concave extrusion,
full and partial Y-axis revolutions, a partial X-axis revolution,
watertightness, and binary STL length.
Those temporary checks were removed; a persistent automated test project has
not yet been added.
