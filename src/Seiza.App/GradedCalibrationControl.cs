using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Models;
using Seiza.App.Services;

namespace Seiza.App;

// UI draft only. Core owns grading, matching, choices, recipes and cache publication.
internal sealed class GradedCalibrationControl : StackPanel, IDisposable
{
    private readonly TextBlock _status = Text("Provide calibration files or a library folder to analyze.");
    private readonly StackPanel _settings = new() { Spacing = 12 };
    private readonly StackPanel[] _pages = Enumerable.Range(0, 4).Select(_ => new StackPanel { Spacing = 12 }).ToArray();
    private readonly ComboBox _scope = new() { Header = "Settings for", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Pivot _review = new();
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _analysis;
    private string? _library;
    private string[] _lights = [], _provided = [];
    private LibraryField[] _fields = [];
    private int _generation;
    private bool _disposed;
    private string? _inputKey;
    private string _scopeId = "";
    public CalibrationLibraryPreferences Preferences { get; } = CalibrationLibraryPreferencesStore.Load().Copy();
    public LibraryReview? Review { get; private set; }
    public bool IsCurrent { get; private set; }
    public bool IsBusy { get; private set; }
    public event EventHandler? StateChanged;
    public UIElement SettingsView { get; }
    public UIElement ReviewView => _review;

    public GradedCalibrationControl()
    {
        Spacing = 10;
        Children.Add(Text("Frames are graded, grouped and matched by Seiza. Review the evidence here; masters are built when stacking starts."));
        Children.Add(Choice("Flats", ["Group and match automatically", "Use all as one set"],
            Preferences.FlatAssociation == "one_set" ? 1 : 0, i => { Preferences.FlatAssociation = i == 0 ? "automatic" : "one_set"; RenderSettings(); Changed(); }));
        Children.Add(Choice("Darks", ["Group and match automatically", "Use all as one set"],
            Preferences.DarkAssociation == "one_set" ? 1 : 0, i => { Preferences.DarkAssociation = i == 0 ? "automatic" : "one_set"; Changed(); }));
        Children.Add(Choice("Incomplete lights", ["Stop and review", "Keep with available calibration", "Exclude from stack"],
            Preferences.Incomplete switch { "keep" => 1, "exclude" => 2, _ => 0 }, i =>
            {
                Preferences.Incomplete = i switch { 1 => "keep", 2 => "exclude", _ => "stop" };
                // Execution choice only: retain the current review and any in-flight analysis.
                StateChanged?.Invoke(this, EventArgs.Empty);
            }));
        Children.Add(_progress);
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Children.Add(_status);
        var again = new Button { Content = "Analyze again" };
        again.Click += (_, _) => Changed();
        Children.Add(again);
        Children.Add(Text("Changes apply to this stack and become saved defaults when stacking starts."));
        var settingsPanel = new StackPanel { Spacing = 12 };
        settingsPanel.Children.Add(_scope); settingsPanel.Children.Add(_settings);
        _scope.SelectionChanged += (_, _) =>
        {
            _scopeId = (_scope.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            RenderSettings();
        };
        SettingsView = new Expander { Header = "Matching, grading and flat stability", Content = settingsPanel,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        string[] labels = ["Calibration frames", "Light matches", "Master plan", "Flat groups"];
        for (int i = 0; i < labels.Length; i++) _review.Items.Add(new PivotItem { Header = labels[i],
            Content = new ScrollViewer { Content = _pages[i], HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
        Loaded += async (_, _) =>
        {
            if (_fields.Length > 0) return;
            try
            {
                _fields = (await CalibrationLibraryService.SchemaAsync()).Settings;
                if (!_disposed) { UpdateScopes(); RenderSettings(); }
            }
            catch (Exception e) { if (!_disposed) _status.Text = e.Message; }
        };
    }

    public void SetInputs(string? library, IEnumerable<string> lights, IEnumerable<string> provided, bool enabled)
    {
        string[] nextLights = lights.ToArray(), nextProvided = provided.ToArray();
        string key = JsonSerializer.Serialize(new LibraryRequest { Operation = enabled ? "analyze" : "schema", Library = library,
            Lights = nextLights, Provided = nextProvided }, LibraryJsonContext.Default.LibraryRequest);
        if (_inputKey == key) return;
        _inputKey = key; _library = library; _lights = nextLights; _provided = nextProvided;
        Changed(enabled);
    }

    public LibraryRequest Request(string operation, string? reviewKey = null) =>
        LibraryRequest.Create(operation, _library, _lights, _provided, Preferences, reviewKey);

    private void Changed(bool enabled = true)
    {
        _analysis?.Cancel();
        int generation = ++_generation;
        IsCurrent = false; IsBusy = false;
        _review.IsEnabled = false;
        if (!enabled || _lights.Length < 2 || (_provided.Length == 0 && _library is null))
        {
            _status.Text = "Choose at least two lights and provide calibration files or a library folder.";
            _progress.Visibility = Visibility.Collapsed;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        _status.Text = "Review needs analysis…";
        _progress.Visibility = Visibility.Visible;
        var cancel = new CancellationTokenSource();
        _analysis = cancel;
        _ = AnalyzeAsync(generation, cancel);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task AnalyzeAsync(int generation, CancellationTokenSource cancel)
    {
        try
        {
            await Task.Delay(350, cancel.Token);
            IsBusy = true; StateChanged?.Invoke(this, EventArgs.Empty);
            var progress = new Progress<LibraryProgress>(p =>
            {
                if (!_disposed && generation == _generation)
                    _status.Text = $"{p.Phase.Replace('_', ' ')}: {p.Completed} / {p.Total}";
            });
            LibraryReview result = await CalibrationLibraryService.PerformAsync(Request("analyze"), progress, cancel.Token);
            if (_disposed || generation != _generation) return;
            Review = result; IsCurrent = true;
            _review.IsEnabled = true;
            _status.Text = $"{result.Frames.Length} calibration frames · {result.SessionCount} calibration {(result.SessionCount == 1 ? "session" : "sessions")} · {result.Missing.Count(m => m)} incomplete lights";
            UpdateScopes(); RenderReview();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_disposed && generation == _generation) _status.Text = e.Message; }
        finally
        {
            if (!_disposed && generation == _generation)
            {
                IsBusy = false; _progress.Visibility = Visibility.Collapsed;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            if (ReferenceEquals(_analysis, cancel)) _analysis = null;
            cancel.Dispose();
        }
    }

    private void UpdateScopes()
    {
        var next = new[] { (Id: "", Name: "All rigs") }.Concat((Review?.Rigs ?? []).Select(r => (r.Id, r.Name))).ToArray();
        if (_scope.Items.Cast<ComboBoxItem>().Select(i => ((string)i.Tag, (string)i.Content)).SequenceEqual(next)) return;
        // Keep existing editors/focus alive when an analysis only changes
        // values/evidence. Rebuild scope controls only for a different rig list.
        string selected = _scopeId;
        _scope.Items.Clear();
        _scope.Items.Add(new ComboBoxItem { Content = "All rigs", Tag = "" });
        foreach (LibraryRig rig in Review?.Rigs ?? []) _scope.Items.Add(new ComboBoxItem { Content = rig.Name, Tag = rig.Id });
        _scope.SelectedItem = _scope.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? _scope.Items[0];
    }
    private Dictionary<string, JsonNode?> Values
    {
        get
        {
            if (_scopeId.Length == 0) return Preferences.Settings;
            if (!Preferences.Rigs.TryGetValue(_scopeId, out var values)) Preferences.Rigs[_scopeId] = values = [];
            return values;
        }
    }
    private JsonNode? Value(LibraryField field) => Values.GetValueOrDefault(field.Key) ?? Preferences.Settings.GetValueOrDefault(field.Key) ?? field.Default;
    private void Set(LibraryField field, JsonNode? value)
    {
        if (value is null) Values.Remove(field.Key); else Values[field.Key] = value.DeepClone();
        Changed();
    }
    private void RenderSettings()
    {
        _settings.Children.Clear();
        foreach (string group in new[] { "matching", "darks", "flat_groups", "grades" })
        {
            var rows = new StackPanel { Spacing = 12 };
            foreach (LibraryField field in _fields.Where(f => f.Group == group && f.Key is not ("night_zone" or "light_nights")))
            {
                var row = new StackPanel { Spacing = 4 };
                JsonNode? current = Value(field);
                string type = S(field.Kind, "type");
                switch (type)
                {
                    case "toggle":
                        var toggle = new ToggleSwitch { Header = field.Label, IsOn = current?.GetValue<bool>() ?? false };
                        toggle.Toggled += (_, _) => Set(field, JsonValue.Create(toggle.IsOn)); row.Children.Add(toggle); break;
                    case "choice":
                        JsonNode?[] choices = A(field.Kind, "options").ToArray();
                        int selected = Array.FindIndex(choices, c => S(c, "value") == current?.GetValue<string>());
                        row.Children.Add(Choice(field.Label, choices.Select(c => S(c, "label")).ToArray(), Math.Max(0, selected),
                            i => Set(field, choices[i]?["value"]))); break;
                    case "limits":
                        row.Children.Add(Text(field.Label));
                        foreach (string level in new[] { "suspect", "poor" })
                        {
                            double multiplier = field.Kind["percent"]?.GetValue<bool>() == true ? 100 : 1;
                            var on = new CheckBox { Content = $"{field.Label}: {level} threshold", IsChecked = current?[level] is not null };
                            var number = Number($"{level} ({S(field.Kind, "unit")})", (current?[level]?.GetValue<double>() ?? 0) * multiplier);
                            number.IsEnabled = on.IsChecked == true;
                            void Update()
                            {
                                var values = (Value(field)?.DeepClone() as JsonObject) ?? new JsonObject();
                                values[level] = on.IsChecked == true ? JsonValue.Create(number.Value / multiplier) : null;
                                Set(field, values);
                            }
                            on.Click += (_, _) => { number.IsEnabled = on.IsChecked == true; Update(); };
                            number.ValueChanged += (_, _) => { if (double.IsFinite(number.Value)) Update(); };
                            row.Children.Add(on); row.Children.Add(number);
                        }
                        break;
                    default:
                        var numeric = Number($"{field.Label} ({S(field.Kind, "unit")})", current?.GetValue<double>() ?? 0);
                        numeric.ValueChanged += (_, _) => { if (double.IsFinite(numeric.Value)) Set(field, JsonValue.Create(numeric.Value)); };
                        row.Children.Add(numeric); break;
                }
                row.Children.Add(Text(field.Help.Replace("PSF Guard", "Seiza", StringComparison.Ordinal)));
                var reset = new Button { Content = _scopeId.Length == 0 ? "Use native default" : "Inherit global setting" };
                reset.Click += (_, _) => { Set(field, null); RenderSettings(); };
                row.Children.Add(reset); rows.Children.Add(row);
            }
            _settings.Children.Add(new Expander { Header = char.ToUpperInvariant(group[0]) + group[1..].Replace('_', ' '), Content = rows,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        var starMask = new ToggleSwitch { Header = "Mask stars when building flats", IsOn = Preferences.FlatStarMasking };
        starMask.Toggled += (_, _) => { Preferences.FlatStarMasking = starMask.IsOn; Changed(); };
        _settings.Children.Add(starMask);
        foreach (var (key, label) in new[] { ("enabled", "Check flat-set stability"), ("trimUnstable", "Retry without drifting flats"),
            ("tryNextSession", "Try another flat session"), ("retainUnstable", "Keep unstable flats with a warning") })
        {
            var toggle = new ToggleSwitch { Header = label, IsOn = Preferences.FlatStability[key]?.GetValue<bool>() == true };
            // Core enforces one-set's prohibition on unrelated-session retries.
            if (key == "tryNextSession" && Preferences.FlatAssociation == "one_set") { toggle.IsOn = false; toggle.IsEnabled = false; }
            toggle.Toggled += (_, _) => { Preferences.FlatStability[key] = toggle.IsOn; Changed(); };
            _settings.Children.Add(toggle);
        }
    }

    private void RenderReview()
    {
        foreach (StackPanel page in _pages) page.Children.Clear();
        if (Review is not { } review) return;
        foreach (LibraryFrame frame in review.Frames)
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(Text($"{Path.GetFileName(frame.Path)} · {frame.Kind} · {(frame.IsMaster ? "Existing master" : frame.Grade ?? "Ungraded")}", true));
            row.Children.Add(Text(DisplayPath(frame.Path))); row.Children.Add(Text(frame.Error ?? frame.Reason ?? frame.MeasurementState));
            string? choice = Preferences.Choices.FirstOrDefault(c => c.Path == frame.Path && c.Fingerprint == frame.Fingerprint)?.Choice;
            row.Children.Add(Choice($"Quality choice for {Path.GetFileName(frame.Path)}", ["Automatic", "Keep out", "Use anyway"], choice switch { "exclude" => 1, "use" => 2, _ => 0 }, i =>
            {
                Preferences.Choices.RemoveAll(c => c.Path == frame.Path);
                if (i > 0) Preferences.Choices.Add(new(frame.Path, frame.Fingerprint, i == 1 ? "exclude" : "use"));
                Changed();
            }));
            _pages[0].Children.Add(row);
        }
        int[] assignments = review.Assignments;
        var selections = A(review.Snapshot, "selections").ToArray();
        for (int i = 0; i < review.LightPaths.Length; i++)
        {
            string path = review.LightPaths[i]; JsonNode? selection = selections[i];
            _pages[1].Children.Add(Text($"{Path.GetFileName(path)} · {(review.Missing[i] ? "Incomplete calibration" : $"Session {assignments[i] + 1}")}", true));
            _pages[1].Children.Add(Text(DisplayPath(path)));
            _pages[1].Children.Add(Text($"Flat association: {S(selection?["flatBasis"], "basis")} · Family: {S(selection?["flatFamily"], "by")}"));
            foreach (string kind in new[] { "bias", "dark", "darkFlat", "flat" })
            {
                string[] names = A(selection, kind).Select(id => review.Frames.FirstOrDefault(f => f.Id == id?.GetValue<string>())?.Path)
                    .OfType<string>().Select(p => Path.GetFileName(p)).ToArray();
                _pages[1].Children.Add(Text($"{kind}: {(names.Length > 0 ? string.Join(", ", names) : "None")}"));
            }
            foreach (var choice in A(selection, "flatChoices")) _pages[1].Children.Add(Text(S(choice, "orphaned") is { Length: > 0 } orphan ? orphan : S(choice, "label")));
            AddLightChoices(_pages[1], review.LightOptions[i]);
        }
        int index = 0;
        foreach (JsonNode? session in A(review.Snapshot, "sessions"))
        {
            _pages[2].Children.Add(Text($"Session {++index}", true));
            foreach (JsonNode? recipe in A(session, "recipes"))
            {
                _pages[2].Children.Add(Text($"{S(recipe, "kind")}: {S(recipe, "action")} · {A(recipe, "inputs").Count} inputs"));
                if (S(recipe, "failure").Length > 0) _pages[2].Children.Add(Text(S(recipe, "failure")));
            }
            _pages[2].Children.Add(Text($"Flat application: {S(session?["application"], "flatUse")}"));
        }
        _pages[3].Children.Add(Text($"{A(review.Grouping, "sessions").Count} flat sessions · {A(review.Grouping, "families").Count} response families · {A(review.Grouping, "events").Count} changes", true));
        if (Preferences.FlatAssociation == "one_set") _pages[3].Children.Add(Text("All eligible compatible flats use one set."));
        foreach (var correction in A(review.FlatWork, "corrections"))
            _pages[3].Children.Add(Text($"Correction to {S(correction?["correction"], "lightSide")} side for {A(correction, "lightIndices").Count} lights."));
        foreach (var evidence in A(review.FlatWork["grouping"], "evidence"))
        {
            _pages[3].Children.Add(Text($"Light evidence: {S(evidence, "verdict")}", true));
            foreach (var fit in A(evidence, "fits")) _pages[3].Children.Add(Text($"{S(fit, "filter")}: {S(fit, "verdict")} · {fit?["lights"]} sampled lights"));
        }
        foreach (var edge in A(review.FlatWork, "groupEdges"))
        {
            _pages[3].Children.Add(Text(S(edge, "label")));
            _pages[3].Children.Add(Choice("Group edge: " + S(edge, "label"), ["Use measured grouping", "Split here", "Join with previous group"], 0, i =>
            {
                JsonObject row = (JsonObject)edge!["row"]!.DeepClone();
                Preferences.GroupOverrides.RemoveAll(r => JsonNode.DeepEquals(r["sessionKey"], row["sessionKey"]) && JsonNode.DeepEquals(r["familyKey"], row["familyKey"]));
                if (i > 0) { row["choice"] = i == 1 ? "split" : "join"; row["chosenAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); Preferences.GroupOverrides.Add(row); }
                Changed();
            }));
        }
        foreach (var state in A(review.FlatWork, "overrides"))
        {
            _pages[3].Children.Add(Text($"{S(state?["row"], "label")}: {S(state?["status"], "status")} {S(state?["status"], "reason")}"));
            var clear = new Button { Content = "Clear group choice" };
            clear.Click += (_, _) => { Preferences.GroupOverrides.RemoveAll(r => JsonNode.DeepEquals(r["anchorFrame"], state?["row"]?["anchorFrame"]) && JsonNode.DeepEquals(r["sessionKey"], state?["row"]?["sessionKey"])); Changed(); };
            _pages[3].Children.Add(clear);
        }
        foreach (var session in A(review.Grouping, "sessions"))
        {
            _pages[3].Children.Add(Text($"{S(session, "night")}: {A(session, "frameUuids").Count} frames"));
            _pages[3].Children.Add(Text(string.Join(", ", A(session, "frameUuids").Select(id => review.Frames.FirstOrDefault(f => f.Id == id?.GetValue<string>())?.Path).OfType<string>().Select(p => Path.GetFileName(p)))));
        }
        foreach (string warning in review.Warnings) _pages[2].Children.Add(Text(warning));
    }

    private void AddLightChoices(StackPanel page, JsonObject options)
    {
        var buttons = new StackPanel { Spacing = 6 };
        string path = S(options, "path");
        var clear = new Button { Content = "Use automatic flat choices" };
        clear.Click += (_, _) => { Preferences.LightChoices.RemoveAll(c => c.Path == path); Changed(); };
        buttons.Children.Add(clear);
        var groups = A(options, "groups").ToArray();
        if (groups.Length > 0) buttons.Children.Add(Choice("Choose a flat group", ["Automatic", .. groups.Select(g => S(g, "label"))], 0,
            i => ChooseLight(options, null, i == 0 ? null : groups[i - 1], null)));
        foreach (var change in A(options, "changes")) buttons.Children.Add(Choice("Side of change " + S(change, "key"),
            ["Automatic", "Before change", "After change"], 0, i => ChooseLight(options, change, null, i == 0 ? null : i == 1 ? "before" : "after")));
        page.Children.Add(new Expander { Header = "Flat choices for " + Path.GetFileName(path), Content = buttons,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
    }
    private void ChooseLight(JsonObject options, JsonNode? change, JsonNode? group, string? side)
    {
        string path = S(options, "path"), key = change is null ? "group" : S(change, "key");
        Preferences.LightChoices.RemoveAll(c => c.Path == path && S(c.Row, "changeKey") == key);
        if (group is not null || side is not null) Preferences.LightChoices.Add(new(path, S(options, "fingerprint"), new JsonObject
        {
            ["lightGuid"] = options["guid"]?.DeepClone(), ["changeKey"] = key, ["rigUuid"] = options["rigUuid"]?.DeepClone(),
            ["changeStart"] = change?["start"]?.DeepClone(), ["changeEnd"] = change?["end"]?.DeepClone(),
            ["choice"] = side ?? "group", ["groupFrame"] = group?["frame"]?.DeepClone(),
            ["groupSessionKey"] = group?["sessionKey"]?.DeepClone(), ["label"] = group is null ? $"{side} of measured change" : S(group, "label"),
            ["chosenAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }));
        Changed();
    }
    private static JsonArray A(JsonNode? node, string key) => node?[key] is JsonArray array ? array : [];
    private static string DisplayPath(string path) => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
        ? @"\\" + path[8..] : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    private static string S(JsonNode? node, string key) => node?[key] is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";
    private static TextBlock Text(string text, bool heading = false) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true, FontWeight = heading ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal };
    private static NumberBox Number(string label, double value)
    {
        var box = new NumberBox { Header = label, Value = value, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(box, label); return box;
    }
    private static ComboBox Choice(string label, string[] values, int selected, Action<int> changed)
    {
        var box = new ComboBox { Header = label, ItemsSource = values, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(box, label);
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) changed(box.SelectedIndex); };
        return box;
    }
    public void Dispose() { _disposed = true; ++_generation; _analysis?.Cancel(); }
}
