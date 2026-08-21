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
                result = await StackAsync(
                    job.Request,
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
        Action<ImageStackProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke(new ImageStackProgress(
            ImageStackProgressPhase.Preparing,
            "Opening reference image…",
            0,
            request.Inputs.Count,
            0,
            0));

        await using ImageStackSession session = await ImageStackSession.OpenAsync(
            request.Inputs[0],
            request.Options,
            request.Calibration,
            cancellationToken).ConfigureAwait(false);

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
            Path.GetFileName(request.Inputs[0]),
            1,
            request.Inputs.Count,
            counts.AcceptedFrames,
            counts.RejectedFrames));

        for (int index = 1; index < request.Inputs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = request.Inputs[index];
            ImageStackPushResult push = await session.PushFrameAsync(path, cancellationToken)
                .ConfigureAwait(false);
            dispositions.Add(push.Disposition);
            if (push.NativeFailure)
            {
                failedFrames++;
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

        progress?.Invoke(new ImageStackProgress(
            ImageStackProgressPhase.Writing,
            $"Writing {Path.GetFileName(request.OutputPath)}…",
            request.Inputs.Count,
            request.Inputs.Count,
            counts.AcceptedFrames,
            counts.RejectedFrames + failedFrames));

        await using ImageStackSnapshot snapshot = await session.FinishAsync(cancellationToken)
            .ConfigureAwait(false);
        await snapshot.WriteFitsAsync(request.OutputPath, cancellationToken).ConfigureAwait(false);

        return new ImageStackResult(
            request.OutputPath,
            snapshot.AcceptedFrames,
            snapshot.RejectedFrames + failedFrames,
            dispositions,
            StackSnrAnalyzer.Analyze(snrSamples.Select(sample => new StackSnrMeasurement(
                sample.Frames,
                sample.Noise,
                sample.Background,
                sample.Signal))),
            snrWarning);
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
