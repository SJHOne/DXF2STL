using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using Dxf2Stl.Export;
using Dxf2Stl.Geometry;
using Dxf2Stl.Import;
using Dxf2Stl.Operations;

namespace Dxf2Stl;

public partial class MainWindow : Window
{
    private readonly DxfProfileReader _reader = new();
    private readonly IReadOnlyList<IProfileOperation> _operations =
    [
        new ExtrudeOperation(),
        new RevolveOperation()
    ];
    private readonly DispatcherTimer _previewTimer = new(DispatcherPriority.Background);
    private IReadOnlyList<Profile> _profiles = [];
    private TriangleMesh? _previewMesh;
    private bool _isOrbiting;
    private Point _lastOrbitPosition;
    private double _cameraAzimuth = -35;
    private double _cameraElevation = 25;
    private double _modelRadius = 1;
    private double _cameraDistance = 3;

    public MainWindow()
    {
        InitializeComponent();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(250);
        _previewTimer.Tick += (_, _) => RefreshMeshPreview();
        OperationCombo.ItemsSource = _operations;
        OperationCombo.SelectedIndex = 0;
        UpdateCamera();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open DXF",
            Filter = "DXF files (*.dxf)|*.dxf|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            _profiles = _reader.Read(dialog.FileName);
            ProfileList.ItemsSource = _profiles;
            ProfileList.SelectedItems.Clear();
            FileNameText.Text = dialog.FileName;
            var holeCount = _profiles.Sum(profile => profile.Holes.Count);
            StatusText.Text = $"{_profiles.Count} face(s) loaded with {holeCount} hole boundary/boundaries. Select face(s) to extrude.";
            DrawPreview();
            ScheduleMeshPreview();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _profiles = [];
            ProfileList.ItemsSource = null;
            FileNameText.Text = "No DXF loaded";
            DrawPreview();
            ScheduleMeshPreview();
            MessageBox.Show(this, ex.Message, "DXF import failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "DXF import failed.";
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0 || OperationCombo.SelectedItem is not IProfileOperation operation)
        {
            MessageBox.Show(this, "Open a DXF before exporting.", "Nothing to export",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!double.TryParse(AmountText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var amount) ||
            !double.IsFinite(amount))
        {
            MessageBox.Show(this, "Enter a valid number for the operation amount.", "Invalid amount",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var isExtrude = operation is ExtrudeOperation;
        var selectedProfiles = isExtrude
            ? ProfileList.SelectedItems.Cast<Profile>().ToArray()
            : _profiles;
        if (isExtrude && selectedProfiles.Count == 0)
        {
            MessageBox.Show(this, "Select one or more closed faces to extrude.", "No faces selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "Export watertight STL",
            Filter = "Binary STL files (*.stl)|*.stl",
            DefaultExt = ".stl",
            AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(FileNameText.Text) + ".stl"
        };
        if (saveDialog.ShowDialog(this) != true)
            return;

        try
        {
            RefreshMeshPreview();
            if (_previewMesh is null)
            {
                MessageBox.Show(this, Preview3DStatusText.Text, "STL preview unavailable",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StlWriter.WritePreparedBinary(saveDialog.FileName, _previewMesh);
            var message = $"Saved {_previewMesh.Triangles.Count:N0} triangles. Mesh checked and watertight.";
            StatusText.Text = message;
            MessageBox.Show(this, $"{message}\n\n{saveDialog.FileName}", "STL export complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "STL export failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "STL export failed.";
        }
    }

    private void OperationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AmountLabel is null || OperationCombo.SelectedItem is not IProfileOperation operation)
            return;
        var isRevolve = operation is RevolveOperation;
        AmountLabel.Text = isRevolve ? "Angle (degrees)" : "Extrusion distance";
        AxisLabel.Visibility = isRevolve ? Visibility.Visible : Visibility.Collapsed;
        AxisCombo.Visibility = isRevolve ? Visibility.Visible : Visibility.Collapsed;
        if (StatusText is not null)
            StatusText.Text = isRevolve
                ? "Revolves all faces around X or Y; Z is unavailable because this XY input would remain flat. Use 360 degrees for a full turn."
                : "Select one or more faces, then extrude them by the entered distance.";
        ScheduleMeshPreview();
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DrawPreview();
        ScheduleMeshPreview();
    }

    private void AmountText_TextChanged(object sender, TextChangedEventArgs e) => ScheduleMeshPreview();

    private void AxisCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ScheduleMeshPreview();

    private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPreview();

    private void ScheduleMeshPreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void RefreshMeshPreview()
    {
        _previewTimer.Stop();
        _previewMesh = null;
        Preview3DModelVisual.Content = null;

        if (_profiles.Count == 0)
        {
            SetPreviewMessage("Load a DXF to preview a 3D mesh.");
            return;
        }

        if (OperationCombo.SelectedItem is not IProfileOperation operation)
        {
            SetPreviewMessage("Choose an operation to preview a 3D mesh.");
            return;
        }

        if (!double.TryParse(AmountText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var amount) ||
            !double.IsFinite(amount))
        {
            SetPreviewMessage("Enter a valid operation amount to preview the mesh.");
            return;
        }

        var profiles = operation is ExtrudeOperation
            ? ProfileList.SelectedItems.Cast<Profile>().ToArray()
            : _profiles;
        if (profiles.Count == 0)
        {
            SetPreviewMessage("Select one or more closed faces to preview the extrusion.");
            return;
        }

        var axis = AxisCombo.SelectedItem is ComboBoxItem item && item.Content is string axisText
            ? axisText[0]
            : 'Y';

        try
        {
            var mesh = operation.Build(profiles, new OperationSettings(amount, axis));
            MeshRepairer.Repair(mesh);
            _previewMesh = mesh;
            DisplayMesh(mesh);
            Preview3DStatusText.Visibility = Visibility.Collapsed;
        }
        catch (InvalidDataException ex)
        {
            SetPreviewMessage($"3D preview unavailable: {ex.Message}");
        }
    }

    private void SetPreviewMessage(string message)
    {
        Preview3DStatusText.Text = message;
        Preview3DStatusText.Visibility = Visibility.Visible;
    }

    private void DisplayMesh(TriangleMesh mesh)
    {
        var minX = mesh.Vertices.Min(point => point.X);
        var maxX = mesh.Vertices.Max(point => point.X);
        var minY = mesh.Vertices.Min(point => point.Y);
        var maxY = mesh.Vertices.Max(point => point.Y);
        var minZ = mesh.Vertices.Min(point => point.Z);
        var maxZ = mesh.Vertices.Max(point => point.Z);
        var centerX = minX + (maxX - minX) / 2;
        var centerY = minY + (maxY - minY) / 2;
        var centerZ = minZ + (maxZ - minZ) / 2;
        var sizeX = maxX - minX;
        var sizeY = maxY - minY;
        var sizeZ = maxZ - minZ;
        _modelRadius = Math.Max(Math.Sqrt(sizeX * sizeX + sizeY * sizeY + sizeZ * sizeZ) / 2, 1e-9);
        FitCamera();

        var positions = new Point3DCollection(mesh.Vertices.Select(point =>
            new Point3D(point.X - centerX, point.Y - centerY, point.Z - centerZ)));
        var normals = new Vector3D[mesh.Vertices.Count];
        var indices = new Int32Collection();
        foreach (var (a, b, c) in mesh.Triangles)
        {
            var normal = Vector3.Cross(mesh.Vertices[b] - mesh.Vertices[a], mesh.Vertices[c] - mesh.Vertices[a]);
            if (normal.Length > 0)
                normal *= 1 / normal.Length;
            var wpfNormal = new Vector3D(normal.X, normal.Y, normal.Z);
            normals[a] += wpfNormal;
            normals[b] += wpfNormal;
            normals[c] += wpfNormal;
            indices.Add(a);
            indices.Add(b);
            indices.Add(c);
        }

        var normalCollection = new Vector3DCollection(normals.Select(normal =>
        {
            if (normal.Length > 0)
                normal.Normalize();
            return normal;
        }));
        var geometry = new MeshGeometry3D
        {
            Positions = positions,
            TriangleIndices = indices,
            Normals = normalCollection
        };
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(80, 155, 210)));
        Preview3DModelVisual.Content = new GeometryModel3D(geometry, material)
        {
            BackMaterial = material
        };
    }

    private void FitCamera()
    {
        _cameraAzimuth = -35;
        _cameraElevation = 25;
        _cameraDistance = _modelRadius * 2.8;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var azimuth = _cameraAzimuth * Math.PI / 180;
        var elevation = _cameraElevation * Math.PI / 180;
        var horizontal = Math.Cos(elevation);
        var position = new Point3D(
            _cameraDistance * horizontal * Math.Sin(azimuth),
            -_cameraDistance * horizontal * Math.Cos(azimuth),
            _cameraDistance * Math.Sin(elevation));
        Preview3DCamera.Position = position;
        Preview3DCamera.LookDirection = new Vector3D(-position.X, -position.Y, -position.Z);
        Preview3DCamera.UpDirection = new Vector3D(0, 0, 1);
    }

    private void FitView_Click(object sender, RoutedEventArgs e)
    {
        if (_previewMesh is not null)
            FitCamera();
    }

    private void Preview3DViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_previewMesh is null)
            return;
        _isOrbiting = true;
        _lastOrbitPosition = e.GetPosition(Preview3DViewport);
        Preview3DViewport.CaptureMouse();
        e.Handled = true;
    }

    private void Preview3DViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isOrbiting || !Preview3DViewport.IsMouseCaptured)
            return;

