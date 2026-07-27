using System.Runtime.InteropServices;
using System.Text.Json;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal static class ImageStackService
{
    public static Task<ImageStackBatchResult> StackBatchAsync(
        IReadOnlyList<ImageStackJob> jobs,
        IProgress<ImageStackProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => StackBatch(jobs, progress, cancellationToken), cancellationToken);
    }

    private static ImageStackBatchResult StackBatch(
        IReadOnlyList<ImageStackJob> jobs,
        IProgress<ImageStackProgress>? progress,
        CancellationToken cancellationToken)
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
                result = Stack(
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
                    cancellationToken);
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

    private static ImageStackResult Stack(
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

        nint error = 0;
        nint stacker = NativeMethods.OpenLiveStacker(
            request.Inputs[0],
            request.Calibration.BiasPath,
            request.Calibration.DarkPath,
            request.Calibration.FlatPath,
            request.Calibration.OverridesDarkExposure
                ? request.Calibration.DarkExposureSeconds
                : 0,
            request.Options.ToJson(),
            out error);
        if (stacker == 0)
        {
            throw ReadError(error, "The Seiza core could not open the reference image.");
        }

        try
        {
            var dispositions = new List<ImageStackDisposition>(request.Inputs.Count - 1);
            int unreadableFrames = 0;
            progress?.Invoke(new ImageStackProgress(
                ImageStackProgressPhase.Stacking,
                Path.GetFileName(request.Inputs[0]),
                1,
                request.Inputs.Count,
                1,
                0));

            for (int index = 1; index < request.Inputs.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = request.Inputs[index];
                error = 0;
                nint response = NativeMethods.PushLiveStackerFrameJson(stacker, path, out error);
                if (response == 0)
                {
                    unreadableFrames++;
                    dispositions.Add(new ImageStackDisposition(
                        path,
                        false,
                        TakeErrorMessage(error, "The frame could not be read.")));
                }
                else
                {
                    try
                    {
                        string json = Marshal.PtrToStringUTF8(response)
                            ?? throw new SeizaCoreException(
                                "The Seiza core returned an invalid stacking result.");
                        ImageStackDisposition disposition = JsonSerializer.Deserialize(
                            json,
                            SeizaJsonSerializerContext.Default.ImageStackDisposition)
                            ?? throw new SeizaCoreException(
                                "The Seiza core returned an invalid stacking result.");
                        dispositions.Add(disposition);
                    }
                    finally
                    {
                        NativeMethods.FreeString(response);
                    }
                }

                int accepted = checked((int)NativeMethods.GetLiveStackerAcceptedFrames(stacker));
                int rejected = checked((int)NativeMethods.GetLiveStackerRejectedFrames(stacker))
                    + unreadableFrames;
                progress?.Invoke(new ImageStackProgress(
                    ImageStackProgressPhase.Stacking,
                    Path.GetFileName(path),
                    index + 1,
                    request.Inputs.Count,
                    accepted,
                    rejected));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.GetLiveStackerAcceptedFrames(stacker) <= 1)
            {
                string reason = dispositions.FirstOrDefault(item => !item.Accepted)?.Reason
                    ?? "All additional frames were rejected.";
                throw new SeizaCoreException(
                    $"The stack needs at least two accepted frames. {reason}");
            }

            progress?.Invoke(new ImageStackProgress(
                ImageStackProgressPhase.Writing,
                $"Writing {Path.GetFileName(request.OutputPath)}…",
                request.Inputs.Count,
                request.Inputs.Count,
                checked((int)NativeMethods.GetLiveStackerAcceptedFrames(stacker)),
                checked((int)NativeMethods.GetLiveStackerRejectedFrames(stacker))
                    + unreadableFrames));

            error = 0;
            nint snapshot = NativeMethods.FinishLiveStacker(ref stacker, out error);
            if (snapshot == 0)
            {
                throw ReadError(error, "The Seiza core could not finish the image stack.");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                AtomicOutputFile.Write(
                    request.OutputPath,
                    stagingPath =>
                    {
                        error = 0;
                        if (!NativeMethods.WriteStackSnapshotFits(snapshot, stagingPath, out error))
                        {
                            throw ReadError(
                                error,
                                "The Seiza core could not write the stacked image.");
                        }
                    },
                    cancellationToken);

                return new ImageStackResult(
                    request.OutputPath,
                    checked((int)NativeMethods.GetStackSnapshotAcceptedFrames(snapshot)),
                    checked((int)NativeMethods.GetStackSnapshotRejectedFrames(snapshot))
                        + unreadableFrames,
                    dispositions);
            }
            finally
            {
                NativeMethods.FreeStackSnapshot(snapshot);
            }
        }
        finally
        {
            if (stacker != 0)
            {
                NativeMethods.FreeLiveStacker(stacker);
            }
        }
    }

    private static SeizaCoreException ReadError(nint error, string fallbackMessage) =>
        new(TakeErrorMessage(error, fallbackMessage));

    private static string TakeErrorMessage(nint error, string fallbackMessage)
    {
        if (error == 0)
        {
            return fallbackMessage;
        }
        try
        {
            return Marshal.PtrToStringUTF8(error) ?? fallbackMessage;
        }
        finally
        {
            NativeMethods.FreeString(error);
        }
    }
}
