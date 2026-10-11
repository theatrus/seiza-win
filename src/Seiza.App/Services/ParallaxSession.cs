using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Seiza.App.Models;
using Seiza.App.ViewModels;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Seiza.App.Services;

internal enum ParallaxInputFile { Separator, Objects, ObjectDistances, StarDistances }

// One independent document snapshot. Native jobs retain handles/files until they return;
// closing cancels publication, never detaches an in-flight native operation.
internal sealed class ParallaxSession : INotifyPropertyChanged, IDisposable
{
    private readonly string _sourcePath;
    private readonly FitsImageProcessingConfiguration _processing;
    private readonly DispatcherQueue _dispatcher;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Seiza-Parallax-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<ParallaxInputFile, string> _inputs = [];
    private readonly List<ParallaxComposition> _undo = [], _redo = [];
    private readonly List<string> _warnings = [], _notes = [];
    private WcsResult? _wcs;
    private ParallaxVideo? _scene, _preview;
    private string? _starless, _stars;
    private CancellationTokenSource? _operation, _refit, _playback;
    private Task? _refitTask, _frameTask;
    private int _revision, _frameRevision, _activeJobs, _catalogStatusRevision;
    private int? _pendingFrame;
    private bool _closed, _refitRequested, _automaticPreview = true, _layersAligned, _automaticSeparation;
    private double _playhead;
    private readonly CatalogSettingsViewModel _catalogs = CatalogSettingsViewModel.Instance;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ParallaxComposition Composition { get; private set; } = new();
    public Guid? SelectedStop { get; set; }
    public int? SelectedIndex => Composition.Stops.FindIndex(s => s.Id == SelectedStop) is int i && i >= 0 ? i : null;
    public string SourceName => Path.GetFileName(_sourcePath);
    public int Width { get; private set; }
    public int Height { get; private set; }
    public RenderedImageData? SourceImage { get; private set; }
    public RenderedImageData? PreviewImage { get; private set; }
    public bool SourceReady { get; private set; }
    public bool HasWcs => _wcs is not null;
    public bool HasScene => _scene is not null;
    public bool HasPreview => _preview is not null;
    public bool IsBusy { get; private set; } = true;
    public bool IsPlaying { get; private set; }
    public bool IsReconfiguring { get; private set; }
    public string Status { get; private set; } = "Preparing source snapshot…";
    public ParallaxActivity Activity { get; private set; } = new("Preparing source snapshot…");
    public double? Progress { get; private set; }
    public string? Error { get; set; }
    public IReadOnlyList<string> Warnings => _warnings;
    public IReadOnlyList<string> PreparationNotes => _notes;
    public ParallaxSummary? Summary { get; private set; }
    public string? StarlessName { get; private set; }
    public string? StarsName { get; private set; }
    public bool ShowsSource { get; set; } = true;
    public bool PickingDestination { get; set; }
    public bool PickingDistance { get; set; }
    public bool LayersAligned { get => _layersAligned; set { if (_layersAligned == value) return; _layersAligned = value; Invalidate(true); } }
    public bool AutomaticSeparation { get => _automaticSeparation; set { if (_automaticSeparation == value) return; _automaticSeparation = value; Invalidate(true); } }
    public bool AutomaticPreview
    {
        get => _automaticPreview;
        set { _automaticPreview = value; if (value) ScheduleRefit(); else { _refit?.Cancel(); _refitRequested = false; } Notify(); }
    }
    public double Playhead { get => _playhead; set { _playhead = double.IsFinite(value) ? Math.Clamp(value, 0, Math.Max(0, Composition.Duration)) : 0; Notify(nameof(Playhead)); } }
    public bool CanUndo => !IsBusy && _undo.Count > 0;
    public bool CanRedo => !IsBusy && _redo.Count > 0;
    public bool CanPrepare => SourceReady && !IsBusy && !_closed && !_catalogs.IsRunning && Composition.ValidationMessage is null &&
        (AutomaticSeparation ? _inputs.ContainsKey(ParallaxInputFile.Separator) : _starless is not null && _stars is not null && LayersAligned);
    public ParallaxTourOptions TourOptions { get; set; } = new();
    public ParallaxTourPlan? Candidate { get; private set; }
    public ParallaxVideoCodec Codec { get; set; }
    public string? ExportedPath { get; private set; }
    public IReadOnlyDictionary<ParallaxInputFile, string> InputNames => _inputs.ToDictionary(p => p.Key, p => Path.GetFileName(p.Value));
    public string CatalogStatus { get; private set; } = "Checking offline distance catalogs…";
    public string CatalogDirectoryPath { get; private set; } = "Checking the default location…";
    public bool CatalogsRunning => _catalogs.IsRunning;