        var position = e.GetPosition(Preview3DViewport);
        var delta = position - _lastOrbitPosition;
        _lastOrbitPosition = position;
        _cameraAzimuth += delta.X * 0.6;
        _cameraElevation = Math.Clamp(_cameraElevation - delta.Y * 0.5, -85, 85);
        UpdateCamera();
    }

    private void Preview3DViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isOrbiting = false;
        if (Preview3DViewport.IsMouseCaptured)
            Preview3DViewport.ReleaseMouseCapture();
    }

    private void Preview3DViewport_LostMouseCapture(object sender, MouseEventArgs e) => _isOrbiting = false;

    private void Preview3DViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_previewMesh is null)
            return;

        var minDistance = Math.Max(_modelRadius * 0.05, 1e-9);
        var maxDistance = Math.Max(_modelRadius * 100, minDistance);
        _cameraDistance = Math.Clamp(_cameraDistance * Math.Pow(0.85, e.Delta / 120.0),
            minDistance, maxDistance);
        UpdateCamera();
        e.Handled = true;
    }

    private void DrawPreview()
    {
        if (PreviewCanvas is null)
            return;
        PreviewCanvas.Children.Clear();
        if (_profiles.Count == 0 || PreviewCanvas.ActualWidth < 2 || PreviewCanvas.ActualHeight < 2)
            return;

        var rings = _profiles.SelectMany(profile => new[] { profile.Points }.Concat(profile.Holes)).ToArray();
        var minX = rings.SelectMany(ring => ring).Min(p => p.X);
        var maxX = rings.SelectMany(ring => ring).Max(p => p.X);
        var minY = rings.SelectMany(ring => ring).Min(p => p.Y);
        var maxY = rings.SelectMany(ring => ring).Max(p => p.Y);
        var rangeX = Math.Max(maxX - minX, 1e-9);
        var rangeY = Math.Max(maxY - minY, 1e-9);
        var scale = Math.Min((PreviewCanvas.ActualWidth - 36) / rangeX, (PreviewCanvas.ActualHeight - 36) / rangeY);
        var offsetX = (PreviewCanvas.ActualWidth - rangeX * scale) / 2;
        var offsetY = (PreviewCanvas.ActualHeight - rangeY * scale) / 2;
        var selected = ProfileList.SelectedItems.Cast<Profile>().ToHashSet();

        foreach (var profile in _profiles)
        {
            foreach (var ring in new[] { profile.Points }.Concat(profile.Holes))
            {
                var shape = new System.Windows.Shapes.Polygon
                {
                    Points = new PointCollection(ring.Select(p =>
                        new Point(offsetX + (p.X - minX) * scale, PreviewCanvas.ActualHeight - offsetY - (p.Y - minY) * scale))),
                    Stroke = selected.Contains(profile) ? Brushes.DodgerBlue : Brushes.DimGray,
                    StrokeThickness = selected.Contains(profile) ? 2.5 : 1.4,
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false
                };
                PreviewCanvas.Children.Add(shape);
            }
        }
    }
}
