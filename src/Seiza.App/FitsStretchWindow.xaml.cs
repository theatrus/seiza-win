using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Models;
using Windows.Graphics;

namespace Seiza.App;

internal sealed record FitsStretchPreviewRequest(
    FitsImageProcessingConfiguration Processing,
    CancellationToken CancellationToken);

public sealed partial class FitsStretchWindow : Window
{
    private static readonly StretchTypeChoice[] StretchTypes =
        Enum.GetValues<FitsStretchType>()
            .Select(type => new StretchTypeChoice(type, type.Title()))
            .ToArray();

    private static readonly ColorStrategyChoice[] ColorStrategies =
        Enum.GetValues<FitsStretchColorStrategy>()
            .Select(strategy => new ColorStrategyChoice(strategy, strategy.Title()))
            .ToArray();

    private readonly List<FitsStretchConfiguration> _stages;
    private readonly TaskCompletionSource<bool> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _previewCancellation;
    private int _selectedStageIndex;
    private int _previewGeneration;
    private bool _isUpdating;
    private bool _loaded;

    internal FitsStretchWindow(
        string documentName,
        FitsStretchStack stack,
        bool extractsBackground,
        bool supportsColor)
    {
        InitializeComponent();
        _stages = stack.Stages.Select(stage => stage.Clone()).ToList();
        _selectedStageIndex = _stages.Count - 1;
        Title = $"FITS Stretch — {documentName}";
        StretchTitleBar.Title = Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(StretchTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
        ContentRoot.Loaded += ContentRoot_Loaded;
        Closed += Window_Closed;

        MethodPicker.ItemsSource = StretchTypes;
        ColorStrategyPicker.ItemsSource = ColorStrategies;
        BackgroundToggle.IsOn = extractsBackground;
        ColorPanel.Visibility = supportsColor ? Visibility.Visible : Visibility.Collapsed;
        RefreshEditor();
    }

    internal Func<FitsStretchPreviewRequest, Task>? PreviewRequested { get; set; }

    internal Task<bool> Completion => _completion.Task;

    internal FitsStretchStack ResultStack => new(_stages);

    internal bool ResultExtractsBackground => BackgroundToggle.IsOn;

    private FitsStretchConfiguration Configuration => _stages[_selectedStageIndex];

    private void ContentRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 workArea = display.WorkArea;
        double scale = ContentRoot.XamlRoot.RasterizationScale;
        int margin = Math.Max(24, (int)Math.Round(24 * scale));
        int width = Math.Min((int)Math.Round(600 * scale), workArea.Width - (margin * 2));
        int height = Math.Min((int)Math.Round(780 * scale), workArea.Height - (margin * 2));
        int x = workArea.X + workArea.Width - width - margin;
        int y = workArea.Y + ((workArea.Height - height) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void RefreshEditor()
    {
        _isUpdating = true;
        try
        {
            StagePicker.ItemsSource = _stages
                .Select((stage, index) => $"{index + 1}. {stage.Type.Title()}")
                .ToArray();
            StagePicker.SelectedIndex = _selectedStageIndex;
            MoveStageUpButton.IsEnabled = _selectedStageIndex > 0;
            MoveStageDownButton.IsEnabled = _selectedStageIndex < _stages.Count - 1;
            RemoveStageButton.IsEnabled = _stages.Count > 1;

            MethodPicker.SelectedIndex = Array.FindIndex(
                StretchTypes,
                item => item.Type == Configuration.Type);
            MethodHelpText.Text = Configuration.Type.Help();
            ColorStrategyPicker.SelectedIndex = Array.FindIndex(
                ColorStrategies,
                item => item.Strategy == Configuration.ColorStrategy);
            ColorHelpText.Text = Configuration.ColorStrategy.Help();
            BuildParameterControls();
            UpdateValidation();
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void BuildParameterControls()
    {
        ParameterPanel.Children.Clear();
        switch (Configuration.Type)
        {
            case FitsStretchType.AutoMtf:
                AddParameter("Target median", Configuration.TargetMedian, 0.01, 0.95, 0.01,
                    value => Configuration.TargetMedian = value);
                AddParameter("Shadows clipping", Configuration.ShadowsClip, -10, 0, 0.1,
                    value => Configuration.ShadowsClip = value);
                break;
            case FitsStretchType.PercentileAsinh:
                AddParameter("Black percentile", Configuration.BlackPercentile, 0, 0.99, 0.001,
                    value => Configuration.BlackPercentile = value);
                AddParameter("White percentile", Configuration.WhitePercentile, 0.01, 1, 0.001,
                    value => Configuration.WhitePercentile = value);
                AddParameter("Strength", Configuration.Strength, 0.1, 50, 0.1,
                    value => Configuration.Strength = value);
                break;
            case FitsStretchType.Linear:
                AddBlackAndWhiteParameters();
                break;
            case FitsStretchType.Asinh:
                AddBlackAndWhiteParameters();
                AddParameter("Strength", Configuration.Strength, 0.1, 50, 0.1,
                    value => Configuration.Strength = value);
                break;
            case FitsStretchType.Mtf:
                AddParameter("Shadows", Configuration.Shadows, 0, 0.99, 0.001,
                    value => Configuration.Shadows = value);
                AddParameter("Midtone", Configuration.Midtone, 0.01, 0.99, 0.001,
                    value => Configuration.Midtone = value);
                AddParameter("Highlights", Configuration.Highlights, 0.01, 1, 0.001,
                    value => Configuration.Highlights = value);
                break;
            case FitsStretchType.Ghs:
                AddParameter("Stretch factor", Configuration.StretchFactor, 0, 20, 0.1,
                    value => Configuration.StretchFactor = value);
                AddParameter("Local intensity", Configuration.LocalIntensity, -5, 15, 0.1,
                    value => Configuration.LocalIntensity = value);
                AddParameter("Symmetry point", Configuration.SymmetryPoint, 0, 1, 0.001,
                    value =>
                    {
                        Configuration.SymmetryPoint = value;
                        Configuration.ProtectShadows = Math.Min(Configuration.ProtectShadows, value);
                        Configuration.ProtectHighlights = Math.Max(Configuration.ProtectHighlights, value);
                        DispatcherQueue.TryEnqueue(
                            DispatcherQueuePriority.Low,
                            BuildParameterControls);
                    });
                AddParameter(
                    "Shadow protection",
                    Configuration.ProtectShadows,
                    0,
                    Math.Max(Configuration.SymmetryPoint, 0.001),
                    0.001,
                    value => Configuration.ProtectShadows = value);
                AddParameter(
                    "Highlight protection",
                    Configuration.ProtectHighlights,
                    Math.Min(Configuration.SymmetryPoint, 0.999),
                    1,
                    0.001,
                    value => Configuration.ProtectHighlights = value);
                AddBlackAndWhiteParameters();
                break;
            case FitsStretchType.Identity:
                ParameterPanel.Children.Add(new TextBlock
                {
                    Text = "Normalized samples are clamped directly to the display range.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.72,
                });
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void AddBlackAndWhiteParameters()
    {
        AddParameter("Black point", Configuration.Black, 0, 0.99, 0.001,
            value => Configuration.Black = value);
        AddParameter("White point", Configuration.White, 0.01, 1, 0.001,
            value => Configuration.White = value);
    }

    private void AddParameter(
        string title,
        double value,
        double minimum,
        double maximum,
        double step,
        Action<double> setter)
    {
        var grid = new Grid
        {
            ColumnSpacing = 10,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });

        var label = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            StepFrequency = step,
            Value = Math.Clamp(value, minimum, maximum),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(slider, title);
        Grid.SetColumn(slider, 1);

        var number = new NumberBox
        {
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = step,
            LargeChange = step * 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = Math.Clamp(value, minimum, maximum),
        };
        AutomationProperties.SetName(number, $"{title} value");
        Grid.SetColumn(number, 2);

        bool syncing = false;
        slider.ValueChanged += (_, args) =>
        {
            if (_isUpdating || syncing)
            {
                return;
            }
            syncing = true;
            number.Value = args.NewValue;
            syncing = false;
            setter(args.NewValue);
            DraftChanged();
        };
        number.ValueChanged += (_, args) =>
        {
            if (_isUpdating || syncing || !double.IsFinite(args.NewValue))
            {
                return;
            }
            double next = Math.Clamp(args.NewValue, minimum, maximum);
            syncing = true;
            slider.Value = next;
            syncing = false;
            setter(next);
            DraftChanged();
        };

        grid.Children.Add(label);
        grid.Children.Add(slider);
        grid.Children.Add(number);
        ParameterPanel.Children.Add(grid);
    }

    private void StagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating || StagePicker.SelectedIndex < 0)
        {
            return;
        }
        _selectedStageIndex = StagePicker.SelectedIndex;
        RefreshEditor();
    }

    private void AddStage_Click(object sender, RoutedEventArgs e)
    {
        _stages.Add(FitsStretchConfiguration.CreateIdentity());
        _selectedStageIndex = _stages.Count - 1;
        RefreshEditor();
        DraftChanged();
    }

    private void RemoveStage_Click(object sender, RoutedEventArgs e)
    {
        if (_stages.Count <= 1)
        {
            return;
        }
        _stages.RemoveAt(_selectedStageIndex);
        _selectedStageIndex = Math.Min(_selectedStageIndex, _stages.Count - 1);
        RefreshEditor();
        DraftChanged();
    }

    private void MoveStageUp_Click(object sender, RoutedEventArgs e) => MoveStage(-1);

    private void MoveStageDown_Click(object sender, RoutedEventArgs e) => MoveStage(1);

    private void MoveStage(int offset)
    {
        int destination = _selectedStageIndex + offset;
        if (destination < 0 || destination >= _stages.Count)
        {
            return;
        }
        (_stages[_selectedStageIndex], _stages[destination]) =
            (_stages[destination], _stages[_selectedStageIndex]);
        _selectedStageIndex = destination;
        RefreshEditor();
        DraftChanged();
    }

    private void ResetStage_Click(object sender, RoutedEventArgs e)
    {
        _stages[_selectedStageIndex] = new FitsStretchConfiguration();
        RefreshEditor();
        DraftChanged();
    }

    private void MethodPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating || MethodPicker.SelectedItem is not StretchTypeChoice choice)
        {
            return;
        }
        Configuration.Type = choice.Type;
        RefreshEditor();
        DraftChanged();
    }

    private void ColorStrategyPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating || ColorStrategyPicker.SelectedItem is not ColorStrategyChoice choice)
        {
            return;
        }
        Configuration.ColorStrategy = choice.Strategy;
        ColorHelpText.Text = choice.Strategy.Help();
        DraftChanged();
    }

    private void BackgroundToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isUpdating)
        {
            DraftChanged();
        }
    }

    private void DraftChanged()
    {
        RefreshStageLabels();
        if (UpdateValidation())
        {
            SchedulePreview();
        }
        else
        {
            CancelPreview();
        }
    }

    private void RefreshStageLabels()
    {
        int selected = _selectedStageIndex;
        _isUpdating = true;
        StagePicker.ItemsSource = _stages
            .Select((stage, index) => $"{index + 1}. {stage.Type.Title()}")
            .ToArray();
        StagePicker.SelectedIndex = selected;
        _isUpdating = false;
    }

    private bool UpdateValidation()
    {
        string? message = _stages
            .Select(stage => stage.ValidationMessage)
            .FirstOrDefault(candidate => candidate is not null);
        SaveButton.IsEnabled = message is null;
        ValidationInfoBar.Message = message ?? string.Empty;
        ValidationInfoBar.IsOpen = message is not null;
        return message is null;
    }

    private void SchedulePreview()
    {
        CancelPreview();
        CancellationTokenSource cancellation = new();
        _previewCancellation = cancellation;
        int generation = ++_previewGeneration;
        _ = RunPreviewAsync(generation, cancellation.Token);
    }

    private async Task RunPreviewAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(140, cancellationToken);
            if (PreviewRequested is null)
            {
                return;
            }

            SetPreviewStatus(true, "Updating live preview…");
            var processing = new FitsImageProcessingConfiguration(
                new FitsStretchStack(_stages),
                BackgroundToggle.IsOn,
                interactivePreview: true);
            await PreviewRequested(new FitsStretchPreviewRequest(processing, cancellationToken));
            if (generation == _previewGeneration && !cancellationToken.IsCancellationRequested)
            {
                SetPreviewStatus(false, string.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer draft or dialog dismissal superseded this render.
        }
        catch (Exception exception)
        {
            if (generation == _previewGeneration && !cancellationToken.IsCancellationRequested)
            {
                SetPreviewStatus(false, $"Preview failed: {exception.Message}");
            }
        }
    }

    private void SetPreviewStatus(bool active, string message)
    {
        PreviewProgressRing.IsActive = active;
        PreviewProgressRing.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PreviewStatusText.Text = message;
    }

    private void CancelPreview()
    {
        _previewGeneration++;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        SetPreviewStatus(false, string.Empty);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled)
        {
            return;
        }
        _completion.TrySetResult(true);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        CancelPreview();
        _completion.TrySetResult(false);
    }

    private sealed record StretchTypeChoice(FitsStretchType Type, string Title);

    private sealed record ColorStrategyChoice(FitsStretchColorStrategy Strategy, string Title);
}
