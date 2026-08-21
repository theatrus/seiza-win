using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal sealed class CalibrationPreparationService
{
    private const int MaximumAllowedProbeConcurrency = 64;
    private const string FlatPedestalWarning =
        "Matched flat frames were found, but no verified pedestal-removal path is available. " +
        "A master bias, or an uncalibrated master dark-flat/dark with known exposure matching " +
        "every flat, is required. The master flat was withheld.";
    private static readonly object CacheLocksGate = new();
    private static readonly Dictionary<string, CacheLockEntry> CacheLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, CancellationToken, Task<CalibrationFrameProbe>> _probeAsync;
    private readonly Func<CalibrationPlanRequest, CancellationToken, Task<CalibrationPlanResult>>
        _planAsync;
    private readonly Func<CalibrationMasterBuildRequest, CancellationToken,
        Task<CalibrationMasterBuildResult>> _buildAsync;
    private readonly Func<string> _coreVersion;

    public CalibrationPreparationService()
        : this(
            CalibrationService.ProbeAsync,
            CalibrationService.PlanAsync,
            CalibrationService.BuildMasterAsync,
            ReadCoreVersion)
    {
    }

    internal CalibrationPreparationService(
        Func<string, CancellationToken, Task<CalibrationFrameProbe>> probeAsync,
        Func<CalibrationPlanRequest, CancellationToken, Task<CalibrationPlanResult>> planAsync,
        Func<CalibrationMasterBuildRequest, CancellationToken,
            Task<CalibrationMasterBuildResult>> buildAsync,
        Func<string> coreVersion)
    {
        _probeAsync = probeAsync ?? throw new ArgumentNullException(nameof(probeAsync));
        _planAsync = planAsync ?? throw new ArgumentNullException(nameof(planAsync));
        _buildAsync = buildAsync ?? throw new ArgumentNullException(nameof(buildAsync));
        _coreVersion = coreVersion ?? throw new ArgumentNullException(nameof(coreVersion));
    }

    public async Task<CalibrationPreparationResult> PrepareAsync(
        CalibrationPreparationRequest request,
        IProgress<CalibrationPreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        using var retentionLeases = new CacheRetentionLeaseSet();
        CalibrationFrameProbe[] targetLights = NormalizeTargetLights(request);
        HashSet<string> protectedMasterPaths = NormalizeProtectedMasterPaths(request);
        cancellationToken.ThrowIfCancellationRequested();

        string cacheDirectory = Path.GetFullPath(request.CacheDirectory);
        Directory.CreateDirectory(cacheDirectory);
        progress?.Report(new(
            CalibrationPreparationStage.Discovering,
            "Finding calibration frames."));

        var warnings = new List<string>();
        string[] discovered = await Task.Run(
            () => DiscoverFiles(request.SourcePaths, cacheDirectory, warnings, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        CalibrationFrameProbe[] probes = await ProbeAsync(
            discovered,
            request.Options.MaximumProbeConcurrency,
            warnings,
            progress,
            cancellationToken).ConfigureAwait(false);

        CalibrationFrameProbe[] rawCandidates = probes
            .Where(IsRawCandidate)
            .ToArray();
        int ignoredMasters = probes.Count(static probe => probe.IsMaster);
        if (ignoredMasters > 0)
        {
            warnings.Add(
                $"Ignored {ignoredMasters} existing calibration master" +
                (ignoredMasters == 1 ? "." : "s; automatic preparation uses raw frames only."));
        }
        int ignoredProcessed = probes.Count(static probe =>
            !probe.IsMaster && !HasRawCalibrationState(probe));
        if (ignoredProcessed > 0)
        {
            warnings.Add(
                $"Ignored {ignoredProcessed} preprocessed calibration frame" +
                (ignoredProcessed == 1
                    ? "; automatic preparation uses raw frames only."
                    : "s; automatic preparation uses raw frames only."));
        }

        string coreVersion = _coreVersion();
        if (string.IsNullOrWhiteSpace(coreVersion))
        {
            coreVersion = "unknown";
        }

        CalibrationPlanResult biasPlan = await PlanKindAsync(
            CalibrationFrameRoles.Bias,
            request.Options.MinimumBiasFrames,
            targetLights[0],
            targetLights,
            rawCandidates,
            request.Options,
            biasAvailable: false,
            warnings,
            progress,
            cancellationToken).ConfigureAwait(false);
        BuildOutcome bias = await BuildKindAsync(
            CalibrationFrameRoles.Bias,
            biasPlan,
            rawCandidates,
            cacheDirectory,
            retentionLeases,
            coreVersion,
            request.Options,
            bias: null,
            dark: null,
            warnings,
            progress,
            cancellationToken).ConfigureAwait(false);

        CalibrationPlanResult darkPlan = await PlanKindAsync(
            CalibrationFrameRoles.Dark,
            request.Options.MinimumDarkFrames,
            targetLights[0],
            targetLights,
            rawCandidates,
            request.Options,
            bias.MasterPath is not null,
            warnings,
            progress,
            cancellationToken).ConfigureAwait(false);
        BuildOutcome dark = await BuildKindAsync(
            CalibrationFrameRoles.Dark,
            darkPlan,
            rawCandidates,
            cacheDirectory,
            retentionLeases,
            coreVersion,
            request.Options,
            bias,
            dark: null,
            warnings,
            progress,
            cancellationToken).ConfigureAwait(false);

        CalibrationPlanResult flatPlan = await PlanKindAsync(
            CalibrationFrameRoles.Flat,
            request.Options.MinimumFlatFrames,
            targetLights[0],
            targetLights,
            rawCandidates,
            request.Options,
            bias.MasterPath is not null,
            warnings,
            progress,
            cancellationToken).ConfigureAwait(false);

        const string darkFlatKind = CalibrationFrameRoles.DarkFlat;
        CalibrationPlanResult darkFlatPlan = EmptyPlan(
            darkFlatKind,
            request.Options.MinimumDarkFlatFrames);
        BuildOutcome darkFlat;
        CalibrationFrameProbe[] selectedFlatReferences = flatPlan.Ready
            ? FindSelectedFlatReferences(flatPlan, rawCandidates)
            : [];
        CalibrationFrameProbe? selectedFlatReference = selectedFlatReferences.FirstOrDefault();
        if (selectedFlatReference is null ||
            selectedFlatReferences.Length != flatPlan.SelectedPaths.Length)
        {
            string reason = flatPlan.Ready
                ? "Dark-flat preparation was skipped because the selected flat was invalid."
                : "Dark-flat preparation was skipped because no flat master can be built.";
            darkFlat = BuildOutcome.Skipped(darkFlatPlan, reason);
        }
        else
        {
            darkFlatPlan = await PlanKindAsync(
                darkFlatKind,
                request.Options.MinimumDarkFlatFrames,
                selectedFlatReference,
                selectedFlatReferences,
                rawCandidates,
                request.Options,
                bias.MasterPath is not null,
                warnings,
                progress,
                cancellationToken,
                "Matching raw dark-flat frames to the selected flat.")
                .ConfigureAwait(false);
            darkFlat = await BuildKindAsync(
                darkFlatKind,
                darkFlatPlan,
                rawCandidates,
                cacheDirectory,
                retentionLeases,
                coreVersion,
                request.Options,
                bias,
                dark: null,
                warnings,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        BuildOutcome flat;
        BuildOutcome? flatDark = darkFlat.MasterPath is not null
            ? darkFlat
            : dark.MasterPath is not null
                ? dark
                : null;
        string? flatSafetyWarning = null;
        if (flatPlan.Ready)
        {
            bool exposuresKnown = HasKnownPositiveExposures(flatPlan, rawCandidates) &&
                flatDark?.Summary.Build?.OutputExposureSeconds is double darkExposure &&
                double.IsFinite(darkExposure) &&
                darkExposure > 0;
            bool darkMatchesEveryFlatWithoutBias = bias.MasterPath is null &&
                exposuresKnown &&
                flatDark is not null &&
                DarkMatchesEverySelectedFlat(
                    flatPlan,
                    flatDark,
                    rawCandidates,
                    request.Options.Tolerances);
            if (bias.MasterPath is null &&
                (flatDark?.MasterPath is null ||
                    flatDark.Summary.Build?.BiasSubtracted != false ||
                    !darkMatchesEveryFlatWithoutBias))
            {
                flatSafetyWarning = FlatPedestalWarning;
            }
        }

        if (flatSafetyWarning is not null)
        {
            warnings.Add(flatSafetyWarning);
            flat = BuildOutcome.Withheld(flatPlan, flatSafetyWarning);
        }
        else
        {
            flat = await BuildKindAsync(
                CalibrationFrameRoles.Flat,
                flatPlan,
                rawCandidates,
                cacheDirectory,
                retentionLeases,
                coreVersion,
                request.Options,
                bias,
                flatDark,
                warnings,
                progress,
                cancellationToken).ConfigureAwait(false);
            flat = await VerifyBuiltFlatAsync(
                flat,
                targetLights,
                request.Options.Tolerances,
                warnings,
                cancellationToken).ConfigureAwait(false);
        }

        CalibrationPreparationKindSummary[] summaries =
            [bias.Summary, dark.Summary, darkFlat.Summary, flat.Summary];
        await PruneCacheAsync(
            cacheDirectory,
            request.Options,
            summaries,
            protectedMasterPaths,
            warnings,
            cancellationToken).ConfigureAwait(false);

        var calibration = new ImageStackCalibration
        {
            BiasPath = bias.MasterPath,
            DarkPath = dark.MasterPath,
            FlatPath = flat.MasterPath,
        };
        progress?.Report(new(
            CalibrationPreparationStage.Completed,
            "Calibration preparation is complete.",
            discovered.Length,
            discovered.Length));
        return new CalibrationPreparationResult
        {
            Calibration = calibration,
            Summaries = summaries,
            Warnings = warnings.ToArray(),
            DiscoveredFiles = discovered.Length,
            ProbedFiles = probes.Length,
            RetentionLease = retentionLeases.TransferOwnership(),
        };
    }

    private async Task<CalibrationPlanResult> PlanKindAsync(
        string kind,
        int minimum,
        CalibrationFrameProbe reference,
        IReadOnlyList<CalibrationFrameProbe> references,
        IReadOnlyList<CalibrationFrameProbe> candidates,
        CalibrationPreparationOptions options,
        bool biasAvailable,
        List<string> warnings,
        IProgress<CalibrationPreparationProgress>? progress,
        CancellationToken cancellationToken,
        string? message = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (kind == CalibrationFrameRoles.Flat &&
            references.Any(static target =>
                string.IsNullOrWhiteSpace(target.Signature.Filter)))
        {
            string warning =
                "The master flat was withheld because at least one target light has no " +
                "FILTER header or recognized filename filter. Seiza cannot prove which " +
                "optical response belongs to that light.";
            warnings.Add(warning);
            return EmptyPlan(kind, minimum);
        }
        progress?.Report(new(
            CalibrationPreparationStage.Planning,
            message ?? $"Matching raw {kind} frames to every target light.",
            Kind: kind));
        try
        {
            CalibrationPlanResult plan = await _planAsync(
                new CalibrationPlanRequest(
                    kind,
                    CalibrationPlanRecord.From(reference),
                    candidates.Select(CalibrationPlanRecord.From).ToArray(),
                    minimum,
                    options.Tolerances)
                {
                    References = references.Select(CalibrationPlanRecord.From).ToArray(),
                    Dependencies = new CalibrationPlanDependencies
                    {
                        BiasAvailable = biasAvailable,
                    },
                },
                cancellationToken).ConfigureAwait(false);
            ValidatePlanResult(plan, kind, minimum);
            AddCoherentSetWarning(kind, plan, warnings);
            return plan;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            warnings.Add($"The {kind} calibration plan failed: {exception.Message}");
            return EmptyPlan(kind, minimum);
        }
    }

    private async Task<BuildOutcome> BuildKindAsync(
        string kind,
        CalibrationPlanResult plan,
        IReadOnlyList<CalibrationFrameProbe> candidates,
        string cacheDirectory,
        CacheRetentionLeaseSet retentionLeases,
        string coreVersion,
        CalibrationPreparationOptions options,
        BuildOutcome? bias,
        BuildOutcome? dark,
        List<string> warnings,
        IProgress<CalibrationPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!plan.Ready)
        {
            string warning = plan.SelectedPaths.Length == 0
                ? $"No compatible raw {kind} frames were found."
                : $"Only {plan.SelectedPaths.Length} compatible raw {kind} frames were found; " +
                  $"at least {plan.Minimum} are required.";
            warnings.Add(warning);
            return BuildOutcome.Withheld(plan, warning);
        }

        string[] selectedPaths;
        try
        {
            selectedPaths = ValidateSelectedPaths(kind, plan, candidates);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            string warning = $"The {kind} calibration plan was invalid: {exception.Message}";
            warnings.Add(warning);
            return BuildOutcome.Withheld(plan, warning);
        }

        string nativeBuildKind = kind == CalibrationFrameRoles.DarkFlat
            ? CalibrationFrameRoles.Dark
            : kind;
        CalibrationMasterBuildRequest buildRequest = new()
        {
            Kind = nativeBuildKind,
            Inputs = selectedPaths,
            Output = string.Empty,
            Bias = kind is CalibrationFrameRoles.Dark or CalibrationFrameRoles.DarkFlat or
                CalibrationFrameRoles.Flat
                ? bias?.MasterPath
                : null,
            Dark = kind == CalibrationFrameRoles.Flat ? dark?.MasterPath : null,
            Rejection = options.Rejection,
            DefectSuppression = kind == CalibrationFrameRoles.Flat
                ? options.FlatDefectSuppression
                : null,
        };

        InputIdentity[] identities;
        string fingerprint;
        try
        {
            identities = CaptureInputIdentities(selectedPaths);
            fingerprint = ComputeFingerprint(
                kind,
                buildRequest,
                identities,
                coreVersion,
                bias?.Fingerprint,
                dark?.Fingerprint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            string warning = $"The {kind} master inputs could not be read: {exception.Message}";
            warnings.Add(warning);
            return BuildOutcome.Withheld(plan, warning);
        }

        string masterPath = Path.Combine(cacheDirectory, $"master-{kind}-{fingerprint}.fits");
        string reportPath = Path.ChangeExtension(masterPath, ".json");
        using CacheLockLease cacheLock = await AcquireCacheLockAsync(
            masterPath,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await using FileStream cacheLease = await AcquireCacheLeaseAsync(
                masterPath,
                cancellationToken).ConfigureAwait(false);
            CalibrationMasterCacheReport? cached = await TryReadCacheAsync(
                reportPath,
                masterPath,
                kind,
                fingerprint,
                coreVersion,
                selectedPaths.Length,
                plan.Minimum,
                buildRequest,
                cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                EnsureInputsUnchanged(identities);
                TouchCacheEntryBestEffort(reportPath);
                retentionLeases.Retain(masterPath);
                string? skippedWarning = DescribeSkippedInputs(kind, cached.Build);
                if (skippedWarning is not null)
                {
                    warnings.Add(skippedWarning);
                }
                return BuildOutcome.FromCache(plan, cached, skippedWarning);
            }

            progress?.Report(new(
                CalibrationPreparationStage.Building,
                $"Building the master {kind}.",
                Kind: kind));
            string stagingMaster = Path.Combine(
                cacheDirectory,
                $".master-{kind}-{fingerprint}-{Guid.NewGuid():N}.tmp.fits");
            string stagingReport = Path.Combine(
                cacheDirectory,
                $".master-{kind}-{fingerprint}-{Guid.NewGuid():N}.tmp.json");
            try
            {
                CalibrationMasterBuildResult built = await _buildAsync(
                    buildRequest with { Output = stagingMaster },
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ValidateBuildResult(
                    built,
                    buildRequest,
                    stagingMaster,
                    selectedPaths.Length,
                    plan.Minimum);
                EnsureInputsUnchanged(identities);
                File.Move(stagingMaster, masterPath, overwrite: true);

                CalibrationMasterBuildResult published = built with
                {
                    Kind = kind,
                    Output = masterPath,
                };
                FileInfo master = new(masterPath);
                var report = new CalibrationMasterCacheReport
                {
                    Kind = kind,
                    Fingerprint = fingerprint,
                    CoreVersion = coreVersion,
                    MasterPath = masterPath,
                    MasterLength = master.Length,
                    MasterLastWriteUtcTicks = master.LastWriteTimeUtc.Ticks,
                    Build = published,
                };
                await File.WriteAllTextAsync(
                    stagingReport,
                    JsonSerializer.Serialize(
                        report,
                        CalibrationPreparationJsonContext.Default.CalibrationMasterCacheReport),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(stagingReport, reportPath, overwrite: true);
                retentionLeases.Retain(masterPath);
                string? skippedWarning = DescribeSkippedInputs(kind, published);
                if (skippedWarning is not null)
                {
                    warnings.Add(skippedWarning);
                }
                return BuildOutcome.Built(plan, report, skippedWarning);
            }
            finally
            {
                DeleteBestEffort(stagingMaster);
                DeleteBestEffort(stagingReport);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string warning = kind == CalibrationFrameRoles.Flat && bias?.MasterPath is null
                ? $"{FlatPedestalWarning} The native check reported: {exception.Message}"
                : $"The master {kind} could not be built: {exception.Message}";
            warnings.Add(warning);
            return BuildOutcome.Withheld(plan, warning, fingerprint);
        }
    }

    private async Task<CalibrationFrameProbe[]> ProbeAsync(
        string[] paths,
        int maximumConcurrency,
        List<string> warnings,
        IProgress<CalibrationPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var probes = new ConcurrentBag<CalibrationFrameProbe>();
        var probeWarnings = new ConcurrentBag<string>();
        int completed = 0;
        await Parallel.ForEachAsync(
            paths,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maximumConcurrency,
            },
            async (path, token) =>
            {
                try
                {
                    CalibrationFrameProbe probe = await _probeAsync(path, token)
                        .ConfigureAwait(false);
                    probes.Add(probe with { Path = Path.GetFullPath(path) });
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    probeWarnings.Add($"Could not inspect {path}: {exception.Message}");
                }
                finally
                {
                    int count = Interlocked.Increment(ref completed);
                    progress?.Report(new(
                        CalibrationPreparationStage.Probing,
                        $"Inspected {count} of {paths.Length} calibration frames.",
                        count,
                        paths.Length,
                        Path: path));
                }
            }).ConfigureAwait(false);

        warnings.AddRange(probeWarnings.Order(StringComparer.OrdinalIgnoreCase));
        return probes
            .OrderBy(static probe => probe.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] DiscoverFiles(
        IReadOnlyList<string> sourcePaths,
        string cacheDirectory,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                warnings.Add("Ignored an empty calibration source path.");
                continue;
            }

            string source;
            try
            {
                source = Path.GetFullPath(sourcePath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                warnings.Add($"Ignored invalid calibration source {sourcePath}: {exception.Message}");
                continue;
            }

            if (File.Exists(source))
            {
                if (IsAstronomyImage(source) &&
                    !LiveStackPath.IsWithinDirectory(source, cacheDirectory))
                {
                    files.Add(source);
                }
                else if (!IsAstronomyImage(source))
                {
                    warnings.Add($"Ignored unsupported calibration file {source}.");
                }
                continue;
            }
            if (!Directory.Exists(source))
            {
                warnings.Add($"Calibration source does not exist: {source}");
                continue;
            }

            try
            {
                var enumeration = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (string candidate in Directory.EnumerateFiles(source, "*", enumeration))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string fullPath = Path.GetFullPath(candidate);
                    if (IsAstronomyImage(fullPath) &&
                        !LiveStackPath.IsWithinDirectory(fullPath, cacheDirectory))
                    {
                        files.Add(fullPath);
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not completely scan {source}: {exception.Message}");
            }
        }

        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] ValidateSelectedPaths(
        string kind,
        CalibrationPlanResult plan,
        IReadOnlyList<CalibrationFrameProbe> candidates)
    {
        var allowed = candidates
            .Where(candidate => IsRawCandidate(candidate) && candidate.Role == kind)
            .Select(static candidate => Path.GetFullPath(candidate.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] selected = plan.SelectedPaths.Select(Path.GetFullPath).ToArray();
        if (selected.Length < plan.Minimum)
        {
            throw new InvalidDataException(
                $"the core marked the plan ready with only {selected.Length} selected frames");
        }
        if (selected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
        {
            throw new InvalidDataException("the core selected the same frame more than once");
        }
        string? invalid = selected.FirstOrDefault(path => !allowed.Contains(path));
        if (invalid is not null)
        {
            throw new InvalidDataException(
                $"the core selected a master or non-{kind} frame: {invalid}");
        }
        return selected;
    }

    private static bool HasKnownPositiveExposures(
        CalibrationPlanResult plan,
        IReadOnlyList<CalibrationFrameProbe> candidates)
    {
        var byPath = candidates
            .Where(IsRawCandidate)
            .ToDictionary(
                static candidate => Path.GetFullPath(candidate.Path),
                StringComparer.OrdinalIgnoreCase);
        return plan.SelectedPaths.All(path =>
            byPath.TryGetValue(Path.GetFullPath(path), out CalibrationFrameProbe? probe) &&
            probe.Signature.ExposureSeconds is double exposure &&
            double.IsFinite(exposure) &&
            exposure > 0);
    }

    private static bool DarkMatchesEverySelectedFlat(
        CalibrationPlanResult flatPlan,
        BuildOutcome flatDark,
        IReadOnlyList<CalibrationFrameProbe> candidates,
        CalibrationPlanTolerances requestedTolerances)
    {
        CalibrationMasterBuildResult? build = flatDark.Summary.Build;
        if (build?.OutputExposureSeconds is not double outputExposure ||
            !double.IsFinite(outputExposure) ||
            outputExposure <= 0 ||
            build.Inputs.Length == 0)
        {
            return false;
        }

        var byPath = candidates.ToDictionary(
            static probe => Path.GetFullPath(probe.Path),
            StringComparer.OrdinalIgnoreCase);
        if (!byPath.TryGetValue(
                Path.GetFullPath(build.Inputs[0].Path),
                out CalibrationFrameProbe? darkInput))
        {
            return false;
        }

        CalibrationFrameSignature darkSignature = darkInput.Signature with
        {
            ExposureSeconds = outputExposure,
        };
        CalibrationMatchTolerances tolerances = ResolveMatchTolerances(requestedTolerances);
        foreach (string path in flatPlan.SelectedPaths)
        {
            if (!byPath.TryGetValue(
                    Path.GetFullPath(path),
                    out CalibrationFrameProbe? flat) ||
                !CalibrationMatchingService.DarkMatches(
                    flat.Signature,
                    darkSignature,
                    tolerances))
            {
                return false;
            }
        }
        return true;
    }

    private async Task<BuildOutcome> VerifyBuiltFlatAsync(
        BuildOutcome flat,
        IReadOnlyList<CalibrationFrameProbe> targetLights,
        CalibrationPlanTolerances requestedTolerances,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (flat.MasterPath is null)
        {
            return flat;
        }

        string? mismatch = null;
        try
        {
            CalibrationFrameProbe master = await _probeAsync(
                    flat.MasterPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!master.IsMaster ||
                !string.Equals(
                    master.Role,
                    CalibrationFrameRoles.Flat,
                    StringComparison.OrdinalIgnoreCase) ||
                !master.CalibrationState.FlatNormalized)
            {
                mismatch = "the written file does not identify itself as a normalized master flat";
            }
            else
            {
                CalibrationMatchTolerances tolerances =
                    ResolveMatchTolerances(requestedTolerances);
                foreach (CalibrationFrameProbe target in targetLights)
                {
                    if (!CalibrationMatchingService.SensorMatches(
                            target.Signature,
                            master.Signature))
                    {
                        mismatch = "its sensor or readout metadata no longer matches every target";
                        break;
                    }
                    if (!CalibrationMatchingService.OpticsMatch(
                            target.Signature,
                            master.Signature,
                            tolerances))
                    {
                        mismatch = "its optical metadata no longer matches every target";
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            mismatch = $"its written metadata could not be verified ({exception.Message})";
        }

        if (mismatch is null)
        {
            return flat;
        }

        string warning =
            $"The built master flat was withheld because {mismatch}. " +
            "The actual master file must pass the same native compatibility rules as its " +
            "source frames.";
        warnings.Add(warning);
        return BuildOutcome.WithheldBuilt(flat, warning);
    }

    private static CalibrationMatchTolerances ResolveMatchTolerances(
        CalibrationPlanTolerances requested)
    {
        CalibrationMatchTolerances defaults =
            CalibrationMatchingService.GetDefaultTolerances();
        return defaults with
        {
            ExposureSeconds = requested.ExposureSeconds ?? defaults.ExposureSeconds,
            ExposureFraction = requested.ExposureFraction ?? defaults.ExposureFraction,
            DarkTemperatureC = requested.DarkTemperatureC ?? defaults.DarkTemperatureC,
            MasterTemperatureC = requested.MasterTemperatureC ?? defaults.MasterTemperatureC,
            RotationDeg = requested.RotationDeg ?? defaults.RotationDeg,
            FocalLengthMm = requested.FocalLengthMm ?? defaults.FocalLengthMm,
            FlatSessionSeconds = requested.FlatSessionSeconds ?? defaults.FlatSessionSeconds,
        };
    }

    private static InputIdentity[] CaptureInputIdentities(IEnumerable<string> paths) =>
        paths.Select(static path =>
        {
            string fullPath = Path.GetFullPath(path);
            FileInfo file = new(fullPath);
            if (!file.Exists)
            {
                throw new FileNotFoundException("A selected calibration frame disappeared.", fullPath);
            }
            return new InputIdentity(fullPath, file.Length, file.LastWriteTimeUtc.Ticks);
        }).ToArray();

    private static void EnsureInputsUnchanged(IEnumerable<InputIdentity> expected)
    {
        foreach (InputIdentity identity in expected)
        {
            FileInfo file = new(identity.Path);
            if (!file.Exists || file.Length != identity.Length ||
                file.LastWriteTimeUtc.Ticks != identity.LastWriteUtcTicks)
            {
                throw new IOException(
                    $"Calibration input changed while its master was being built: {identity.Path}");
            }
        }
    }

    private static string ComputeFingerprint(
        string logicalKind,
        CalibrationMasterBuildRequest request,
        IReadOnlyList<InputIdentity> identities,
        string coreVersion,
        string? biasFingerprint,
        string? darkFingerprint)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHash(hash, "seiza-calibration-cache-v1");
        AppendHash(hash, request.Kind);
        if (!string.Equals(logicalKind, request.Kind, StringComparison.Ordinal))
        {
            AppendHash(hash, $"logical:{logicalKind}");
        }
        AppendHash(hash, coreVersion);
        AppendHash(hash, request.Rejection.LowSigma.ToString("R", CultureInfo.InvariantCulture));
        AppendHash(hash, request.Rejection.HighSigma.ToString("R", CultureInfo.InvariantCulture));
        AppendHash(hash, request.DefectSuppression?.LowSigma.ToString(
            "R", CultureInfo.InvariantCulture) ?? "none");
        AppendHash(hash, request.DefectSuppression?.HighSigma.ToString(
            "R", CultureInfo.InvariantCulture) ?? "none");
        AppendHash(hash, request.DarkExposureSeconds?.ToString(
            "R", CultureInfo.InvariantCulture) ?? "none");
        AppendHash(hash, request.ExposureSeconds?.ToString(
            "R", CultureInfo.InvariantCulture) ?? "none");
        AppendHash(hash, biasFingerprint ?? "none");
        AppendHash(hash, darkFingerprint ?? "none");
        foreach (InputIdentity identity in identities)
        {
            AppendHash(hash, identity.Path);
            AppendHash(hash, identity.Length.ToString(CultureInfo.InvariantCulture));
            AppendHash(hash, identity.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendHash(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static async Task<CalibrationMasterCacheReport?> TryReadCacheAsync(
        string reportPath,
        string masterPath,
        string kind,
        string fingerprint,
        string coreVersion,
        int requestedFrames,
        int minimumFrames,
        CalibrationMasterBuildRequest request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(reportPath) || !File.Exists(masterPath))
        {
            return null;
        }
        try
        {
            string json = await File.ReadAllTextAsync(reportPath, cancellationToken)
                .ConfigureAwait(false);
            CalibrationMasterCacheReport? report = JsonSerializer.Deserialize(
                json,
                CalibrationPreparationJsonContext.Default.CalibrationMasterCacheReport);
            FileInfo master = new(masterPath);
            return report is not null &&
                report.SchemaVersion == CalibrationMasterCacheReport.CurrentSchemaVersion &&
                string.Equals(report.Kind, kind, StringComparison.Ordinal) &&
                string.Equals(report.Fingerprint, fingerprint, StringComparison.Ordinal) &&
                string.Equals(report.CoreVersion, coreVersion, StringComparison.Ordinal) &&
                string.Equals(
                    Path.GetFullPath(report.MasterPath),
                    Path.GetFullPath(masterPath),
                    StringComparison.OrdinalIgnoreCase) &&
                report.MasterLength > 0 &&
                master.Length == report.MasterLength &&
                master.LastWriteTimeUtc.Ticks == report.MasterLastWriteUtcTicks &&
                report.Build is not null &&
                report.Build.SchemaVersion > 0 &&
                string.Equals(report.Build.Kind, kind, StringComparison.Ordinal) &&
                string.Equals(
                    Path.GetFullPath(report.Build.Output),
                    Path.GetFullPath(masterPath),
                    StringComparison.OrdinalIgnoreCase) &&
                report.Build.Width > 0 &&
                report.Build.Height > 0 &&
                report.Build.Channels > 0 &&
                HasExpectedCalibrationState(report.Build, request) &&
                requestedFrames == request.Inputs.Count &&
                HasValidInputPartition(report.Build, request.Inputs, minimumFrames)
                    ? report
                    : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<CacheLockLease> AcquireCacheLockAsync(
        string key,
        CancellationToken cancellationToken)
    {
        CacheLockEntry entry = RentCacheLock(key);
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new CacheLockLease(key, entry);
        }
        catch
        {
            ReturnCacheLock(key, entry, releaseSemaphore: false);
            throw;
        }
    }

    private static CacheLockLease? TryAcquireCacheLock(string key)
    {
        CacheLockEntry entry = RentCacheLock(key);
        if (entry.Semaphore.Wait(0))
        {
            return new CacheLockLease(key, entry);
        }
        ReturnCacheLock(key, entry, releaseSemaphore: false);
        return null;
    }

    private static CacheLockEntry RentCacheLock(string key)
    {
        lock (CacheLocksGate)
        {
            if (!CacheLocks.TryGetValue(key, out CacheLockEntry? entry))
            {
                entry = new CacheLockEntry();
                CacheLocks.Add(key, entry);
            }
            entry.References++;
            return entry;
        }
    }

    private static void ReturnCacheLock(
        string key,
        CacheLockEntry entry,
        bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            entry.Semaphore.Release();
        }
        lock (CacheLocksGate)
        {
            entry.References--;
            if (entry.References == 0)
            {
                CacheLocks.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private static async Task<FileStream> AcquireCacheLeaseAsync(
        string masterPath,
        CancellationToken cancellationToken)
    {
        string leasePath = CacheLeasePath(masterPath);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    leasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static FileStream? TryAcquireCacheLease(string masterPath)
    {
        try
        {
            return new FileStream(
                CacheLeasePath(masterPath),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileStream AcquireCacheRetentionLease(string masterPath) => new(
        CacheRetentionLeasePath(masterPath),
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.ReadWrite,
        bufferSize: 1,
        FileOptions.Asynchronous);

    private static FileStream? TryAcquireExclusiveCacheRetentionLease(string masterPath)
    {
        try
        {
            return new FileStream(
                CacheRetentionLeasePath(masterPath),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string CacheLeasePath(string masterPath) => masterPath + ".lock";

    private static string CacheRetentionLeasePath(string masterPath) => masterPath + ".retain";

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private static void TouchCacheEntryBestEffort(string reportPath)
    {
        try
        {
            File.SetLastWriteTimeUtc(reportPath, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task PruneCacheAsync(
        string cacheDirectory,
        CalibrationPreparationOptions options,
        IReadOnlyList<CalibrationPreparationKindSummary> summaries,
        HashSet<string> callerProtectedMasters,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (options.MaximumCacheBytes is null && options.MaximumCacheAge is null)
        {
            return;
        }

        var protectedMasters = new HashSet<string>(
            callerProtectedMasters,
            StringComparer.OrdinalIgnoreCase);
        protectedMasters.UnionWith(summaries
            .Where(static summary => summary.MasterPath is not null)
            .Select(static summary => Path.GetFullPath(summary.MasterPath!)));
        try
        {
            await Task.Run(
                () => PruneCache(
                    cacheDirectory,
                    options.MaximumCacheBytes,
                    options.MaximumCacheAge,
                    protectedMasters,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException)
        {
            warnings.Add($"Calibration cache cleanup could not complete: {exception.Message}");
        }
    }

    private static void PruneCache(
        string cacheDirectory,
        long? maximumBytes,
        TimeSpan? maximumAge,
        HashSet<string> protectedMasters,
        CancellationToken cancellationToken)
    {
        DateTime now = DateTime.UtcNow;
        var entries = new List<CacheEntry>();
        var pairedReports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(
            cacheDirectory,
            "master-*.fits",
            SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string masterPath = Path.GetFullPath(path);
            string reportPath = Path.ChangeExtension(masterPath, ".json");
            pairedReports.Add(reportPath);
            AddEntry(masterPath, masterPath, reportPath);
        }
        foreach (string path in Directory.EnumerateFiles(
            cacheDirectory,
            "master-*.json",
            SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string reportPath = Path.GetFullPath(path);
            if (!pairedReports.Contains(reportPath))
            {
                AddEntry(Path.ChangeExtension(reportPath, ".fits"), reportPath);
            }
        }
        foreach (string path in Directory.EnumerateFiles(
            cacheDirectory,
            ".master-*.tmp.*",
            SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string stagingPath = Path.GetFullPath(path);
            if (TryGetStagingMasterPath(stagingPath, out string? masterPath) &&
                masterPath is not null)
            {
                AddEntry(masterPath, stagingPath);
            }
        }

        void AddEntry(string masterPath, params string[] paths)
        {
            try
            {
                string[] existing = paths.Where(File.Exists).ToArray();
                if (existing.Length == 0)
                {
                    return;
                }
                long bytes = 0;
                DateTime lastUsedUtc = DateTime.MinValue;
                foreach (string entryPath in existing)
                {
                    FileInfo file = new(entryPath);
                    bytes = SaturatingAdd(bytes, file.Length);
                    if (file.LastWriteTimeUtc > lastUsedUtc)
                    {
                        lastUsedUtc = file.LastWriteTimeUtc;
                    }
                }
                entries.Add(new CacheEntry(
                    Path.GetFullPath(masterPath),
                    existing,
                    bytes,
                    lastUsedUtc));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or
                    NotSupportedException)
            {
                // Another process may be publishing or removing this entry.
            }
        }

        long totalBytes = 0;
        foreach (CacheEntry entry in entries)
        {
            totalBytes = SaturatingAdd(totalBytes, entry.Bytes);
        }

        foreach (CacheEntry entry in entries.OrderBy(static entry => entry.LastUsedUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool expired = maximumAge is TimeSpan age && now - entry.LastUsedUtc > age;
            bool overLimit = maximumBytes is long limit && totalBytes > limit;
            if (!expired && !overLimit)
            {
                break;
            }
            if (protectedMasters.Contains(entry.MasterPath))
            {
                continue;
            }
            totalBytes = Math.Max(
                0,
                totalBytes - TryDeleteCacheEntry(entry, protectedMasters));
        }
    }

    private static long TryDeleteCacheEntry(
        CacheEntry entry,
        HashSet<string> protectedMasters)
    {
        using CacheLockLease? cacheLock = TryAcquireCacheLock(entry.MasterPath);
        if (cacheLock is null)
        {
            return 0;
        }
        using FileStream? lease = TryAcquireCacheLease(entry.MasterPath);
        if (lease is null || protectedMasters.Contains(entry.MasterPath))
        {
            return 0;
        }
        using FileStream? retentionLease =
            TryAcquireExclusiveCacheRetentionLease(entry.MasterPath);
        if (retentionLease is null || protectedMasters.Contains(entry.MasterPath))
        {
            return 0;
        }

        long before = ExistingLengths(entry.Paths);
        foreach (string path in entry.Paths)
        {
            DeleteBestEffort(path);
        }
        long after = ExistingLengths(entry.Paths);
        return Math.Max(0, before - after);
    }

    private static long ExistingLengths(IEnumerable<string> paths)
    {
        long length = 0;
        foreach (string path in paths)
        {
            length = SaturatingAdd(length, ExistingLength(path));
        }
        return length;
    }

    private static bool TryGetStagingMasterPath(
        string stagingPath,
        out string? masterPath)
    {
        string name = Path.GetFileName(stagingPath);
        foreach (string kind in new[]
        {
            CalibrationFrameRoles.DarkFlat,
            CalibrationFrameRoles.Bias,
            CalibrationFrameRoles.Dark,
            CalibrationFrameRoles.Flat,
        })
        {
            string prefix = $".master-{kind}-";
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            ReadOnlySpan<char> remainder = name.AsSpan(prefix.Length);
            if (remainder.Length > 64 && remainder[64] == '-' &&
                IsHexFingerprint(remainder[..64]))
            {
                string fingerprint = remainder[..64].ToString().ToLowerInvariant();
                masterPath = Path.Combine(
                    Path.GetDirectoryName(stagingPath)!,
                    $"master-{kind}-{fingerprint}.fits");
                return true;
            }
            break;
        }
        masterPath = null;
        return false;
    }

    private static bool IsHexFingerprint(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }
        return true;
    }

    private static long ExistingLength(string path)
    {
        try
        {
            FileInfo file = new(path);
            return file.Exists ? file.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static IEnumerable<(string Kind, int Minimum)> PlanKinds(
        CalibrationPreparationOptions options)
    {
        yield return (CalibrationFrameRoles.Bias, options.MinimumBiasFrames);
        yield return (CalibrationFrameRoles.Dark, options.MinimumDarkFrames);
        yield return (CalibrationFrameRoles.Flat, options.MinimumFlatFrames);
    }

    private static CalibrationFrameProbe[] NormalizeTargetLights(
        CalibrationPreparationRequest request)
    {
        var byPath = new Dictionary<string, CalibrationFrameProbe>(
            StringComparer.OrdinalIgnoreCase);
        var targets = new List<CalibrationFrameProbe>(request.TargetLights.Count + 1);
        Add(request.Reference);
        foreach (CalibrationFrameProbe target in request.TargetLights)
        {
            Add(target);
        }
        return targets.ToArray();

        void Add(CalibrationFrameProbe probe)
        {
            string path = Path.GetFullPath(probe.Path);
            CalibrationFrameProbe normalized = CalibrationTargetMetadata.Enrich(
                probe with { Path = path });
            if (byPath.TryGetValue(path, out CalibrationFrameProbe? existing))
            {
                if (!EquivalentTargetProbe(existing, normalized))
                {
                    throw new ArgumentException(
                        $"Target light {path} was supplied with conflicting metadata.",
                        nameof(request));
                }
                return;
            }
            byPath.Add(path, normalized);
            targets.Add(normalized);
        }
    }

    private static bool EquivalentTargetProbe(
        CalibrationFrameProbe left,
        CalibrationFrameProbe right) =>
        string.Equals(left.Role, right.Role, StringComparison.Ordinal) &&
        left.IsMaster == right.IsMaster &&
        left.Signature == right.Signature &&
        left.CalibrationState == right.CalibrationState;

    private static HashSet<string> NormalizeProtectedMasterPaths(
        CalibrationPreparationRequest request)
    {
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in request.ProtectedMasterPaths)
        {
            protectedPaths.Add(Path.GetFullPath(path));
        }
        return protectedPaths;
    }

    private static CalibrationFrameProbe[] FindSelectedFlatReferences(
        CalibrationPlanResult flatPlan,
        IReadOnlyList<CalibrationFrameProbe> candidates)
    {
        var flatsByPath = candidates
            .Where(static candidate =>
                IsRawCandidate(candidate) && candidate.Role == CalibrationFrameRoles.Flat)
            .ToDictionary(
                static candidate => Path.GetFullPath(candidate.Path),
                StringComparer.OrdinalIgnoreCase);
        var selected = new List<CalibrationFrameProbe>(flatPlan.SelectedPaths.Length);
        foreach (string selectedPath in flatPlan.SelectedPaths)
        {
            try
            {
                if (flatsByPath.TryGetValue(
                    Path.GetFullPath(selectedPath),
                    out CalibrationFrameProbe? selectedFlat))
                {
                    selected.Add(selectedFlat);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException)
            {
                // A malformed native selection is rejected by the flat build as well.
            }
        }
        return selected.ToArray();
    }

    private static CalibrationPlanResult EmptyPlan(string kind, int minimum) => new()
    {
        SchemaVersion = 1,
        Kind = kind,
        Minimum = minimum,
        Ready = false,
    };

    private static void ValidatePlanResult(
        CalibrationPlanResult plan,
        string kind,
        int minimum)
    {
        if (plan.SchemaVersion < 1 ||
            !string.Equals(plan.Kind, kind, StringComparison.Ordinal) ||
            plan.Minimum != minimum)
        {
            throw new InvalidDataException(
                $"The Seiza core returned an invalid {kind} calibration plan.");
        }
    }

    private static void AddCoherentSetWarning(
        string kind,
        CalibrationPlanResult plan,
        List<string> warnings)
    {
        string[] excluded = plan.Excluded
            .Where(static entry => string.Equals(
                entry.Reason,
                "outside-coherent-set",
                StringComparison.Ordinal))
            .Select(static entry => Path.GetFileName(entry.Path))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        if (excluded.Length == 0)
        {
            return;
        }

        const int maximumNames = 3;
        string names = string.Join(", ", excluded.Take(maximumNames));
        string remainder = excluded.Length > maximumNames
            ? $", and {excluded.Length - maximumNames} more"
            : string.Empty;
        warnings.Add(
            $"Set aside {excluded.Length} raw {kind} frame" +
            (excluded.Length == 1 ? string.Empty : "s") +
            $" outside the selected temperature/session/rotation cohort: {names}{remainder}.");
    }

    private static bool IsAstronomyImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".fits" or ".fit" or ".fts" or ".xisf";

    private static bool IsRawCandidate(CalibrationFrameProbe probe) =>
        !probe.IsMaster && HasRawCalibrationState(probe);

    private static bool HasRawCalibrationState(CalibrationFrameProbe probe) =>
        !probe.CalibrationState.BiasSubtracted &&
        !probe.CalibrationState.DarkSubtracted &&
        !probe.CalibrationState.FlatNormalized;

    private static void DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateBuildResult(
        CalibrationMasterBuildResult result,
        CalibrationMasterBuildRequest request,
        string output,
        int requestedFrames,
        int minimumFrames)
    {
        if (result.SchemaVersion < 1 ||
            !string.Equals(result.Kind, request.Kind, StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetFullPath(result.Output),
                Path.GetFullPath(output),
                StringComparison.OrdinalIgnoreCase) ||
            result.Width <= 0 ||
            result.Height <= 0 ||
            result.Channels <= 0 ||
            requestedFrames != request.Inputs.Count ||
            !HasValidInputPartition(result, request.Inputs, minimumFrames) ||
            !HasExpectedCalibrationState(result, request) ||
            !File.Exists(output) ||
            new FileInfo(output).Length <= 0)
        {
            throw new InvalidDataException(
                $"The Seiza core returned an invalid master-{request.Kind} build result.");
        }
    }

    private static bool HasValidInputPartition(
        CalibrationMasterBuildResult result,
        IReadOnlyList<string> requestedInputs,
        int minimumFrames)
    {
        if (result.Inputs is null ||
            result.SkippedInputs is null ||
            result.InputFrames < minimumFrames ||
            result.InputFrames > requestedInputs.Count ||
            result.Inputs.Length != result.InputFrames ||
            result.RequestedFrames < 0)
        {
            return false;
        }

        // Schema 1 did not report RequestedFrames/SkippedInputs. A complete legacy
        // result is still unambiguous, but a partial legacy result is not: its
        // compacted per-input statistics cannot identify which paths survived.
        bool legacyResponse = result.SchemaVersion < 2;
        if (legacyResponse)
        {
            if (result.RequestedFrames != 0 ||
                result.InputFrames != requestedInputs.Count ||
                result.SkippedInputs.Length != 0)
            {
                return false;
            }
        }
        else if (result.RequestedFrames != requestedInputs.Count)
        {
            return false;
        }

        try
        {
            var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in requestedInputs)
            {
                if (string.IsNullOrWhiteSpace(path) ||
                    !requested.Add(Path.GetFullPath(path)))
                {
                    return false;
                }
            }

            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CalibrationMasterInputResult input in result.Inputs)
            {
                if (input is null ||
                    string.IsNullOrWhiteSpace(input.Path) ||
                    !reported.Add(Path.GetFullPath(input.Path)))
                {
                    return false;
                }
            }
            foreach (CalibrationMasterSkippedInputResult skipped in result.SkippedInputs)
            {
                if (skipped is null ||
                    string.IsNullOrWhiteSpace(skipped.Path) ||
                    string.IsNullOrWhiteSpace(skipped.Reason) ||
                    !reported.Add(Path.GetFullPath(skipped.Path)))
                {
                    return false;
                }
            }
            return reported.SetEquals(requested);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? DescribeSkippedInputs(
        string kind,
        CalibrationMasterBuildResult result)
    {
        if (result.SkippedInputs is not { Length: > 0 } skippedInputs)
        {
            return null;
        }

        const int maximumDetails = 3;
        string[] details = skippedInputs
            .Take(maximumDetails)
            .Select(static skipped =>
            {
                string name = Path.GetFileName(skipped.Path);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = skipped.Path;
                }
                string reason = skipped.Reason?.Trim().ReplaceLineEndings(" ") ?? string.Empty;
                return string.IsNullOrWhiteSpace(reason) ? name : $"{name} ({reason})";
            })
            .ToArray();
        string more = skippedInputs.Length > maximumDetails
            ? $"; and {skippedInputs.Length - maximumDetails} more"
            : string.Empty;
        return $"The master {kind} used {result.InputFrames} of {result.RequestedFrames} " +
            $"selected frames. Seiza skipped {skippedInputs.Length} after its final " +
            $"compatibility check: {string.Join("; ", details)}{more}.";
    }

    private static bool HasExpectedCalibrationState(
        CalibrationMasterBuildResult result,
        CalibrationMasterBuildRequest request)
    {
        bool expectedBiasSubtracted = request.Kind switch
        {
            CalibrationFrameRoles.Bias => false,
            CalibrationFrameRoles.Dark => request.Bias is not null,
            CalibrationFrameRoles.Flat => true,
            _ => false,
        };
        bool expectedDarkSubtracted = request.Kind == CalibrationFrameRoles.Flat &&
            request.Dark is not null;
        bool expectedNormalized = request.Kind == CalibrationFrameRoles.Flat;
        return result.BiasSubtracted == expectedBiasSubtracted &&
            result.DarkSubtracted == expectedDarkSubtracted &&
            result.Normalized == expectedNormalized;
    }

    private static string ReadCoreVersion()
    {
        nint value = NativeMethods.GetCoreVersion();
        return Marshal.PtrToStringUTF8(value) ?? "unknown";
    }

    private static void ValidateRequest(CalibrationPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Reference);
        ArgumentNullException.ThrowIfNull(request.TargetLights);
        ArgumentNullException.ThrowIfNull(request.ProtectedMasterPaths);
        ArgumentNullException.ThrowIfNull(request.SourcePaths);
        ArgumentNullException.ThrowIfNull(request.Options);
        CalibrationLightEligibility.Validate(request.Reference, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reference.Path);
        foreach (CalibrationFrameProbe? target in request.TargetLights)
        {
            if (target is null)
            {
                throw new ArgumentException(
                    "Target light probes cannot contain null entries.",
                    nameof(request));
            }
            CalibrationLightEligibility.Validate(target, nameof(request));
            ArgumentException.ThrowIfNullOrWhiteSpace(target.Path);
        }
        foreach (string? protectedPath in request.ProtectedMasterPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(protectedPath);
        }
        if (request.SourcePaths.Count == 0)
        {
            throw new ArgumentException(
                "Choose at least one calibration folder or file.",
                nameof(request));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CacheDirectory);
        foreach ((string kind, int minimum) in PlanKinds(request.Options))
        {
            if (minimum < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    $"The minimum {kind} frame count must be at least two.");
            }
        }
        if (request.Options.MinimumDarkFlatFrames < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The minimum dark-flat frame count must be at least two.");
        }
        if (request.Options.MaximumProbeConcurrency is < 1 or > MaximumAllowedProbeConcurrency)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Probe concurrency must be between 1 and {MaximumAllowedProbeConcurrency}.");
        }
        if (request.Options.MaximumCacheBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The cache byte limit must be positive, or null to disable it.");
        }
        if (request.Options.MaximumCacheAge is TimeSpan maximumAge && maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The cache age limit must be positive, or null to disable it.");
        }
    }

    private sealed record InputIdentity(string Path, long Length, long LastWriteUtcTicks);

    private sealed record CacheEntry(
        string MasterPath,
        string[] Paths,
        long Bytes,
        DateTime LastUsedUtc);

    private sealed class CacheLockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class CacheRetentionLeaseSet : IDisposable
    {
        private List<FileStream>? _leases;

        public CacheRetentionLeaseSet()
            : this([])
        {
        }

        private CacheRetentionLeaseSet(List<FileStream> leases)
        {
            _leases = leases;
        }

        public void Retain(string masterPath)
        {
            List<FileStream> leases = _leases ??
                throw new ObjectDisposedException(nameof(CacheRetentionLeaseSet));
            FileStream? lease = null;
            try
            {
                lease = AcquireCacheRetentionLease(masterPath);
                leases.Add(lease);
                lease = null;
            }
            finally
            {
                lease?.Dispose();
            }
        }

        public CacheRetentionLeaseSet? TransferOwnership()
        {
            List<FileStream> leases = Interlocked.Exchange(ref _leases, null) ??
                throw new ObjectDisposedException(nameof(CacheRetentionLeaseSet));
            return leases.Count == 0 ? null : new CacheRetentionLeaseSet(leases);
        }

        public void Dispose()
        {
            List<FileStream>? leases = Interlocked.Exchange(ref _leases, null);
            if (leases is null)
            {
                return;
            }
            foreach (FileStream lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    private sealed class CacheLockLease : IDisposable
    {
        private readonly string _key;
        private CacheLockEntry? _entry;

        public CacheLockLease(string key, CacheLockEntry entry)
        {
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            CacheLockEntry? entry = Interlocked.Exchange(ref _entry, null);
            if (entry is not null)
            {
                ReturnCacheLock(_key, entry, releaseSemaphore: true);
            }
        }
    }

    private sealed record BuildOutcome(
        CalibrationPreparationKindSummary Summary,
        string? MasterPath,
        string? Fingerprint)
    {
        public static BuildOutcome Withheld(
            CalibrationPlanResult plan,
            string warning,
            string? fingerprint = null) => new(
                new CalibrationPreparationKindSummary
                {
                    Kind = plan.Kind,
                    Plan = plan,
                    Fingerprint = fingerprint,
                    Warning = warning,
                },
                null,
                fingerprint);

        public static BuildOutcome Skipped(
            CalibrationPlanResult plan,
            string reason) => new(
                new CalibrationPreparationKindSummary
                {
                    Kind = plan.Kind,
                    Plan = plan,
                    Warning = reason,
                },
                null,
                null);

        public static BuildOutcome WithheldBuilt(
            BuildOutcome outcome,
            string warning) => new(
                outcome.Summary with
                {
                    MasterPath = null,
                    Warning = string.IsNullOrWhiteSpace(outcome.Summary.Warning)
                        ? warning
                        : $"{outcome.Summary.Warning} {warning}",
                },
                null,
                outcome.Fingerprint);

        public static BuildOutcome FromCache(
            CalibrationPlanResult plan,
            CalibrationMasterCacheReport report,
            string? warning = null) => FromReport(plan, report, cacheReused: true, warning);

        public static BuildOutcome Built(
            CalibrationPlanResult plan,
            CalibrationMasterCacheReport report,
            string? warning = null) => FromReport(plan, report, cacheReused: false, warning);

        private static BuildOutcome FromReport(
            CalibrationPlanResult plan,
            CalibrationMasterCacheReport report,
            bool cacheReused,
            string? warning) => new(
                new CalibrationPreparationKindSummary
                {
                    Kind = plan.Kind,
                    Plan = plan,
                    Build = report.Build,
                    MasterPath = report.MasterPath,
                    Fingerprint = report.Fingerprint,
                    CacheReused = cacheReused,
                    Warning = warning,
                },
                report.MasterPath,
                report.Fingerprint);
    }
}
