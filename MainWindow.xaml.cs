using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private IReadOnlyList<Profile> _profiles = [];

    public MainWindow()
    {
        InitializeComponent();
        OperationCombo.ItemsSource = _operations;
        OperationCombo.SelectedIndex = 0;
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
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _profiles = [];
            ProfileList.ItemsSource = null;
            FileNameText.Text = "No DXF loaded";
            DrawPreview();
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
            var axis = AxisCombo.SelectedItem is ComboBoxItem item && item.Content is string axisText
                ? axisText[0]
                : 'Y';
            var mesh = operation.Build(selectedProfiles, new OperationSettings(amount, axis));
            StlWriter.WriteBinary(saveDialog.FileName, mesh);
            var message = $"Saved {mesh.Triangles.Count:N0} triangles. Mesh checked and watertight.";
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
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => DrawPreview();
    private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPreview();

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
