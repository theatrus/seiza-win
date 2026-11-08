using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Models;
using Seiza.App.Services;

namespace Seiza.App;

public sealed partial class ImageStackWindow
{
    private DropDownButton _addLightFrames = null!;

    private void SetImportNotes(string message, bool expand = false)
    {
        RoutingNotes.Text = message;
        ImportNotesExpander.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        if (expand) ImportNotesExpander.IsExpanded = true;
    }

    private async Task AddFramesAsync(string source)
    {
        if (_running || _routing || _closed) return;
        TextBlock status = RoutingNotes;
        string previousStatus = status.Text;
        _running = true;
        ConfigurationHost.IsEnabled = false;
        StartButton.IsEnabled = false;
        using var cancellation = new CancellationTokenSource();
        _stackCancellation = cancellation;
        try
        {
            StackFrameImportResult? imported = await StackFramePicker.PickAsync(source, WindowHandle, cancellation.Token);
            if (imported is null) return;
            cancellation.Token.ThrowIfCancellationRequested();
            SetImportNotes("Reading image roles…", expand: true);
            string[] paths = imported.Paths;
            string[] notes = imported.Notes;
            string[] existing = _frames.Select(frame => frame.Path).Concat(_providedCalibration).ToArray();
            string[] added = paths.Except(existing, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            StackInputRoutingResult routed = await StackInputRouting.RouteAsync(added, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            _sourcePaths = _sourcePaths.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var messages = new List<string>(notes.Concat(routed.Notes));
            int duplicates = paths.Length - added.Length;
            if (duplicates > 0) messages.Add($"{duplicates} already-added frames ignored; existing selections kept.");
            {
                var selected = SelectedPaths.Concat(routed.Lights).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _initializing = true;
                try
                {
                    _frames = _frames.Concat(routed.Lights.Select(path => new StackFrameChoice(path, ImageStackFrameLabel.DisplayName(path, _browserRoot)))).ToArray();
                    FrameList.ItemsSource = _frames;
                    foreach (StackFrameChoice frame in _frames)
                        if (selected.Contains(frame.Path)) FrameList.SelectedItems.Add(frame);
                    _filterNames = _filterNames.Concat(routed.Filters).GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
                    _providedCalibration.AddRange(routed.Calibration.Except(_providedCalibration, StringComparer.OrdinalIgnoreCase));
                    if (routed.Calibration.Length > 0) CalibrationSourcePicker.SelectedIndex = 1;
                    RenderProvidedCalibration();
                }
                finally { _initializing = false; }
                if (routed.Lights.Length > 0 && _skyAnalysis is not null)
                {
                    SkyGrouping_Toggled(this, new RoutedEventArgs());
                    messages.Add("Inputs changed. Re-run sky-panel analysis before stacking with sky grouping enabled.");
                }
                messages.Insert(0, $"Added {routed.Lights.Length} lights and {routed.Calibration.Length} calibration files. {_frames.Length} lights available.");
            }
            SetImportNotes(string.Join(Environment.NewLine, messages));
        }
        catch (OperationCanceledException) { if (!_closed) SetImportNotes(previousStatus + "\nAdding frames cancelled; existing inputs kept.", expand: true); }
        catch (Exception exception) { if (!_closed) SetImportNotes(exception.Message, expand: true); }
        finally
        {
            _running = false;
            if (ReferenceEquals(_stackCancellation, cancellation)) _stackCancellation = null;
            if (!_closed)
            {
                ConfigurationHost.IsEnabled = true;
                CancelButton.IsEnabled = true;
                RefreshGroupsAndValidation();
                if (_closeWhenIdle) { _allowClose = true; Close(); }
            }
        }
    }
}
