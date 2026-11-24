using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Seiza.App.Models;
using Seiza.App.ViewModels;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Seiza.App.Services;

internal enum ParallaxInputFile { Separator, Objects, ObjectDistances, StarDistances, DustMap }

// One independent document snapshot. Native jobs retain handles/files until they return;
// closing cancels publication, never detaches an in-flight native operation.
internal sealed class ParallaxSession : INotifyPropertyChanged, IDisposable
{
    private readonly string _sourcePath;
    private readonly FitsImageProcessingConfiguration _processing;
    private readonly DispatcherQueue _dispatcher;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Seiza-Parallax-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<ParallaxInputFile, string> _inputs = [];
    private readonly ParallaxInputHistory _protectedInputHistory = new();
    private readonly List<ParallaxComposition> _undo = [], _redo = [];
    private readonly List<string> _warnings = [], _notes = [];
    private WcsResult? _wcs;
    private readonly Func<SolveResult?>? _readDocumentSolve;
    private readonly Action<SolveResult>? _publishDocumentSolve;
    private SolveResult? _lastDocumentSolve;
    public SolveResult? SolveInformation { get; private set; }
    private ParallaxVideo? _scene, _preview;
    private string? _starless, _stars, _starlessSourcePath, _starsSourcePath, _paintedDustMapPath;
    private CancellationTokenSource? _operation, _refit, _playback;
    private Task? _refitTask, _frameTask;
    private int _revision, _frameRevision, _activeJobs, _catalogStatusRevision;
    private readonly DeferredSettingsUpdate _rcAstroSettingsUpdate = new();
    private int? _pendingFrame;
    private bool _closed, _refitRequested, _automaticPreview = true, _layersAligned = true, _automaticSeparation, _sourceIsStarless;
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
    public bool HasWcs => _wcs is not null || _readDocumentSolve?.Invoke() is not null;
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
    public bool PickingCrop { get; set; }
    public int[] FilmedRectangle => Composition.Scene.Crop?.ToArray() ?? [0, 0, Width, Height];
    public double[] ImageCenter => [FilmedRectangle[0] + (FilmedRectangle[2] - 1) / 2.0,
        FilmedRectangle[1] + (FilmedRectangle[3] - 1) / 2.0];
    public string? ValidationMessage => Composition.ValidationMessage ??
        (Composition.Stops.Any(stop => stop.PullBackToWholeImage) && !ParallaxCore.SupportsPullBackToWholeImage
            ? "Whole-image pull-back requires a native core containing Seiza #283." : null) ??
        ((Composition.Scene.Crop is not null || Composition.Framing == "fit") && !ParallaxCore.SupportsFraming
            ? "Cropping and whole-image framing require Seiza 0.32.0 or newer." : null);
    public bool LayersAligned { get => _layersAligned; set { if (_layersAligned == value) return; _layersAligned = value; Invalidate(true); } }
    public string? TourSolveIssue => ParallaxRequestSource.SolveIssue(HasWcs, _sourceIsStarless, AutomaticSeparation ? null : _stars, LayersAligned);
    public string SolvingDescription => SolveInformation?.SourceDescription ?? (HasWcs ? "Sky coordinates available" :
        _sourceIsStarless ? TourSolveIssue ?? "The aligned Stars layer will be solved directly; the starless original and snapshot will not be solved." :
        "The original astronomy file's headers are tried before solving the private stars/snapshot.");
    public string? PreparationIssue => !SourceReady ? "Wait for the source snapshot to finish loading." :
        _catalogs.IsRunning ? "Wait for catalogue setup to finish." :
        AutomaticSeparation ? (!_inputs.TryGetValue(ParallaxInputFile.Separator, out string? executable) || !File.Exists(executable)
            ? "Choose an existing RC-Astro executable in Settings." : ValidationMessage ?? TourSolveIssue) :
        _starless is null || _stars is null ? "Choose both Starless and Stars images in Source." :
        !LayersAligned ? "Enable Layers match source alignment and stretch in Source to prepare the selected pair." : ValidationMessage;
    public bool AutomaticSeparation { get => _automaticSeparation; set { if (_automaticSeparation == value) return; _automaticSeparation = value; Invalidate(true); } }
    public bool AutomaticPreview
    {
        get => _automaticPreview;
        set { _automaticPreview = value; if (value) ScheduleRefit(); else { _refit?.Cancel(); _refitRequested = false; } Notify(); }
    }
    public double Playhead { get => _playhead; set { _playhead = double.IsFinite(value) ? Math.Clamp(value, 0, Math.Max(0, Composition.Duration)) : 0; Notify(nameof(Playhead)); } }
    public bool CanUndo => !IsBusy && _undo.Count > 0;
    public bool CanRedo => !IsBusy && _redo.Count > 0;
    public bool CanPrepare => !IsBusy && !_closed && PreparationIssue is null;
    public ParallaxTourOptions TourOptions { get; set; } = new();
    public ParallaxTourPlan? Candidate { get; private set; }
    public ParallaxVideoCodec Codec { get; set; }
    public ParallaxExportFormat ExportFormat { get; set; }
    public string? FfmpegPath { get; set; }
    public bool ExportCountedDustMap { get; set; }
    public string? ExportedPath { get; private set; }
    public IReadOnlyDictionary<ParallaxInputFile, string> InputNames => _inputs.ToDictionary(p => p.Key,
        p => p.Key == ParallaxInputFile.DustMap && p.Value == _paintedDustMapPath ? "Painted in Seiza" : Path.GetFileName(p.Value));
    public string CatalogStatus { get; private set; } = "Checking offline distance catalogs…";
    public string CatalogDirectoryPath { get; private set; } = "Checking the default location…";
    public bool CatalogsRunning => _catalogs.IsRunning;

