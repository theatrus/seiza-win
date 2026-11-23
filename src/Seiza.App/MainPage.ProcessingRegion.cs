using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Seiza.App.Models;
using Seiza.App.Rendering;
using Windows.Foundation;

namespace Seiza.App;

public sealed partial class MainPage
{
    private ProcessingRegion? _processingPreviewRegion;
    private ProcessingRegion? _regionDrag;
    private Vector2? _regionStart;
    private bool _pickingProcessingRegion;

    private void ClearProcessingRegionPreview()
    {
        _processingPreviewRegion = null;
    }

    private void DrawProcessingRegion(CanvasDrawingSession drawing)
    {
        Rect Destination(ProcessingRegion region) => new(_offset.X + region.X * _scale,
            _offset.Y + region.Y * _scale, region.Width * _scale, region.Height * _scale);
        if (_processingPreviewRegion is { } preview)
            drawing.DrawRectangle(Destination(preview), Microsoft.UI.Colors.DeepSkyBlue, 2);
        if (_regionDrag is { } drag) drawing.DrawRectangle(Destination(drag), Microsoft.UI.Colors.DeepSkyBlue, 2);
    }

    private void BeginProcessingRegionPicker()
    {
        if (_bitmap is null || _stretchWindow is null) return;
        _isDragging = false;
        ImageCanvas.ManipulationMode = ManipulationModes.None;
        _pickingProcessingRegion = true;
        _regionStart = null;
        _regionDrag = null;
        ProcessingRegionBanner.Visibility = Visibility.Visible;
        _ownerWindow?.Activate();
    }

    private Vector2 RegionPoint(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageCanvas).Position;
        return (new Vector2((float)point.X, (float)point.Y) - _offset) / _scale;
    }

    private bool StartProcessingRegionDrag(PointerRoutedEventArgs e)
    {
        if (!_pickingProcessingRegion) return false;
        var point = RegionPoint(e);
        if (point.X >= 0 && point.Y >= 0 && point.X < _sourceWidth && point.Y < _sourceHeight)
        {
            _regionStart = point;
            ImageCanvas.CapturePointer(e.Pointer);
        }
        e.Handled = true;
        return true;
    }

    private bool MoveProcessingRegionDrag(PointerRoutedEventArgs e)
    {
        if (!_pickingProcessingRegion) return false;
        if (_regionStart is { } start)
        {
            var point = RegionPoint(e);
            var region = ProcessingRegion.FromDrag(start.X, start.Y, point.X, point.Y, _sourceWidth, _sourceHeight);
            _regionDrag = region with { Width = Math.Min(region.Width, 2048), Height = Math.Min(region.Height, 2048) };
            ImageCanvas.Invalidate();
        }
        e.Handled = true;
        return true;
    }

    private bool FinishProcessingRegionDrag(PointerRoutedEventArgs e)
    {
        if (!_pickingProcessingRegion) return false;
        if (_regionStart is null) { e.Handled = true; return true; }
        MoveProcessingRegionDrag(e);
        var selected = _regionDrag;
        if (selected is null || selected.Width < 16 || selected.Height < 16)
        { _regionStart = null; _regionDrag = null; ImageCanvas.ReleasePointerCapture(e.Pointer); ImageCanvas.Invalidate(); e.Handled = true; return true; }
        EndProcessingRegionPicker();
        ImageCanvas.ReleasePointerCapture(e.Pointer);
        if (selected is not null) _stretchWindow?.ApplyPreviewRegion(selected);
        else _stretchWindow?.Activate();
        e.Handled = true;
        return true;
    }

    private void EndProcessingRegionPicker()
    {
        ImageCanvas.ManipulationMode = ManipulationModes.Scale | ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        _pickingProcessingRegion = false;
        _regionStart = null;
        _regionDrag = null;
        ProcessingRegionBanner.Visibility = Visibility.Collapsed;
        ImageCanvas.Invalidate();
    }

    private void CancelProcessingRegion_Click(object sender, RoutedEventArgs e)
    {
        EndProcessingRegionPicker();
        _stretchWindow?.AppWindow.Show();
        _stretchWindow?.Activate();
    }
}
