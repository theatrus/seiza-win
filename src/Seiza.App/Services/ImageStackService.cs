using Seiza.App.Models;

namespace Seiza.App.Services;

internal static class ImageStackService
{
    public static async Task<ImageStackBatchResult> StackBatchAsync(
        IReadOnlyList<ImageStackJob> jobs,
        IProgress<ImageStackProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ImageStackValidation.ValidateBatch(jobs);
        string[] protectedBatchSourcePaths = ImageStackValidation.GetProtectedSourcePaths(jobs);
        if (jobs.Any(job => job.Request.Options.CanvasMode == StackCanvasMode.Expanded))
            StackingCoreCapabilities.Current.RequireExpandedCanvas();
        foreach (ImageStackJob job in jobs.Where(job => job.Request.AlignmentReferencePath is not null))
        {
            // Fail before any earlier group can write an output when a
            // published core has not yet caught up with the requested APIs.
            StackingCoreCapabilities.Current.RequireReferenceImage(
                needsFallback: job.Request.ChannelReferenceCandidates.Count > 1);
        }

        int totalFrames = jobs.Sum(job => job.Request.Inputs.Count);
        int completedBeforeJob = 0;
        int acceptedBeforeJob = 0;
        int rejectedBeforeJob = 0;
        var results = new List<ImageStackResult>(jobs.Count);

        foreach (ImageStackJob job in jobs)
        {
            ImageStackResult result;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // An output that does not exist yet can still alias another
                // group's eventual output through a directory junction. Once
                // the first is published, later final-name checks must retain it.
                string[] protectedJobPaths = [.. protectedBatchSourcePaths,
                    .. jobs.Where(other => !ReferenceEquals(other, job))
                        .Select(other => other.Request.OutputPath)];
                result = await StackAsync(
                    job.Request,
                    protectedJobPaths,
                    update => progress?.Report(update with
                    {
                        Message = jobs.Count > 1
                            ? $"{job.Group.Title}: {update.Message}"
                            : update.Message,
                        CompletedFrames = completedBeforeJob + update.CompletedFrames,
                        TotalFrames = totalFrames,
                        AcceptedFrames = acceptedBeforeJob + update.AcceptedFrames,
                        RejectedFrames = rejectedBeforeJob + update.RejectedFrames,
                    }),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new ImageStackBatchCanceledException(
                    results.Select(item => item.OutputPath).ToArray(),
                    cancellationToken);
            }
            catch (Exception exception) when (results.Count > 0)
            {
                throw new ImageStackBatchFailureException(
                    exception,
                    results.Select(item => item.OutputPath).ToArray());
            }
            results.Add(result);
            completedBeforeJob += job.Request.Inputs.Count;
            acceptedBeforeJob += result.AcceptedFrames;
            rejectedBeforeJob += result.RejectedFrames;
        }

        return new ImageStackBatchResult(results);
    }

