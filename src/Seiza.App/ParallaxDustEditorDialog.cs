using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Seiza.App.Models;
using Windows.System;

namespace Seiza.App;

// Native dialog chrome around a private mask draft. It deliberately has no
// Session/native handle and cannot publish an edit while the user paints.
internal sealed class ParallaxDustEditorDialog : ContentDialog
{
    private readonly ParallaxDustEditorModel _model;
    private readonly FrameworkElement? _owner;
    private readonly Canvas _surface = new() { Width = 640, Height = 400, Background = new SolidColorBrush(Colors.Black) };
    private readonly Image _source = new() { Width = 640, Height = 400, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly Image _mask = new() { Width = 640, Height = 400, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly Ellipse _cursor = new() { Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly ComboBox _mode = new() { Width = 185 };
    private readonly ComboBox _view = new() { Width = 150 };
    private readonly Slider _diameter = new() { Minimum = 1, StepFrequency = 1, IsThumbToolTipEnabled = true };
    private readonly Slider _strength = new() { Minimum = 0.05, Maximum = 1, StepFrequency = 0.05, Value = 0.5, IsThumbToolTipEnabled = true };
    private readonly TextBlock _diameterText = new(), _strengthText = new();
    private readonly Button _undo = new() { Content = "Undo" }, _redo = new() { Content = "Redo" }, _reset = new() { Content = "Reset edits" };
    private readonly WriteableBitmap _maskBitmap;
    private readonly byte[] _maskPixels;
    private uint? _pointer;
    private bool _aborted;
    private ParallaxDustEditResult? _result;

    private ParallaxDustEditorDialog(XamlRoot owner, RenderedImageData source, float[] opacity, int width, int height)
    {
        _model = new(opacity, width, height); _owner = owner.Content as FrameworkElement;
        XamlRoot = owner; Title = "Paint dust map"; PrimaryButtonText = "Apply"; CloseButtonText = "Cancel";
        TitleTemplate = (DataTemplate)Application.Current.Resources["SeizaDialogTitleTemplate"];
        DefaultButton = ContentDialogButton.None; IsPrimaryButtonEnabled = false;
        Resources["ContentDialogMaxWidth"] = 760d;
        var sourceBitmap = new WriteableBitmap(source.Width, source.Height);
        using (Stream stream = sourceBitmap.PixelBuffer.AsStream()) stream.Write(source.Bgra);
        _source.Source = sourceBitmap;
        _maskBitmap = new(width, height); _maskPixels = new byte[checked(width * height * 4)]; _mask.Source = _maskBitmap;
        _surface.Children.Add(_source); _surface.Children.Add(_mask); _surface.Children.Add(_cursor);
        _mode.Items.Add("Paint opaque dust"); _mode.Items.Add("Paint clear sky"); _mode.SelectedIndex = 0;
        _view.Items.Add("Image + mask"); _view.Items.Add("Mask only"); _view.SelectedIndex = 0;
        AutomationProperties.SetName(_mode, "Dust brush mode"); AutomationProperties.SetName(_view, "Dust preview view");
        AutomationProperties.SetName(_diameter, "Brush diameter in draft pixels"); AutomationProperties.SetName(_strength, "Brush strength");
        AutomationProperties.SetName(_surface, "Dust painting surface. Red marks opaque dust; black in mask view is clear sky.");
        _diameter.Maximum = Math.Max(4, Math.Max(width, height)); _diameter.Value = Math.Clamp(Math.Min(width, height) * 0.08, 1, 96);
        var content = new StackPanel { Spacing = 12, Width = 640 };
        content.Children.Add(new TextBlock
        {
            Text = "Paint dust over the image. Red means opaque; clear sky remains visible. Only Apply changes the scene.",
            TextWrapping = TextWrapping.Wrap,
        });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (UIElement control in new UIElement[] { _mode, _view, _undo, _redo, _reset }) toolbar.Children.Add(control);
        content.Children.Add(toolbar);
        var brushGrid = new Grid { ColumnSpacing = 20 };
        brushGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        brushGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var diameterGroup = new StackPanel(); diameterGroup.Children.Add(_diameterText); diameterGroup.Children.Add(_diameter);
        var strengthGroup = new StackPanel(); strengthGroup.Children.Add(_strengthText); strengthGroup.Children.Add(_strength);
        Grid.SetColumn(strengthGroup, 1); brushGrid.Children.Add(diameterGroup); brushGrid.Children.Add(strengthGroup); content.Children.Add(brushGrid);
        content.Children.Add(new Viewbox { Child = _surface, Stretch = Stretch.Uniform, MaxHeight = Math.Max(120, Math.Min(400, owner.Size.Height - 310)) });
        content.Children.Add(new TextBlock
        {
            Text = $"{width} × {height} opacity draft; source pixels are unchanged. Soft brushes blend toward opaque or clear. Ctrl+Z / Ctrl+Y undo and redo; Reset edits restores the map opened here.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12,
        });
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        _diameter.ValueChanged += (_, _) => RefreshControls(); _strength.ValueChanged += (_, _) => RefreshControls();
        _view.SelectionChanged += (_, _) => RefreshMask();
        _undo.Click += (_, _) => { FinishStroke(false); _model.Undo(); RefreshMask(); RefreshControls(); };
        _redo.Click += (_, _) => { FinishStroke(false); _model.Redo(); RefreshMask(); RefreshControls(); };
        _reset.Click += (_, _) => { FinishStroke(false); _model.Reset(); RefreshMask(); RefreshControls(); };
        _surface.PointerPressed += SurfacePointerPressed;
        _surface.PointerMoved += SurfacePointerMoved;
        _surface.PointerReleased += SurfacePointerReleased;
        _surface.PointerCanceled += SurfacePointerCanceled;
        _surface.PointerCaptureLost += SurfacePointerCanceled;
        _surface.PointerExited += (_, _) => _cursor.Visibility = Visibility.Collapsed;
        PrimaryButtonClick += (_, args) =>
        {
            if (_aborted) { args.Cancel = true; return; }
            FinishStroke(false); _result = _model.CreateResult();
        };
        CloseButtonClick += (_, _) => { FinishStroke(true); _result = null; };
        Closed += (_, _) => FinishStroke(true);
        AddUndoShortcut(VirtualKey.Z, VirtualKeyModifiers.Control, false);
        AddUndoShortcut(VirtualKey.Y, VirtualKeyModifiers.Control, true);
        AddUndoShortcut(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, true);
        RefreshMask(); RefreshControls();
    }

    internal static async Task<ParallaxDustEditResult?> EditAsync(XamlRoot owner, RenderedImageData source,
        float[] opacity, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(source);
        var dialog = new ParallaxDustEditorDialog(owner, source, opacity, width, height);
        if (dialog._owner is { } root) root.Unloaded += dialog.OwnerUnloaded;
        try
        {
            if (dialog._owner is { IsLoaded: false }) return null;
            ContentDialogResult result = await dialog.ShowAsync();
            return !dialog._aborted && result == ContentDialogResult.Primary ? dialog._result : null;
        }
        finally
        {
            if (dialog._owner is { } subscribedRoot) subscribedRoot.Unloaded -= dialog.OwnerUnloaded;
            dialog.FinishStroke(true);
        }
    }

    private void OwnerUnloaded(object sender, RoutedEventArgs args)
    {
        _aborted = true; _result = null; FinishStroke(true); Hide();
    }
    private void AddUndoShortcut(VirtualKey key, VirtualKeyModifiers modifiers, bool redo)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            FinishStroke(false); if (redo) _model.Redo(); else _model.Undo();
            RefreshMask(); RefreshControls(); args.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }
    private void RefreshControls()
    {
        _diameterText.Text = $"Brush diameter: {_diameter.Value:0} draft px";
        _strengthText.Text = $"Strength: {_strength.Value:P0}";
        _undo.IsEnabled = _model.CanUndo; _redo.IsEnabled = _model.CanRedo;
        _reset.IsEnabled = _model.HasChanges; IsPrimaryButtonEnabled = _model.HasChanges && !_aborted;
    }
    private void RefreshMask()
    {
        bool maskOnly = _view.SelectedIndex == 1; _source.Visibility = maskOnly ? Visibility.Collapsed : Visibility.Visible;
        ReadOnlySpan<float> opacity = _model.Opacity;
        for (int index = 0; index < opacity.Length; index++)
        {
            byte value = (byte)Math.Clamp((int)Math.Round(opacity[index] * 255), 0, 255); int pixel = index * 4;
            byte alpha = maskOnly ? (byte)255 : (byte)(value * 0.55);
            // WriteableBitmap uses premultiplied BGRA for translucent pixels.
            _maskPixels[pixel] = maskOnly ? value : (byte)(40 * alpha / 255);
            _maskPixels[pixel + 1] = maskOnly ? value : (byte)(80 * alpha / 255);
            _maskPixels[pixel + 2] = maskOnly ? value : alpha;
            _maskPixels[pixel + 3] = alpha;
        }
        using (Stream stream = _maskBitmap.PixelBuffer.AsStream()) stream.Write(_maskPixels);
        _maskBitmap.Invalidate();
    }
    private bool Map(PointerRoutedEventArgs args, out ParallaxDustPoint point)
    {
        var position = args.GetCurrentPoint(_surface).Position;
        return ParallaxDustEditorModel.TryMapUniform(position.X, position.Y, _surface.ActualWidth, _surface.ActualHeight, _model.Width, _model.Height, out point);
    }
    private void MoveCursor(PointerRoutedEventArgs args)
    {
        if (!Map(args, out _)) { _cursor.Visibility = Visibility.Collapsed; return; }
        var position = args.GetCurrentPoint(_surface).Position;
        double scale = Math.Min(_surface.ActualWidth / _model.Width, _surface.ActualHeight / _model.Height);
        _cursor.Width = _cursor.Height = _diameter.Value * scale;
        Canvas.SetLeft(_cursor, position.X - _cursor.Width / 2); Canvas.SetTop(_cursor, position.Y - _cursor.Height / 2);
        _cursor.Visibility = Visibility.Visible;
    }
    private void SurfacePointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer is not null || !args.GetCurrentPoint(_surface).Properties.IsLeftButtonPressed || !Map(args, out ParallaxDustPoint point)) return;
        if (!_surface.CapturePointer(args.Pointer)) return;
        _pointer = args.Pointer.PointerId;
        _model.BeginStroke(point, _diameter.Value, _strength.Value, _mode.SelectedIndex == 0 ? ParallaxDustBrushMode.Opaque : ParallaxDustBrushMode.Clear);
        MoveCursor(args); RefreshMask(); args.Handled = true;
    }
    private void SurfacePointerMoved(object sender, PointerRoutedEventArgs args)
    {
        MoveCursor(args);
        if (_pointer != args.Pointer.PointerId || !Map(args, out ParallaxDustPoint point)) return;
        if (_model.ContinueStroke(point)) RefreshMask(); args.Handled = true;
    }
    private void SurfacePointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != args.Pointer.PointerId) return;
        FinishStroke(false); RefreshControls(); args.Handled = true;
    }
    private void SurfacePointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != args.Pointer.PointerId) return;
        FinishStroke(true); RefreshMask(); RefreshControls();
    }
    private void FinishStroke(bool cancel)
    {
        if (_pointer is null) return;
        _pointer = null; if (cancel) _model.CancelStroke(); else _model.EndStroke();
        _surface.ReleasePointerCaptures();
    }
}