    public ParallaxSession(string sourcePath, FitsImageProcessingConfiguration processing, WcsResult? wcs, DispatcherQueue dispatcher,
        Func<SolveResult?>? readDocumentSolve = null, Action<SolveResult>? publishDocumentSolve = null)
    {
        _sourcePath = sourcePath; _processing = processing; _wcs = wcs; _dispatcher = dispatcher;
        _readDocumentSolve = readDocumentSolve; _publishDocumentSolve = publishDocumentSolve;
        SolveInformation = _lastDocumentSolve = readDocumentSolve?.Invoke();
        _protectedInputHistory.Remember([sourcePath]);
        CatalogSettingsStore.CatalogDirectoryChanged += CatalogDirectoryChanged;
        CatalogSettingsStore.RcAstroExecutableChanged += RcAstroExecutableChanged;
        LoadRcAstroExecutable();
        _catalogs.PropertyChanged += CatalogChanged;
    }

    private void LoadRcAstroExecutable()
    {
        if (CatalogSettingsStore.LoadRcAstroExecutable() is string path)
        {
            _inputs[ParallaxInputFile.Separator] = path;
            _protectedInputHistory.Remember([path]);
        }
        else
        {
            _inputs.Remove(ParallaxInputFile.Separator);
        }
    }

    private void RcAstroExecutableChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        if (_closed) return;
        _rcAstroSettingsUpdate.Request();
        ApplyPendingRcAstroSettings();
    });

    private void ApplyPendingRcAstroSettings()
    {
        // Keep active native handles and their inputs unchanged until all work,
        // including frame rendering and refits, has finished. Coalesce changes.
        if (!_rcAstroSettingsUpdate.TryTake(_closed, IsBusy, _activeJobs)) return;
        LoadRcAstroExecutable();
        Invalidate(true);
        Notify();
    }

    public async Task InitializeAsync()
    {
        _activeJobs++;
        try
        {
            Directory.CreateDirectory(_directory);
            if (ImageFileService.IsAstronomyImage(_sourcePath))
            {
                RenderedImage16Data source = await ImageRenderService.Render16Async(_sourcePath, _processing);
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
            Composition.SourceWidth = Width; Composition.SourceHeight = Height;
            Composition.Scene.DistanceFocus = [(Width - 1) / 2.0, (Height - 1) / 2.0];
            SourceReady = true; Status = "Choose aligned starless and unscreened stars images.";
            await RefreshCatalogStatusAsync();
        }
        catch (Exception e) { if (!_closed) { Error = e.Message; Status = "Source snapshot failed."; } }
        finally { IsBusy = false; Activity.FinishedAt = DateTimeOffset.UtcNow; JobFinished(); Notify(); }
    }
    private string SnapshotPath => Path.Combine(_directory, "source.png");
    private string CreateSnapshotPath() { using (File.Create(SnapshotPath)) { } return SnapshotPath; }

    public void Edit(Action<ParallaxComposition> edit, ParallaxComposition? continueUndoFrom = null)
    {
        if (_closed || IsBusy) return;
        ParallaxComposition previous = Composition, updated = previous.DeepClone();
        edit(updated);
        if (previous.ContentEquals(updated)) return;
        if (!ReferenceEquals(previous, continueUndoFrom))
        {
            _undo.Add(previous);
            if (_undo.Count > 100) _undo.RemoveAt(0);
        }
        _redo.Clear(); Composition = updated;
        _protectedInputHistory.Remember(ParallaxInputPaths.FromComposition(updated));
        Invalidate(!previous.Scene.ContentEquals(updated.Scene));
        if (previous.Scene.GaiaMaxMagnitude != updated.Scene.GaiaMaxMagnitude) _ = RefreshCatalogStatusAsync();
    }
    public void EditStop(Action<ParallaxStop> edit) { if (SelectedIndex is int i) Edit(c => edit(c.Stops[i])); }
    public void EditStopCamera(Action<ParallaxStop> edit) => EditStop(stop =>
    {
        if (ParallaxCore.SupportsPullBackToWholeImage) stop.EditCamera(edit);
        else edit(stop);
    });
    public void SetCrop(int[]? crop)
    {
        if (_closed || IsBusy || !SourceReady) return;
        if (!ParallaxGeometry.CropInside(crop, Width, Height))
        { Error = "The crop must lie inside the source and be at least 16 pixels each way."; Notify(); return; }
        if (crop is not null && !ParallaxCore.SupportsFraming)
        { Error = "Video cropping requires Seiza 0.32.0 or newer."; Notify(); return; }
        Edit(c =>
        {
            c.Scene.Crop = crop?.ToArray();
            int[] area = crop ?? [0, 0, Width, Height];
            double[] center = [area[0] + (area[2] - 1) / 2.0, area[1] + (area[3] - 1) / 2.0];
            if (c.DistanceName == "Image center") c.Scene.DistanceFocus = center;
            if (c.FocusName == "Image center" && c.Focus is not null) c.Focus = center.ToArray();
        });
        PickingCrop = false; Error = null; Notify();
    }
    public void Undo() => Restore(_undo, _redo);
    public void Redo() => Restore(_redo, _undo);
    private void Restore(List<ParallaxComposition> from, List<ParallaxComposition> to)
    {
        if (IsBusy || from.Count == 0) return;
        var previous = Composition; to.Add(previous); Composition = from[^1]; from.RemoveAt(from.Count - 1);
        _protectedInputHistory.Remember(ParallaxInputPaths.FromComposition(Composition));
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
            if (starless) { _starless = copy; _starlessSourcePath = Path.GetFullPath(path); StarlessName = Path.GetFileName(path);
                _sourceIsStarless |= ParallaxRequestSource.SameSource(_sourcePath, path); }
            else { _stars = copy; _starsSourcePath = Path.GetFullPath(path); StarsName = Path.GetFileName(path); }
            _protectedInputHistory.Remember([path]);
            Invalidate(true);
            Status = PreparationIssue ?? "Layers ready. Prepare Preview or generate a tour.";
        }
        catch (Exception e) { if (!_closed) Error = e.Message; }
        finally { IsBusy = false; JobFinished(); Notify(); }
    }
    public Task ChooseInputAsync(ParallaxInputFile kind, string path)
    {
        if (kind == ParallaxInputFile.DustMap) return ChooseDustMapAsync(path);
        if (!IsBusy)
        {
            if (!File.Exists(path) && !(kind == ParallaxInputFile.Objects && Directory.Exists(path))) throw new FileNotFoundException("The selected input was not found.", path);
            _inputs[kind] = Path.GetFullPath(path); Invalidate(true);
            _protectedInputHistory.Remember([path]);
        }
        return Task.CompletedTask;
    }
    public void ClearInput(ParallaxInputFile kind)
    {
        if (IsBusy) return;
        if (kind == ParallaxInputFile.DustMap) { _ = ClearDustMapAsync(); return; }
        _inputs.Remove(kind); Invalidate(true);
    }

    // Dust maps. A prepared scene takes a new map without being prepared again:
    // only its dust changes, and the result becomes the scene that refits and
    // exports start from, so the map stays with them.
    public bool HasDustMap => _inputs.ContainsKey(ParallaxInputFile.DustMap);
    public bool CanReloadDustMap => HasDustMap && _scene is not null && !IsBusy && !_closed && Composition.Scene.Dust;
    public bool CanExportDustMap => (_preview ?? _scene) is not null && !IsBusy && !_closed;
    public bool CanEditDustMap => CanExportDustMap && Composition.Scene.Dust;
    public string DustDescription => !Composition.Scene.Dust ? "Dust dimming is off." : Summary switch
    {
        null => HasDustMap ? "The dust map applies when the scene is prepared." : "Dust is counted from the stars when the scene is prepared.",
        { DustGiven: true } summary => $"Dust from the map: {summary.StarsInFrontOfDust:N0} stars seen through opaque dust fly in front of the nebula.",
        _ => "Dust counted from the stars.",
    };
    private async Task ChooseDustMapAsync(string path)
    {
        if (IsBusy || _closed) return;
        if (!File.Exists(path)) throw new FileNotFoundException("The selected dust map was not found.", path);
        if (!Composition.Scene.Dust) { Error = "Turn on dust dimming to use a dust map."; Notify(); return; }
        string full = Path.GetFullPath(path);
        _protectedInputHistory.Remember([full]);
        if (_scene is null) { _inputs[ParallaxInputFile.DustMap] = full; _paintedDustMapPath = null; Invalidate(true); return; }
        if (await SwapDustAsync(full, "Applying the dust map…")) { _inputs[ParallaxInputFile.DustMap] = full; _paintedDustMapPath = null; }
        Notify();
    }
    private async Task ClearDustMapAsync()
    {
        // Without a scene, or with the dust off, no prepared scene holds the map.
        if (_scene is null || !Composition.Scene.Dust) { _inputs.Remove(ParallaxInputFile.DustMap); _paintedDustMapPath = null; Notify(); return; }
        if (await SwapDustAsync(null, "Returning to the dust counted from the stars…")) { _inputs.Remove(ParallaxInputFile.DustMap); _paintedDustMapPath = null; }
        Notify();
    }
    // Read the chosen map again, after it was edited in another program.
    public async Task ReloadDustMapAsync()
    {
        if (!CanReloadDustMap || _inputs.GetValueOrDefault(ParallaxInputFile.DustMap) is not { } path) return;
        await SwapDustAsync(path, "Reloading the dust map…");
    }
    public async Task<ParallaxDustEditResult?> ReadDustMapForEditingAsync(bool counted = false)
    {
        if (!CanEditDustMap || (_preview ?? _scene) is not { } source) return null;
        double scale = Math.Min(1, 768.0 / Math.Max(Width, Height));
        int width = Math.Max(1, (int)Math.Round(Width * scale)), height = Math.Max(1, (int)Math.Round(Height * scale));
        bool resumeRefit = _preview is null || _refitRequested || IsReconfiguring;
        var (signal, generation) = Begin("Reading the dust map…");
        try
        {
            float[] opacity = await Task.Run(() => source.DustMap(width, height, counted) ?? new float[checked(width * height)], signal.Token);
            signal.Token.ThrowIfCancellationRequested();
            if (_closed || generation != _revision) throw new OperationCanceledException(signal.Token);
            Finish(signal, null);
            if (resumeRefit) ScheduleRefit();
            return new(opacity, width, height);
        }
        catch (Exception e)
        {
            bool cancelled = signal.IsCancellationRequested || e is OperationCanceledException;
            Finish(signal, e);
            if (resumeRefit && !cancelled) ScheduleRefit();
            return null;
        }
    }

    public async Task ApplyPaintedDustMapAsync(float[] opacity, int width, int height)
    {
        if (!CanEditDustMap) { Error = "Prepare a preview with dust dimming on before applying a painted map."; Notify(); return; }
        string path = Path.Combine(_directory, "painted-dust-" + Guid.NewGuid().ToString("N") + ".png");
        float[] snapshot = opacity.ToArray();
        bool snapshotCreated = false;
        async Task PrepareSnapshot(CancellationToken token)
        {
            await ParallaxDustMapSnapshot.SaveAsync(snapshot, width, height, path, token);
            snapshotCreated = true;
        }
        bool applied = await SwapDustAsync(path, "Applying the painted dust map…", PrepareSnapshot);
        if (applied) { _inputs[ParallaxInputFile.DustMap] = path; _paintedDustMapPath = path; _protectedInputHistory.Remember([path]); }
        else if (snapshotCreated) File.Delete(path); // Only the newly owned private snapshot, never an input file.
        Notify();
    }

    private async Task<bool> SwapDustAsync(string? path, string message, Func<CancellationToken, Task>? prepareFile = null)
    {
        // A camera edit can still be in the debounce/native refit when the dust
        // action starts. Capture that draft before Begin cancels its old refit.
        if ((_preview ?? _scene) is not { } source) return false;
        bool refitCamera = _preview is null || _refitRequested || IsReconfiguring;
        ParallaxVideoSettings? camera;
        try
        {
            if (refitCamera && Composition.ValidationMessage is string invalid) throw new InvalidDataException(invalid);
            camera = refitCamera ? Composition.VideoSettings(true) : null;
        }
        catch (Exception e) { Error = e.Message; Notify(); return false; }
        var (signal, generation) = Begin(message);
        ParallaxVideo? updated = null;
        try
        {
            if (prepareFile is not null) await prepareFile(signal.Token);
            signal.Token.ThrowIfCancellationRequested();
            updated = await ParallaxCore.WithDustMapAsync(source, path, events: Events(generation), cancellationToken: signal.Token);
            if (camera is not null)
            {
                var fitted = await ParallaxCore.ReconfigureAsync(updated, camera, events: Events(generation), cancellationToken: signal.Token);
                updated.Dispose(); updated = fitted;
            }
            signal.Token.ThrowIfCancellationRequested();
            if (_closed || generation != _revision) throw new OperationCanceledException(signal.Token);
            ReleasePreview(); _scene?.Dispose(); _scene = _preview = updated; Summary = updated.Summary;
            updated = null; // Both session references own the one immutable video.
            Status = DustDescription;
            Finish(signal, null); RequestFrame();
            return true;
        }
        catch (Exception e)
        {
            bool cancelled = signal.IsCancellationRequested || e is OperationCanceledException;
            Finish(signal, e);
            // A refused map must not silently discard a camera edit that was
            // already waiting for automatic preview when this action began.
            if (refitCamera && !cancelled) ScheduleRefit();
            return false;
        }
        finally { updated?.Dispose(); }
    }
    public async Task ExportDustMapAsync(string path, bool counted = false)
    {
        if (!CanExportDustMap || (_preview ?? _scene) is not { } video) return;
        string?[] protectedPaths = [_sourcePath, _starlessSourcePath, _starsSourcePath, SnapshotPath, _starless, _stars,
            .. _inputs.Values, .. _protectedInputHistory.Paths, .. ParallaxInputPaths.FromComposition(Composition),
            .. (_scene?.ProtectedPaths ?? []), .. video.ProtectedPaths];
        bool resumeRefit = _preview is null || _refitRequested || IsReconfiguring;
        var (signal, generation) = Begin("Exporting the dust map…");
        try
        {
            string destination = ParallaxDustMapOutput.ValidateDestination(path, protectedPaths);
            // The native writer has no cancellation parameter: keep this job
            // tracked and await its completion before releasing session files.
            // Carry draft-only inputs into the writer's final alias check too,
            // not just this preflight, before atomically publishing the file.
            bool written = await Task.Run(() => video.WriteDustMap(destination, protectedPaths, counted, signal.Token));
            signal.Token.ThrowIfCancellationRequested();
            if (_closed || generation != _revision) throw new OperationCanceledException(signal.Token);
            Status = written ? $"Dust map written to {Path.GetFileName(destination)}." : "This scene has no dust to export: dust dimming is off, or too few stars show to map it.";
            Finish(signal, null);
            if (resumeRefit) ScheduleRefit();
        }
        catch (Exception e)
        {
            bool cancelled = signal.IsCancellationRequested || e is OperationCanceledException;
            Finish(signal, e);
            if (resumeRefit && !cancelled) ScheduleRefit();
        }
    }
    // Depth planes found when the scene was last prepared with automatic planes
    // on, or null. They stay listed after a scene edit, so a distance can be
    // given for one before the scene is prepared again.
    public IReadOnlyList<ParallaxPlane>? ScenePlanes { get; private set; }
    public string PlanesDescription => !Composition.Scene.AutoPlanes && Composition.Scene.Planes is not { Count: > 0 } ? "No depth planes: the starless image moves as one, at the nebula's distance." :
        ScenePlanes switch
        {
            null => "Planes are found when the scene is prepared.",
            { Count: 0 } when _scene is not null => "No depth planes were lifted.",
            { Count: 0 } => "None found when the scene was last prepared. Prepare Preview to apply changes.",
            _ when _scene is null => "Found when the scene was last prepared. Prepare Preview to apply changes.",
            _ => string.Empty,
        };
    // A distance for a catalogued object in place of the catalogue's, or null
    // to drop it. Either prepares the scene again.
    public void SetDistanceOverride(string name, double? parsecs) => Edit(c =>
    {
        if (parsecs is double distance) c.Scene.SetDistanceOverride(name, distance);
        else c.Scene.RemoveDistanceOverride(name);
    });
    private void KeepPlanes(ParallaxRequest request, ParallaxSummary summary) =>
        ScenePlanes = request.Scene.AutoPlanes || request.Scene.Planes is { Count: > 0 } ? summary.Planes : null;

    public void PickDistanceReference() { PickingCrop = false; PickingDestination = false; PickingDistance = true; ShowsSource = true; Notify(); }
    public void Pick(double x, double y)
    {
        int[] crop = FilmedRectangle;
        if (x < crop[0] || y < crop[1] || x > crop[0] + crop[2] - 1 || y > crop[1] + crop[3] - 1)
        { Error = "Pick a point inside the filmed crop."; Notify(); return; }
        if (PickingDistance) { Edit(c => { c.Scene.DistanceFocus = [x, y]; c.DistanceName = "Custom point"; }); PickingDistance = false; }
        else if (PickingDestination)
        {
            if (Composition.Motion == ParallaxMotion.Tour) EditStop(s => { s.Focus = [x, y]; s.Name = "Custom Point"; s.DepthParsecs = null; });
            else Edit(c => { c.Focus = [x, y]; c.FocusName = "Custom Point"; });
            PickingDestination = false;
        }
        Notify();
    }
    public void AddStop(bool whole = false)
    {
        if (IsBusy || _closed) return;
        var stop = new ParallaxStop { Name = whole ? "Whole Image" : "Custom Point", Focus = whole ? null : ImageCenter,
            Dolly = 0, Zoom = 1, PullBackToWholeImage = !whole && ParallaxCore.SupportsPullBackToWholeImage };
        stop.RotateDegrees = SelectedIndex is int i ? Composition.Stops[i].RotateDegrees : 0;
        int index = SelectedIndex is int j ? j + 1 : Composition.Stops.Count;
        Edit(c => c.Stops.Insert(index, stop)); SelectStop(stop.Id);
    }
    public void DuplicateStop()
    {
        if (IsBusy || _closed || SelectedIndex is not int i) return;
        var stop = Composition.Stops[i].DeepClone() with { Id = Guid.NewGuid() };
        Edit(c => c.Stops.Insert(i + 1, stop)); SelectStop(stop.Id);
    }
    public void MoveStop(int offset)
    {
        if (IsBusy || _closed || SelectedIndex is not int i || i + offset < 0 || i + offset >= Composition.Stops.Count) return;
        Edit(c => { var stop = c.Stops[i]; c.Stops.RemoveAt(i); c.Stops.Insert(i + offset, stop); });
        SelectStop(Composition.Stops[i + offset].Id);
    }
    public void RemoveStop()
    {
        if (IsBusy || _closed || SelectedIndex is not int i || Composition.Stops.Count <= 2) return;
        Edit(c => c.Stops.RemoveAt(i)); SelectStop(Composition.Stops[Math.Min(i, Composition.Stops.Count - 1)].Id);
    }
    public void PullBackStop()
    {
        if (IsBusy || _closed || SelectedStop is not Guid id) return;
        if (!ParallaxCore.SupportsPullBackToWholeImage)
        { Error = "Whole-image pull-back requires a native core containing Seiza #283."; Notify(); return; }
        EditStop(s => s.PullBack());
        SelectStop(id);
    }
    public void SelectStop(Guid id) { SelectedStop = id; if (SelectedIndex is int i) Playhead = Composition.ArrivalAt(i); RequestFrame(); Notify(); }

    private ParallaxRequest Request(bool preview)
    {
        if (!SourceReady) throw new InvalidOperationException("The source snapshot is not ready.");
        if (ValidationMessage is string message) throw new InvalidDataException(message);
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Seiza", "ParallaxDistances");
        Directory.CreateDirectory(cache);
        return ParallaxRequestSource.Create(Composition, preview, SnapshotPath, AutomaticSeparation, _starless, _stars,
            new()
            {
                Wcs = _wcs, CatalogDirectory = CatalogSettingsStore.LoadCatalogDirectory(), GaiaCache = cache,
                Objects = _inputs.GetValueOrDefault(ParallaxInputFile.Objects), ObjectDistances = _inputs.GetValueOrDefault(ParallaxInputFile.ObjectDistances),
                StarDistances = _inputs.GetValueOrDefault(ParallaxInputFile.StarDistances),
                // The core refuses a dust map with the dust off.
                DustMap = Composition.Scene.Dust ? _inputs.GetValueOrDefault(ParallaxInputFile.DustMap) : null,
                RcAstroExecutable = AutomaticSeparation ? _inputs.GetValueOrDefault(ParallaxInputFile.Separator) : null, RcAstroHost = "seiza-win",
            });
    }
    private (CancellationTokenSource Cancellation, int Revision) Begin(string message)
    {
        PickingCrop = false;
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
        RefreshDocumentSolution();
        if (TourSolveIssue is { } issue) { Error = Status = issue; Notify(); return; }
        if (!double.IsFinite(TourOptions.Hold) || TourOptions.Hold < 0 || !double.IsFinite(TourOptions.Motion) || TourOptions.Motion < 0) { Error = "Tour hold and motion must be finite and nonnegative."; Notify(); return; }
        // A prepared scene plans each stop at the depth it shows its target at,
        // with no solve, for the full output frame. The scene keeps the
        // distance it was prepared with.
        if (_scene is { } scene)
        {
            string size = Composition.VideoSettings(false).Size;
            ParallaxTourOptions options = TourOptions with { };
            var (sceneSignal, sceneGeneration) = Begin("Planning tour on the prepared scene…");
            try
            {
                var plan = (await ParallaxCore.PlanOnSceneAsync(scene, size, options, Composition.Framing, sceneSignal.Token))
                    .InSourceCoordinates(Composition.Scene.Crop);
                if (!_closed && sceneGeneration == _revision) { Candidate = plan; Status = $"Generated {plan.Tour.Count} stops on the prepared scene. Apply the tour or keep your draft."; }
                Finish(sceneSignal, null);
            }
            catch (Exception e) { Finish(sceneSignal, e); }
            return;
        }
        ParallaxRequest request;
        try { request = Request(false); } catch (Exception e) { Error = e.Message; Notify(); return; }
        request.AutoTour = TourOptions with { }; request.Video.Tour = [];
        var (signal, generation) = Begin(HasWcs ? "Planning tour…" : "Solving image and planning tour…");
        try
        {
            await ResolveSolutionAsync(request, generation, signal.Token);
            var plan = (await ParallaxCore.PlanAsync(request, events: Events(generation), cancellationToken: signal.Token))
                .InSourceCoordinates(Composition.Scene.Crop);
            if (!_closed && generation == _revision) { _wcs = request.Inputs.Wcs; Candidate = plan; Status = $"Generated {plan.Tour.Count} stops. Apply the tour or keep your draft."; }
            Finish(signal, null);
        }
        catch (Exception e) { Finish(signal, e); }
    }
    public void ApplyCandidate()
    {
        if (Candidate is not { } plan) return;
        Edit(c => c.ApplyTour(plan));
        Candidate = null; SelectStop(plan.Tour[0].Id);
    }
    public void DiscardCandidate() { Candidate = null; Notify(); }
    public async Task LoadTourAsync(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("The tour JSON exceeds the 4 MiB limit.");
            var plan = ParallaxTourPlan.FromJson(await File.ReadAllTextAsync(path), path);
            int[]? crop = plan.Crop ?? Composition.Scene.Crop;
            if (!ParallaxGeometry.CropInside(crop, Width, Height)) throw new InvalidDataException("The tour's crop lies outside this source image.");
            plan = plan.InSourceCoordinates(crop);
            if (crop is [0, 0, var cropWidth, var cropHeight] && cropWidth == Width && cropHeight == Height)
                plan = plan with { Crop = null };
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
            var plan = new ParallaxTourPlan { Focus = Composition.Scene.DistanceFocus ?? ImageCenter, FocusName = Composition.DistanceName, Seconds = Composition.Duration, Tour = Composition.Stops, TourGlide = Composition.TourGlide, TourTitles = Composition.TourTitles, TourLoop = Composition.TourLoop }
                .InCropCoordinates(FilmedRectangle);
            string stage = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".seiza-tour-" + Guid.NewGuid().ToString("N") + ".json");
            try { await File.WriteAllTextAsync(stage, plan.ToJson(path)); File.Move(stage, path, true); }
            finally { File.Delete(stage); }
        }
        catch (Exception e) { Error = e.Message; }
        Notify();
    }
    public async Task PreparePreviewAsync()
    {
        RefreshDocumentSolution();
        if (!CanPrepare) return;
        if (_scene is not null) { ScheduleRefit(true); return; }
        ParallaxRequest request = Request(true);
        var (signal, generation) = Begin(HasWcs ? "Loading images and finding stars…" : "Loading images, solving sky coordinates and finding stars…");
        try
        {
            await ResolveSolutionAsync(request, generation, signal.Token);
            var video = await ParallaxCore.PrepareAsync(request, events: Events(generation), cancellationToken: signal.Token);
            if (_closed || generation != _revision) video.Dispose();
            else
            {
                ReleasePreview(); _scene?.Dispose(); _scene = _preview = video; Summary = video.Summary; _wcs = Summary.Wcs;
                KeepPlanes(request, Summary);
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
        RefreshDocumentSolution();
        if (!CanPrepare) return;
        ParallaxRequest request = Request(false); ParallaxVideo? existing = _scene;
        var options = new ParallaxExportOptions(ExportFormat, Codec, FfmpegPath);
        string?[] protectedPaths = [_sourcePath, _starlessSourcePath, _starsSourcePath, SnapshotPath, _starless, _stars,
            .. _inputs.Values, .. _protectedInputHistory.Paths, .. ParallaxInputPaths.FromComposition(Composition), .. (existing?.ProtectedPaths ?? [])];
        var (signal, generation) = Begin(existing is null ? "Preparing full-resolution video…" : "Fitting full-resolution video…");
        ParallaxVideo? video = null;
        bool retained = false;
        try
        {
            // Fail before expensive preparation when an optional encoder is
            // missing or an output names an input/previous sequence folder.
            string[] preflightPaths = ParallaxInputPaths.Capture(protectedPaths);
            if (options.Format is ParallaxExportFormat.Mov or ParallaxExportFormat.Matroska)
            {
                string encoder = ParallaxVideoExporter.ResolveFfmpeg(options.FfmpegPath);
                preflightPaths = ParallaxInputPaths.Capture([.. preflightPaths, encoder]);
            }
            if (options.Format == ParallaxExportFormat.PngSequence) ParallaxExportOutput.ValidateSequence(destination, preflightPaths);
            else ParallaxExportOutput.ValidateFile(destination, options.Format, preflightPaths);
            if (existing is null) await ResolveSolutionAsync(request, generation, signal.Token);
            video = existing is null ? await ParallaxCore.PrepareAsync(request, events: Events(generation), cancellationToken: signal.Token) : await ParallaxCore.ReconfigureAsync(existing, request.Video, events: Events(generation), cancellationToken: signal.Token);
            signal.Token.ThrowIfCancellationRequested();
            if (_closed || generation != _revision) throw new OperationCanceledException();
            // Export-first workflows retain the expensive scene too. Keep an older
            // preview independently owned while replacing its scene reference.
            if (!ReferenceEquals(_scene, _preview)) _scene?.Dispose();
            _scene = video; retained = true;
            Summary = video.Summary; _wcs = Summary.Wcs; Status = "Encoding video…";
            if (existing is null) KeepPlanes(request, Summary);
            Notify();
            var progress = new Progress<ParallaxExportProgress>(p => { if (!_closed && IsBusy && ReferenceEquals(_operation, signal) && generation == _revision && !signal.IsCancellationRequested) { Progress = p.Fraction; Status = p.CompletedFrames == p.TotalFrames ? "Finalizing video…" : $"Encoding frame {p.CompletedFrames} of {p.TotalFrames}…"; Notify(); } });
            await ParallaxVideoExporter.ExportAsync(video, destination, options, protectedPaths, progress, signal.Token);
            if (!_closed) { ExportedPath = destination; Status = options.Format == ParallaxExportFormat.PngSequence ? "PNG sequence exported." : "Video exported."; }
            Finish(signal, null);
        }
        catch (Exception e) { Finish(signal, e); }
        finally { if (!retained) video?.Dispose(); }
    }

    public async Task DownloadCatalogsAsync() => await _catalogs.StartParallaxSetupAsync();
    private void RefreshDocumentSolution()
    {
        if (_closed || IsBusy) return;
        if (_readDocumentSolve?.Invoke() is not { } result || ReferenceEquals(result, _lastDocumentSolve)) return;
        _lastDocumentSolve = SolveInformation = result; _wcs = result.Wcs;
        Invalidate(true);
    }
    private async Task ResolveSolutionAsync(ParallaxRequest request, int generation, CancellationToken cancellationToken)
    {
        var result = await ParallaxRequestSource.EnsureSolutionAsync(request, _sourcePath,
            path => Task.Run(() => SeizaCore.Solve(path, request.Inputs.CatalogDirectory,
                request.Scene.MinimumScaleArcsecPerPixel, request.Scene.MaximumScaleArcsecPerPixel,
                cancellationToken: cancellationToken)), cancellationToken,
            sourceIsStarless: _sourceIsStarless, layersAligned: LayersAligned, snapshotPath: SnapshotPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed || generation != _revision) throw new OperationCanceledException(cancellationToken);
        _wcs = request.Inputs.Wcs;
        if (result is not null)
        {
            SolveInformation = _lastDocumentSolve = result;
            _publishDocumentSolve?.Invoke(result);
            if (result.IsUncheckedHeader && !_warnings.Contains(result.SourceDescription!)) _warnings.Add(result.SourceDescription!);
            Notify();
        }
    }
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
        CatalogSettingsStore.CatalogDirectoryChanged -= CatalogDirectoryChanged;
        CatalogSettingsStore.RcAstroExecutableChanged -= RcAstroExecutableChanged;
        _catalogs.PropertyChanged -= CatalogChanged;
        ReleasePreview(); _scene?.Dispose(); _scene = null; SourceImage = PreviewImage = null;
        TryCleanup();
    }
    public void Dispose() { Close(); GC.SuppressFinalize(this); }
    private void JobFinished() { _activeJobs--; ApplyPendingRcAstroSettings(); TryCleanup(); }
    private void TryCleanup()
    {
        if (!_closed || _activeJobs != 0) return;
        // Only this session's explicitly created GUID directory is removed.
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private void Notify(string? property = null) { if (!_closed) PropertyChanged?.Invoke(this, new(property)); }
}