    private static async Task<ImageStackResult> StackAsync(
        ImageStackRequest request,
        IReadOnlyList<string> protectedBatchSourcePaths,
        Action<ImageStackProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke(new ImageStackProgress(
            ImageStackProgressPhase.Preparing,
            request.Options.LearnDefects ? "Learning sensor defects from dithered lights…" : "Opening reference image…",
            0,
            request.Inputs.Count,
            0,
            0));

        LibraryPreparedPlan? plan = request.LibraryPlan;
        if (plan is not null)
        {
            await Task.Run(() => CalibrationLibraryService.Verify(plan.Review.Sources), cancellationToken).ConfigureAwait(false);
            if (request.Options.LearnDefects)
                throw new InvalidOperationException("Turn off learned sensor defects when using graded per-light calibration.");
        }
        ImageStackOptions preparedOptions = await StackDefectService.PrepareAsync(request.Inputs,
            request.Options, request.Calibration, cancellationToken).ConfigureAwait(false);
        ImageStackSession opened;
        StackAlignmentReferenceSelection? referenceSelection = null;
        if (preparedOptions.CanvasMode == StackCanvasMode.Expanded)
        {
            progress?.Invoke(new ImageStackProgress(ImageStackProgressPhase.Preparing,
                "Planning expanded mosaic canvas…", 0, request.Inputs.Count, 0, 0));
            opened = await ImageStackSession.OpenExpandedAsync(request.Inputs, preparedOptions,
                request.Calibration, plan, cancellationToken).ConfigureAwait(false);
        }
        else if (plan is not null)
        {
            ImageStackSession? selected = null;
            var failures = new List<string>();
            string[] candidates = request.ChannelReferenceCandidates.Count > 0 ? request.ChannelReferenceCandidates.ToArray() : [request.Inputs[0]];
            foreach (string candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                plan.VerifyActive(candidate);
                LibraryFileSession fileSession = plan.Session(candidate);
                try
                {
                    if (request.AlignmentReferencePath is string geometry)
                    {
                        ImageStackReferenceOpenResult result = await ImageStackSession.OpenWithReferenceImageAsync(
                            geometry, [candidate], preparedOptions, fileSession.Calibration, fileSession, cancellationToken).ConfigureAwait(false);
                        selected = result.Session;
                        referenceSelection = new(1, Array.IndexOf(candidates, candidate), candidate,
                            candidates.Take(Array.IndexOf(candidates, candidate)).Select((p, i) => new StackAlignmentReferenceSkip(i, p, failures[i])).ToArray());
                    }
                    else selected = await ImageStackSession.OpenAsync(candidate, preparedOptions, fileSession.Calibration,
                        fileSession, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { failures.Add(e.Message); }
            }
            opened = selected ?? throw new SeizaCoreException("Could not open a calibrated reference. " + string.Join(" ", failures));
        }
        else if (request.AlignmentReferencePath is string alignmentPath)
        {
            ImageStackReferenceOpenResult result = await ImageStackSession.OpenWithReferenceImageAsync(
                alignmentPath,
                request.ChannelReferenceCandidates.Count > 0
                    ? request.ChannelReferenceCandidates : [request.Inputs[0]],
                preparedOptions,
                request.Calibration,
                cancellationToken).ConfigureAwait(false);
            opened = result.Session;
            referenceSelection = result.Selection;
        }
        else
        {
            opened = await ImageStackSession.OpenAsync(
                request.Inputs[0], preparedOptions, request.Calibration,
                cancellationToken).ConfigureAwait(false);
        }
        await using ImageStackSession session = opened;
        string[] orderedInputs = request.InputsStartingWith(
            referenceSelection?.ChannelReferencePath ?? opened.SelectedReferencePath ?? request.Inputs[0]);
        bool referenceIntegrated = request.AlignmentReferencePath is null && preparedOptions.CanvasMode != StackCanvasMode.Expanded;

        var dispositions = new List<ImageStackDisposition>(request.Inputs.Count - 1);
        var snrSamples = new List<ImageStackSnrSample>();
        var snrDepths = new HashSet<int>(
            ImageStackSession.GetSnrMeasurementDepths(request.Inputs.Count));
        var attemptedSnrDepths = new HashSet<int>();
        string? snrWarning = null;
        int failedFrames = 0;
        ImageStackSessionCounts counts = await session.GetCountsAsync(cancellationToken)
            .ConfigureAwait(false);
        snrWarning = await TryMeasureSnrAsync(
            session,
            counts.AcceptedFrames,
            snrDepths,
            attemptedSnrDepths,
            snrSamples,
            includeCurrentDepth: false,
            cancellationToken).ConfigureAwait(false);
        progress?.Invoke(new ImageStackProgress(
            ImageStackProgressPhase.Stacking,
            referenceSelection is null
                ? Path.GetFileName(orderedInputs[0])
                : $"Channel reference: {Path.GetFileName(orderedInputs[0])}" +
                    (referenceSelection.Skipped.Length > 0
                        ? $" ({referenceSelection.Skipped.Length} earlier alignment candidates skipped)" : string.Empty),
            referenceIntegrated ? 1 : 0,
            request.Inputs.Count,
            counts.AcceptedFrames,
            counts.RejectedFrames));

        for (int index = referenceIntegrated ? 1 : 0; index < orderedInputs.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = orderedInputs[index];
            ImageStackPushResult push = await (plan is null ? session.PushFrameAsync(path, cancellationToken)
                : session.PushCalibratedFrameAsync(path, plan, preparedOptions.CanvasMode == StackCanvasMode.Expanded, cancellationToken))
                .ConfigureAwait(false);
            dispositions.Add(push.Disposition);
            if (push.NativeFailure)
            {
                failedFrames++;
            }
            if (!referenceIntegrated && index == 0 && !push.Disposition.Accepted)
            {
                throw new SeizaCoreException(
                    $"The {(preparedOptions.CanvasMode == StackCanvasMode.Expanded ? "mosaic anchor" : "channel reference")} could not be integrated: {push.Disposition.Reason}");
            }

            counts = await session.GetCountsAsync(cancellationToken).ConfigureAwait(false);
            string? measurementWarning = await TryMeasureSnrAsync(
                session,
                counts.AcceptedFrames,
                snrDepths,
                attemptedSnrDepths,
                snrSamples,
                includeCurrentDepth: false,
                cancellationToken).ConfigureAwait(false);
            snrWarning ??= measurementWarning;
            progress?.Invoke(new ImageStackProgress(
                ImageStackProgressPhase.Stacking,
                Path.GetFileName(path),
                index + 1,
                request.Inputs.Count,
                counts.AcceptedFrames,
                counts.RejectedFrames + failedFrames));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (plan is not null) await Task.Run(() => CalibrationLibraryService.Verify(plan.Review.Sources), cancellationToken).ConfigureAwait(false);
        counts = await session.GetCountsAsync(cancellationToken).ConfigureAwait(false);
        if (counts.AcceptedFrames <= 1)
        {
            string reason = dispositions.FirstOrDefault(item => !item.Accepted)?.Reason
                ?? "All additional frames were rejected.";
            throw new SeizaCoreException(
                $"The stack needs at least two accepted frames. {reason}");
        }

        string? finalMeasurementWarning = await TryMeasureSnrAsync(
            session,
            counts.AcceptedFrames,
            snrDepths,
            attemptedSnrDepths,
            snrSamples,
            includeCurrentDepth: true,
            cancellationToken).ConfigureAwait(false);
        snrWarning ??= finalMeasurementWarning;

        int rejectedFrames = counts.RejectedFrames + failedFrames;
        ImageStackSnapshot? cleaned = null;
        try
        {
            string? transientNote = null;
            if (request.Options.RemoveTransients)
            {
                (cleaned, transientNote) = await TryRemoveTransientsAsync(
                    session,
                    request,
                    progress,
                    counts.AcceptedFrames,
                    rejectedFrames,
                    cancellationToken).ConfigureAwait(false);
            }

            progress?.Invoke(new ImageStackProgress(
                ImageStackProgressPhase.Writing,
                $"Writing {Path.GetFileName(request.OutputPath)}…",
                request.Inputs.Count,
                request.Inputs.Count,
                counts.AcceptedFrames,
                rejectedFrames));

            // With a cleaned snapshot the live stacker is no longer needed;
            // disposing the session frees it without finalizing.
            ImageStackSnapshot? finished = cleaned is null
                ? await session.FinishAsync(cancellationToken).ConfigureAwait(false)
                : null;
            await using (finished)
            {
                ImageStackSnapshot output = cleaned ?? finished!;
                await output.WriteFitsAsync(request.OutputPath, protectedBatchSourcePaths, cancellationToken)
                    .ConfigureAwait(false);

                return new ImageStackResult(
                    request.OutputPath,
                    output.AcceptedFrames,
                    output.RejectedFrames + failedFrames,
                    dispositions,
                    StackSnrAnalyzer.Analyze(snrSamples.Select(sample => new StackSnrMeasurement(
                        sample.Frames,
                        sample.Noise,
                        sample.Background,
                        sample.Signal))),
                    snrWarning,
                    transientNote,
                    referenceSelection,
                    session.CanvasNote);
            }
        }
        finally
        {
            if (cleaned is not null)
            {
                await cleaned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Integrates the accepted frames again with leave-one-out rejection.
    /// Returns the cleaned snapshot, or a note saying why the step did not
    /// run. Cancellation propagates; any other failure becomes a note so the
    /// ordinary stack is still written.
    /// </summary>
    private static async Task<(ImageStackSnapshot? Snapshot, string? Note)> TryRemoveTransientsAsync(
        ImageStackSession session,
        ImageStackRequest request,
        Action<ImageStackProgress>? progress,
        int acceptedFrames,
        int rejectedFrames,
        CancellationToken cancellationToken)
    {
        try
        {
            LiveStackNativeState state = await session.GetStateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (state.ReintegrationUnavailable is string reason)
            {
                return (null, ImageStackTransientRemoval.NotRemovedNote(reason));
            }

            void Report(ImageStackReintegrationProgress update) =>
                progress?.Invoke(new ImageStackProgress(
                    ImageStackProgressPhase.RemovingTransients,
                    ImageStackTransientRemoval.Message(update),
                    request.Inputs.Count,
                    request.Inputs.Count,
                    acceptedFrames,
                    rejectedFrames,
                    ImageStackTransientRemoval.Fraction(update)));

            Report(new ImageStackReintegrationProgress(0, 0, acceptedFrames));
            ImageStackSnapshot snapshot = await session.ReintegrateAsync(
                request.Options.TransientLowSigma,
                request.Options.TransientHighSigma,
                new InlineProgress<ImageStackReintegrationProgress>(Report),
                cancellationToken).ConfigureAwait(false);
            return (snapshot, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (null, ImageStackTransientRemoval.NotRemovedNote(exception.Message));
        }
    }

    private static async Task<string?> TryMeasureSnrAsync(
        ImageStackSession session,
        int acceptedFrames,
        IReadOnlySet<int> scheduledDepths,
        ISet<int> attemptedDepths,
        List<ImageStackSnrSample> samples,
        bool includeCurrentDepth,
        CancellationToken cancellationToken)
    {
        if (!StackSnrMeasurementPolicy.TryBegin(
                acceptedFrames,
                scheduledDepths,
                attemptedDepths,
                includeCurrentDepth) ||
            samples.Any(sample => sample.Frames == (uint)acceptedFrames))
        {
            return null;
        }
        try
        {
            ImageStackSnrSample? sample = await session.MeasureDepthAsync(cancellationToken)
                .ConfigureAwait(false);
            if (sample is not null && sample.Frames == (uint)acceptedFrames)
            {
                samples.Add(sample);
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return $"SNR analysis was unavailable: {exception.Message}";
        }
    }
}