    public ParallaxSession(string sourcePath, FitsImageProcessingConfiguration processing, WcsResult? wcs, DispatcherQueue dispatcher)
    {
        _sourcePath = sourcePath; _processing = processing; _wcs = wcs; _dispatcher = dispatcher;
        CatalogSettingsStore.CatalogDirectoryChanged += CatalogDirectoryChanged;
        _catalogs.PropertyChanged += CatalogChanged;
    }

    public async Task InitializeAsync()
    {
        _activeJobs++;
        try
        {
            Directory.CreateDirectory(_directory);
            if (ImageFileService.IsAstronomyImage(_sourcePath))
            {
                RenderedImage16Data source = await Task.Run(() => SeizaCore.Render16(_sourcePath, _processing));
                StorageFile file = await StorageFile.GetFileFromPathAsync(CreateSnapshotPath());
                await ImageExportService.Save16Async(source, new(file, new(ImageExportFormat.Png, ImageExportBitDepth.Sixteen, false)));
                Width = source.Width; Height = source.Height;
                // Release the full 16-bit buffer before decoding a small display preview.
                source = null!;
            }
            else
            {
                // Raster inputs are already display images. WIC preserves their
                // full channel precision and normalizes orientation without a
                // round trip through the eight-bit viewer bitmap.
                (Width, Height) = await ParallaxRasterSnapshot.SaveAsync(_sourcePath, SnapshotPath);
            }
            var preview = await Task.Run(() => SeizaCore.Render(SnapshotPath, 1280));
            if (_closed) return;
            SourceImage = preview;
            Composition.Scene.DistanceFocus = [(Width - 1) / 2.0, (Height - 1) / 2.0];
            SourceReady = true; Status = "Choose aligned starless and unscreened stars images.";
            await RefreshCatalogStatusAsync();
        }
        catch (Exception e) { if (!_closed) { Error = e.Message; Status = "Source snapshot failed."; } }
        finally { IsBusy = false; Activity.FinishedAt = DateTimeOffset.UtcNow; JobFinished(); Notify(); }
    }
    private string SnapshotPath => Path.Combine(_directory, "source.png");
    private string CreateSnapshotPath() { using (File.Create(SnapshotPath)) { } return SnapshotPath; }

    public void Edit(Action<ParallaxComposition> edit)
    {
        if (_closed || IsBusy) return;
        ParallaxComposition previous = Composition, updated = previous.DeepClone();
        edit(updated);
        if (previous.ContentEquals(updated)) return;
        _undo.Add(previous); if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear(); Composition = updated;
        Invalidate(!previous.Scene.ContentEquals(updated.Scene));
        if (previous.Scene.GaiaMaxMagnitude != updated.Scene.GaiaMaxMagnitude) _ = RefreshCatalogStatusAsync();
    }
    public void EditStop(Action<ParallaxStop> edit) { if (SelectedIndex is int i) Edit(c => edit(c.Stops[i])); }
    public void Undo() => Restore(_undo, _redo);
    public void Redo() => Restore(_redo, _undo);
    private void Restore(List<ParallaxComposition> from, List<ParallaxComposition> to)
    {
        if (IsBusy || from.Count == 0) return;
        var previous = Composition; to.Add(previous); Composition = from[^1]; from.RemoveAt(from.Count - 1);
        Invalidate(!previous.Scene.ContentEquals(Composition.Scene));
        if (previous.Scene.GaiaMaxMagnitude != Composition.Scene.GaiaMaxMagnitude) _ = RefreshCatalogStatusAsync();
    }
    private void ReleasePreview()
    {
        if (!ReferenceEquals(_preview, _scene)) _preview?.Dispose();
        _preview = null; Summary = null;
    }
    private void Invalidate(bool scene)
    {
        _revision++; _frameRevision++; _pendingFrame = null;
        _refit?.Cancel(); _refitRequested = false;
        ReleasePreview(); StopPlayback();
        if (scene) { _scene?.Dispose(); _scene = null; PreviewImage = null; }
        Playhead = _playhead;
        if (SourceReady && !IsBusy) Status = _scene is null ? "Scene changed. Prepare Preview to apply it." : AutomaticPreview ? "Camera changed. Refitting preview…" : "Camera changed. Update Preview to apply it.";
        if (!scene && AutomaticPreview) ScheduleRefit();
        Notify();
    }

