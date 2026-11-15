# Plan: Support Nested Holes and Contours

## Goal

Interpret nested closed DXF boundaries as faces with holes and islands, rather
than treating every loop as a separate filled face. Preserve watertight STL
output for extrusion and supported revolutions.

## Proposed behavior

- Use the **even-odd fill rule**: an outer contour defines a solid region, a
  child contour defines a hole, and a contour nested inside that hole defines
  a solid island.
- Treat a face as one selectable item consisting of its outer contour and all
  hole boundaries directly inside it. Nested islands are separate selectable
  faces.
- Reject invalid or ambiguous geometry with a clear error instead of exporting
  a potentially misleading mesh.
- Continue supporting closed polylines, bulged segments, and circles as
  boundaries. Keep open curves and 3D entities out of scope unless separately
  requested.

## Implementation steps

1. **Represent contour hierarchy**
   - Replace or extend `Profile` with a face model containing an outer ring,
     its hole rings, and any nested child faces.
   - Import all supported closed loops before classifying them.
   - Normalize duplicate closing points and winding direction without relying
     on DXF winding to indicate holes.

2. **Classify and validate contours**
   - Build a containment tree using point-in-polygon tests; use bounding boxes
     only as a preliminary filter.
   - Classify nesting depth by parity (even depth is filled; odd depth is a
     hole).
   - Detect and report self-intersections, touching or crossing rings,
     duplicate contours, zero-area rings, and ambiguous containment.
   - Define tolerances relative to drawing scale and add diagnostics that
     identify the affected contour.

3. **Add hole-aware triangulation**
   - Replace the simple-ring triangulation path with a proven polygon
     triangulator that accepts an outer ring and multiple holes.
   - Keep triangulation behind a small geometry service so operations do not
     depend directly on a third-party API.
   - Verify output triangles stay within the filled region and do not bridge
     holes.

4. **Update extrusion**
   - Build top and bottom caps from the hole-aware triangulation.
   - Generate side walls for the outer ring and every hole ring, with
     consistent outward/inward normals.
   - Handle nested islands as separate filled components.

5. **Update revolution**
   - Sweep each outer and hole boundary to create the corresponding surfaces.
   - For partial revolutions, triangulate the start and end cross-sections with
     their holes.
   - Validate axis contact and crossing explicitly; reject profiles that
     produce self-intersecting or non-manifold results until those cases are
     implemented deliberately.

6. **Update selection and preview**
   - Display face groups rather than individual rings in the profile list.
   - Selecting a face highlights its outer boundary and holes together.
   - Show nested islands as separate selectable faces and provide useful
     import warnings for invalid contours.

7. **Verify and document**
   - Add focused tests for an extruded rectangle with a rectangular hole,
     multiple holes, a circle inside a circle, an island inside a hole,
     reversed ring winding, and partial/full revolutions.
   - Assert watertightness, positive component volume, and that hole regions
     remain empty; test invalid touching, crossing, and self-intersecting
     contours produce errors.
   - Update the README with supported nesting rules, tolerances, unsupported
     geometry, and any triangulation dependency.

## Acceptance criteria

- Nested loops are grouped deterministically regardless of DXF winding order.
- Extruded meshes preserve holes and islands and pass the watertightness check.
- Supported revolutions preserve cross-section holes and pass the same check.
- Invalid or unsupported boundary configurations fail with a specific,
  user-readable message and do not save a success-shaped STL.
- The application builds and the focused geometry/import tests pass.

## Main risks

- DXF files may contain nearly touching or duplicate boundaries; tolerance
  choices must avoid both false intersections and accidental loop merging.
- A triangulator can return topologically valid but geometrically incorrect
  caps; tests must check that triangles do not cover holes.
- Revolving profiles that meet or cross the axis can create degeneracies or
  self-intersections, so axis rules need to be explicit and enforced.
