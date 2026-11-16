# Plan: Add an Interactive 3D STL Preview

## Goal

Add a second tab beside the existing 2D DXF preview that displays the 3D mesh
produced by the selected operation. Let users inspect the model before saving
and interact with the view using mouse rotation and zoom.

## Current application behavior

- `MainWindow.xaml` currently has one 2D preview canvas alongside the profile
  list and operation controls.
- `MainWindow.Export_Click` builds a `TriangleMesh` from the selected operation
  and profiles, then writes it through `StlWriter.WriteBinary`.
- `StlWriter.WriteBinary` repairs the mesh in place before writing it.
- The mesh uses indexed `Vector3` vertices and triangle indices, so it can be
  converted directly to WPF 3D geometry without reading an exported STL file.
- The application already uses WPF and has no 3D rendering package dependency.

## Proposed behavior

- Replace the single preview group with a `TabControl` containing the existing
  **2D preview** and a new **3D preview** tab.
- Preview the mesh built from the current operation, amount, axis, and
  applicable profile selection. Preserve the existing operation-specific
  selection rules: extrusion uses selected profiles; revolution uses all
  loaded profiles.
- Update the 3D preview when its inputs change. When there is no valid mesh,
  show a helpful empty or error state rather than a stale model.
- Use WPF `Viewport3D` and a `PerspectiveCamera`; avoid adding a rendering
  package unless native WPF proves insufficient.
- Support left-button drag to orbit around the model and mouse-wheel zoom.
  Provide a **Fit/Reset view** action and constrain camera elevation and zoom to
  avoid flipping or losing the model.
- Fit the camera to the mesh bounds, center the model, and handle empty or
  near-zero-sized bounds safely.

## Implementation steps

1. **Separate mesh preparation from export**
   - Extract the existing operation/profile/amount validation and mesh-building
     logic so the preview and export use the same path.
   - Ensure the mesh is prepared and repaired once before it is displayed or
     exported. Since `StlWriter.WriteBinary` currently repairs its input in
     place, adjust the shared flow or writer boundary so viewing a mesh and
     exporting it cannot produce different geometry or mutate it unexpectedly.
   - Keep existing validation messages and export behavior intact.

2. **Add the tabbed preview UI**
   - Wrap the current 2D preview in a tab and add a 3D tab with a named
     `Viewport3D`.
   - Add a small fit/reset control and concise mouse interaction hints.
   - Keep profile selection, operation controls, window resizing, and the
     existing 2D preview behavior unchanged.

3. **Convert the mesh to WPF geometry**
   - Map mesh vertices to `Point3D` and triangle indices to `Int32Collection`.
   - Create a lit material and suitable camera/lights so front and back faces
     remain inspectable.
   - Rebuild or update the visual when the prepared mesh changes, without
     retaining obsolete model visuals.

4. **Implement camera interaction**
   - Left-drag orbits the camera around the mesh center; mouse wheel zooms.
   - Capture/release the mouse reliably when dragging begins, ends, or leaves
     the viewport.
   - Clamp orbit elevation and camera distance, and make fit/reset restore a
     useful default view based on current model bounds.

5. **Connect updates and errors**
   - Refresh the mesh preview after DXF load, profile selection, operation or
     axis changes, and amount changes.
   - Avoid recalculating on every keystroke if that causes noticeable lag;
     use a small debounce or update on validation/focus change as appropriate.
   - Surface invalid parameters or unsupported geometry clearly and prevent an
     old preview from appearing as if it represented the current settings.

6. **Verify and document**
   - Build the WPF application and run the existing test suite.
   - Add focused tests for any extracted mesh-building/preparation logic,
     especially selection policy and invalid parameters.
   - Manually verify extrusion and revolution previews, profile selection,
     fit/reset, drag orbit, wheel zoom, empty state, resize, and export parity.
   - Update the README to describe the 3D preview and its mouse controls.

## Acceptance criteria

- The interface presents the original 2D preview and a separate 3D preview tab.
- The 3D view shows the same generated mesh that is written to STL for the
  current operation and inputs.
- Updating the operation inputs or profile selection refreshes the preview;
  invalid or absent input never leaves a misleading stale mesh.
- Users can rotate with left-button drag, zoom with the wheel, and restore a
  fitted view.
- The model stays centered and visible at practical drawing scales, including
  empty and degenerate cases.
- Existing import, selection, operation, and STL export workflows continue to
  work.

## Risks and decisions

- **Mesh preparation ownership:** `StlWriter` currently mutates the mesh during
  repair. Establish one clear preparation boundary to prevent preview/export
  differences or repeated repair.
- **Preview cost:** Large meshes may make rebuilding a WPF `MeshGeometry3D`
  expensive. Measure with representative models before introducing caching or
  more complex rendering infrastructure.
- **WPF interaction:** Camera orbit and zoom need explicit limits and robust
  mouse capture to prevent disorienting or stuck interactions.
- **UI responsiveness:** Mesh generation may be substantial for large inputs.
  Keep the initial implementation synchronous unless measurements show UI
  blocking; if background work is needed, guard against applying stale results
  after inputs change.