    public async Task ImportLayerAsync(string path, bool starless)
    {
        if (!SourceReady || IsBusy || _closed) return;
        IsBusy = true; _activeJobs++; _refit?.Cancel(); Notify();
        try
        {
            if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".tif" or ".tiff")) throw new InvalidDataException("Choose a PNG or TIFF layer.");
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            var orientation = await decoder.BitmapProperties.GetPropertiesAsync(["System.Photo.Orientation"]);
            bool rotated = orientation.TryGetValue("System.Photo.Orientation", out var transform) && Convert.ToUInt32(transform.Value, System.Globalization.CultureInfo.InvariantCulture) is not (0 or 1);
            if (decoder.PixelWidth != Width || decoder.PixelHeight != Height || rotated)
                throw new InvalidDataException($"Layers must match the source’s {Width} × {Height} pixels and have no orientation transform. Export aligned, stretched PNG or TIFF layers.");
            string copy = Path.Combine(_directory, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
            await Task.Run(() => File.Copy(path, copy));
            if (_closed) return;
            if (starless) { _starless = copy; StarlessName = Path.GetFileName(path); }
            else { _stars = copy; StarsName = Path.GetFileName(path); }
            _layersAligned = false; Invalidate(true);
            Status = "Confirm that both layers match the source alignment and stretch.";
        }
        catch (Exception e) { if (!_closed) Error = e.Message; }
        finally { IsBusy = false; JobFinished(); Notify(); }
    }
    public Task ChooseInputAsync(ParallaxInputFile kind, string path)
    {
        if (!IsBusy)
        {
            if (!File.Exists(path) && !(kind == ParallaxInputFile.Objects && Directory.Exists(path))) throw new FileNotFoundException("The selected input was not found.", path);
            _inputs[kind] = Path.GetFullPath(path); Invalidate(true);
        }
        return Task.CompletedTask;
    }
    public void ClearInput(ParallaxInputFile kind) { if (!IsBusy) { _inputs.Remove(kind); Invalidate(true); } }
    public void PickDistanceReference() { PickingDestination = false; PickingDistance = true; ShowsSource = true; Notify(); }
    public void Pick(double x, double y)
    {
        if (PickingDistance) { Edit(c => { c.Scene.DistanceFocus = [x, y]; c.DistanceName = "Custom point"; }); PickingDistance = false; }
        else if (PickingDestination)
        {
            if (Composition.Motion == ParallaxMotion.Tour) EditStop(s => { s.Focus = [x, y]; s.Name = "Custom Point"; });
            else Edit(c => { c.Focus = [x, y]; c.FocusName = "Custom Point"; });
            PickingDestination = false;
        }
        Notify();
    }
    public void AddStop(bool whole = false)
    {
        var stop = new ParallaxStop { Name = whole ? "Whole Image" : "Custom Point", Focus = whole ? null : [Width / 2.0, Height / 2.0], Dolly = whole ? 0 : 0.4 };
        stop.RotateDegrees = SelectedIndex is int i ? Composition.Stops[i].RotateDegrees : 0;
        int index = SelectedIndex is int j ? j + 1 : Composition.Stops.Count;
        Edit(c => c.Stops.Insert(index, stop)); SelectedStop = stop.Id; Notify();
    }
    public void DuplicateStop()
    {
        if (SelectedIndex is not int i) return;
        var stop = Composition.Stops[i].DeepClone() with { Id = Guid.NewGuid() };
        Edit(c => c.Stops.Insert(i + 1, stop)); SelectedStop = stop.Id; Notify();
    }
    public void MoveStop(int offset)
    {
        if (SelectedIndex is not int i || i + offset < 0 || i + offset >= Composition.Stops.Count) return;
        Edit(c => { var stop = c.Stops[i]; c.Stops.RemoveAt(i); c.Stops.Insert(i + offset, stop); });
    }
    public void RemoveStop()
    {
        if (SelectedIndex is not int i || Composition.Stops.Count <= 2) return;
        Edit(c => c.Stops.RemoveAt(i)); SelectedStop = Composition.Stops[Math.Min(i, Composition.Stops.Count - 1)].Id; Notify();
    }
    public void SelectStop(Guid id) { SelectedStop = id; if (SelectedIndex is int i) Playhead = Composition.ArrivalAt(i); RequestFrame(); Notify(); }

    private ParallaxRequest Request(bool preview)
    {
        if (!SourceReady) throw new InvalidOperationException("The source snapshot is not ready.");
        if (Composition.ValidationMessage is string message) throw new InvalidDataException(message);
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Seiza", "ParallaxDistances");
        Directory.CreateDirectory(cache);
        return ParallaxRequestSource.Create(Composition, preview, SnapshotPath, AutomaticSeparation, _starless, _stars,
            new()
            {
                Wcs = _wcs, CatalogDirectory = CatalogSettingsStore.LoadCatalogDirectory(), GaiaCache = cache,
                Objects = _inputs.GetValueOrDefault(ParallaxInputFile.Objects), ObjectDistances = _inputs.GetValueOrDefault(ParallaxInputFile.ObjectDistances),
                StarDistances = _inputs.GetValueOrDefault(ParallaxInputFile.StarDistances),
                RcAstroExecutable = AutomaticSeparation ? _inputs.GetValueOrDefault(ParallaxInputFile.Separator) : null, RcAstroHost = "seiza-win",
            });
    }
    private (CancellationTokenSource Cancellation, int Revision) Begin(string message)
    {
        StopPlayback(); _refit?.Cancel(); _refitRequested = false; _revision++;
        IsBusy = true; Status = message; Progress = null; Error = null; _warnings.Clear(); _notes.Clear();
        Activity = new(message);
        _operation = new(); _activeJobs++; Notify(); return (_operation, _revision);
    }
    private Progress<ParallaxEvent> Events(int revision) => new(e =>
    {
        if (_closed || revision != _revision || (!IsBusy && !IsReconfiguring)) return;
        Activity.Receive(e);
        if (e.Kind == "warning" && e.Message is string warning) { if (!_warnings.Contains(warning)) _warnings.Add(warning); }
        else if (e.Kind == "split") Progress = e.Fraction;
        else if (e.Message is string note) { _notes.Add(note); if (_notes.Count > 100) _notes.RemoveAt(0); if (_operation?.IsCancellationRequested != true && _refit?.IsCancellationRequested != true) Status = note; Progress = null; }
        Notify();
    });
    private void Finish(CancellationTokenSource cancellation, Exception? error)
    {
        if (!_closed)
        {
            if (cancellation.IsCancellationRequested || error is OperationCanceledException) Status = "Cancelled.";
            else if (error is not null) { Error = error.Message; Status = "Could not complete the operation."; }
            IsBusy = false; Progress = null;
            Activity.FinishedAt = DateTimeOffset.UtcNow;
        }
        if (ReferenceEquals(_operation, cancellation)) _operation = null;
        cancellation.Dispose(); JobFinished(); Notify();
    }
    public async Task GenerateTourAsync()
    {
        if (!SourceReady || IsBusy || _closed || _catalogs.IsRunning) return;
        if (!double.IsFinite(TourOptions.Hold) || TourOptions.Hold < 0 || !double.IsFinite(TourOptions.Motion) || TourOptions.Motion < 0) { Error = "Tour hold and motion must be finite and nonnegative."; Notify(); return; }
        ParallaxRequest request;
        try { request = Request(false); } catch (Exception e) { Error = e.Message; Notify(); return; }
        request.AutoTour = TourOptions with { }; request.Video.Tour = [];
        var (signal, generation) = Begin(HasWcs ? "Planning tour…" : "Solving image and planning tour…");
        try
        {
            await ParallaxRequestSource.EnsureWcsAsync(request,
                path => Task.Run(() => SeizaCore.Solve(path, request.Inputs.CatalogDirectory, request.Scene.MinimumScaleArcsecPerPixel, request.Scene.MaximumScaleArcsecPerPixel).Wcs),
                signal.Token);
            var plan = await ParallaxCore.PlanAsync(request, events: Events(generation), cancellationToken: signal.Token);
            if (!_closed && generation == _revision) { _wcs = request.Inputs.Wcs; Candidate = plan; Status = $"Generated {plan.Tour.Count} stops. Apply the tour or keep your draft."; }
            Finish(signal, null);
        }
        catch (Exception e) { Finish(signal, e); }
    }
    public void ApplyCandidate()
    {
        if (Candidate is not { } plan) return;
        Edit(c =>
        {
            c.Motion = ParallaxMotion.Tour; c.Stops = plan.Tour.Select(s => s.DeepClone()).ToList();
            c.Scene.DistanceFocus = plan.Focus.ToArray(); c.DistanceName = plan.FocusName;
            c.TourGlide = plan.TourGlide ?? c.TourGlide; c.TourTitles = plan.TourTitles ?? c.TourTitles; c.TourLoop = plan.TourLoop ?? c.TourLoop;
        });
        SelectedStop = plan.Tour[0].Id; Candidate = null; Notify();
    }
    public void DiscardCandidate() { Candidate = null; Notify(); }
    public async Task LoadTourAsync(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("The tour JSON exceeds the 4 MiB limit.");
            var plan = ParallaxTourPlan.FromJson(await File.ReadAllTextAsync(path));
            Candidate = plan; Status = "Tour loaded. Apply it to replace the current draft.";
        }
        catch (Exception e) { Error = e.Message; }
        Notify();
    }
    public async Task SaveTourAsync(string path)
    {
        try
        {
            if (Composition.ValidationMessage is string error) throw new InvalidDataException(error);
            var plan = new ParallaxTourPlan { Focus = Composition.Scene.DistanceFocus ?? [(Width - 1) / 2.0, (Height - 1) / 2.0], FocusName = Composition.DistanceName, Seconds = Composition.Duration, Tour = Composition.Stops, TourGlide = Composition.TourGlide, TourTitles = Composition.TourTitles, TourLoop = Composition.TourLoop };
            string stage = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".seiza-tour-" + Guid.NewGuid().ToString("N") + ".json");
            try { await File.WriteAllTextAsync(stage, plan.ToJson()); File.Move(stage, path, true); }
            finally { File.Delete(stage); }
        }
        catch (Exception e) { Error = e.Message; }
        Notify();
    }
    public async Task PreparePreviewAsync()
    {
        if (!CanPrepare) return;
        if (_scene is not null) { ScheduleRefit(true); return; }
        ParallaxRequest request = Request(true);
        var (signal, generation) = Begin(HasWcs ? "Loading images and finding stars…" : "Loading images, solving sky coordinates and finding stars…");
        try
        {
            var video = await ParallaxCore.PrepareAsync(request, events: Events(generation), cancellationToken: signal.Token);
            if (_closed || generation != _revision) video.Dispose();
            else
            {
                ReleasePreview(); _scene?.Dispose(); _scene = _preview = video; Summary = video.Summary; _wcs = Summary.Wcs;
                Status = $"Preview ready. {Summary.WithDistance} of {Summary.DetectedStars} stars have catalog distances."; ShowsSource = false;
            }
            Finish(signal, null); RequestFrame();
        }
        catch (Exception e) { Finish(signal, e); }
    }
    private void ScheduleRefit(bool force = false)
    {
        if (_scene is null || _closed || IsBusy || (!AutomaticPreview && !force)) return;
        _refitRequested = true; _refit?.Cancel();
        if (_refitTask is not null) return;
        _refitTask = RefitLoopAsync();
    }
    private async Task RefitLoopAsync()
    {
        IsReconfiguring = true; _activeJobs++; Notify();
        Activity = new("Fitting camera to the prepared scene…");
        try
        {
            while (_refitRequested && !_closed && !IsBusy && _scene is not null)
            {
                await Task.Delay(300);
                if (!_refitRequested || _closed || IsBusy || _scene is not { } scene) break;
                _refitRequested = false;
                if (Composition.ValidationMessage is string invalid) { Status = invalid; Notify(); continue; }
                using var signal = new CancellationTokenSource(); _refit = signal;
                int generation = _revision;
                try
                {
                    var video = await ParallaxCore.ReconfigureAsync(scene, Composition.VideoSettings(true), events: Events(generation), cancellationToken: signal.Token);
                    if (_closed || IsBusy || generation != _revision || signal.IsCancellationRequested) video.Dispose();
                    else { ReleasePreview(); _preview = video; Summary = video.Summary; Status = "Preview updated using the prepared scene."; RequestFrame(); }
                }
                catch (Exception e) { if (!_closed && generation == _revision && !signal.IsCancellationRequested) { Error = e.Message; Status = "Could not refit preview."; } }
                finally { if (ReferenceEquals(_refit, signal)) _refit = null; }
            }
        }
        finally { _refitTask = null; IsReconfiguring = false; if (!IsBusy) Activity.FinishedAt = DateTimeOffset.UtcNow; JobFinished(); Notify(); }
    }
    public void RequestFrame()
    {
        if (_preview is null || _closed) return;
        _frameRevision++; _pendingFrame = Math.Clamp((int)(Playhead * _preview.Summary.Fps), 0, _preview.Summary.Frames - 1);
        if (_frameTask is null) _frameTask = FrameLoopAsync();
    }
    private async Task FrameLoopAsync()
    {
        _activeJobs++;
        await Task.Yield(); // Publish the running task before a very fast native render can finish.
        try
        {
            while (_pendingFrame is int index && _preview is { } video && !_closed)
            {
                _pendingFrame = null; int generation = _frameRevision;
                try
                {
                    byte[] bytes = await Task.Run(() => video.RenderBgraFrame(index));
                    if (!_closed && generation == _frameRevision) { PreviewImage = new(bytes, video.Width, video.Height, SourceImage!.Metadata); Notify(nameof(PreviewImage)); }
                }
                catch (Exception e) { if (generation == _frameRevision && !_closed) { Error = e.Message; StopPlayback(); Notify(); } }
            }
        }
        finally { _frameTask = null; JobFinished(); }
    }
    public void TogglePlayback()
    {
        if (IsPlaying) { StopPlayback(); return; }
        if (_preview is null) return;
        if (Playhead >= Composition.Duration) Playhead = 0;
        ShowsSource = false; IsPlaying = true; _playback = new();
        _ = PlaybackAsync(_playback.Token); Notify();
    }
    private async Task PlaybackAsync(CancellationToken token)
    {
        double start = Playhead; var clock = Stopwatch.StartNew();
        try
        {
            while (!token.IsCancellationRequested && !_closed && _preview is not null && IsPlaying)
            {
                if (_frameTask is null)
                {
                    double elapsed = start + clock.Elapsed.TotalSeconds;
                    Playhead = Composition.Motion == ParallaxMotion.Tour && Composition.TourLoop ? elapsed % Composition.Duration : Math.Min(Composition.Duration, elapsed);
                    RequestFrame(); if (Playhead >= Composition.Duration) break;
                }
                await Task.Delay(33, token);
            }
        }
        catch (OperationCanceledException) { }
        finally { if (!token.IsCancellationRequested) { IsPlaying = false; Notify(); } }
    }
    public void StopPlayback() { IsPlaying = false; _playback?.Cancel(); _playback?.Dispose(); _playback = null; Notify(nameof(IsPlaying)); }
    public async Task ExportVideoAsync(string destination)
    {
        if (!CanPrepare) return;
        ParallaxRequest request = Request(false); ParallaxVideo? existing = _scene; var codec = Codec;
        var (signal, generation) = Begin(existing is null ? "Preparing full-resolution video…" : "Fitting full-resolution video…");
        ParallaxVideo? video = null;
        bool retained = false;
        try
        {
            video = existing is null ? await ParallaxCore.PrepareAsync(request, events: Events(generation), cancellationToken: signal.Token) : await ParallaxCore.ReconfigureAsync(existing, request.Video, events: Events(generation), cancellationToken: signal.Token);
            signal.Token.ThrowIfCancellationRequested();
            if (_closed || generation != _revision) throw new OperationCanceledException();
            // Export-first workflows retain the expensive scene too. Keep an older
            // preview independently owned while replacing its scene reference.
            if (!ReferenceEquals(_scene, _preview)) _scene?.Dispose();
            _scene = video; retained = true;
            Summary = video.Summary; _wcs = Summary.Wcs; Status = "Encoding video…"; Notify();
            var progress = new Progress<ParallaxExportProgress>(p => { if (!_closed && IsBusy && ReferenceEquals(_operation, signal) && generation == _revision && !signal.IsCancellationRequested) { Progress = p.Fraction; Status = p.CompletedFrames == p.TotalFrames ? "Finalizing video…" : $"Encoding frame {p.CompletedFrames} of {p.TotalFrames}…"; Notify(); } });
            await ParallaxVideoExporter.ExportAsync(video, destination, codec, progress, signal.Token);
            if (!_closed) { ExportedPath = destination; Status = "Video exported."; }
            Finish(signal, null);
        }
        catch (Exception e) { Finish(signal, e); }
        finally { if (!retained) video?.Dispose(); }
    }

    public async Task DownloadCatalogsAsync() => await _catalogs.StartParallaxSetupAsync();
    private async Task RefreshCatalogStatusAsync()
    {
        int generation = ++_catalogStatusRevision;
        try
        {
            string? path = CatalogSettingsStore.LoadCatalogDirectory(); double magnitude = Composition.Scene.GaiaMaxMagnitude;
            var status = await Task.Run(() => ParallaxCatalogService.Describe(path, magnitude));
            if (!_closed && !_catalogs.IsRunning && generation == _catalogStatusRevision && path == CatalogSettingsStore.LoadCatalogDirectory())
            { CatalogStatus = status.Description; CatalogDirectoryPath = status.Directory; Notify(); }
        }
        catch (Exception e) { if (!_closed && generation == _catalogStatusRevision) { CatalogStatus = e.Message; Notify(nameof(CatalogStatus)); } }
    }
    private void CatalogDirectoryChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() => { if (!_closed) { Cancel(); Invalidate(true); _ = RefreshCatalogStatusAsync(); } });
    private void CatalogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed) return;
        if (_catalogs.IsRunning)
        {
            if (e.PropertyName == nameof(_catalogs.IsRunning)) { Cancel(); Invalidate(true); }
            CatalogStatus = _catalogs.SetupMessage + " " + _catalogs.SetupDetail; Notify();
        }
        else if (e.PropertyName == nameof(_catalogs.IsRunning)) { Cancel(); Invalidate(true); _ = RefreshCatalogStatusAsync(); }
    }
    public void Cancel()
    {
        _operation?.Cancel(); _refit?.Cancel(); _refitRequested = false;
        Activity.CancellationRequested = true;
        Status = IsBusy ? "Stopping after the current native step…" : "Preview update cancelled."; Notify();
    }
    public void Close()
    {
        if (_closed) return;
        _closed = true; Cancel(); StopPlayback(); _frameRevision++; _pendingFrame = null;
        CatalogSettingsStore.CatalogDirectoryChanged -= CatalogDirectoryChanged; _catalogs.PropertyChanged -= CatalogChanged;
        ReleasePreview(); _scene?.Dispose(); _scene = null; SourceImage = PreviewImage = null;
        TryCleanup();
    }
    public void Dispose() { Close(); GC.SuppressFinalize(this); }
    private void JobFinished() { _activeJobs--; TryCleanup(); }
    private void TryCleanup()
    {
        if (!_closed || _activeJobs != 0) return;
        // Only this session's explicitly created GUID directory is removed.
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private void Notify(string? property = null) { if (!_closed) PropertyChanged?.Invoke(this, new(property)); }
}
