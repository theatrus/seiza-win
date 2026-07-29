using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetSparkleUpdater;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.Events;
using NetSparkleUpdater.SignatureVerifiers;

namespace Seiza.App.Services;

internal sealed class UpdateController : IDisposable
{
    internal const string AppCastUrl =
        "https://github.com/theatrus/seiza-win/releases/latest/download/appcast.xml";

    // Shared with Seiza for Mac so both native apps have the same Sparkle trust root.
    internal const string PublicEd25519Key =
        "Jk4K7QO9ohQbi455S888/lnSiqXB6a5sB4wEuEZjaQ0=";

    private readonly SparkleUpdater _updater;
    private ContentDialog? _downloadDialog;
    private ProgressBar? _downloadProgress;
    private TextBlock? _downloadStatus;
    private AppCastItem? _downloadItem;
    private string? _downloadedPath;
    private Exception? _downloadError;
    private InstallUpdateFailureReason? _installFailureReason;
    private bool _isBusy;
    private bool _disposed;

    public UpdateController()
    {
        _updater = new SparkleUpdater(
            AppCastUrl,
            new Ed25519Checker(
                SecurityMode.Strict,
                PublicEd25519Key,
                publicKeyFile: null,
                readFileBeingVerifiedInChunks: true))
        {
            UIFactory = null,
            RelaunchAfterUpdate = false,
            // GitHub release downloads redirect to an extensionless asset URL.
            // Keep the MSI filename from the signed appcast instead.
            CheckServerFileName = false,
        };

        _updater.DownloadStarted += Updater_DownloadStarted;
        _updater.DownloadMadeProgress += Updater_DownloadMadeProgress;
        _updater.DownloadFinished += Updater_DownloadFinished;
        _updater.DownloadHadError += Updater_DownloadHadError;
        _updater.DownloadCanceled += Updater_DownloadCanceled;
        _updater.DownloadedFileIsCorrupt += Updater_DownloadedFileIsCorrupt;
        _updater.DownloadedFileThrewWhileCheckingSignature +=
            Updater_DownloadedFileThrewWhileCheckingSignature;
        _updater.InstallUpdateFailed += Updater_InstallUpdateFailed;
        _updater.CloseApplication += Updater_CloseApplication;
    }

    public static string CurrentVersion
    {
        get
        {
            Version? version = Assembly.GetEntryAssembly()?.GetName().Version;
            return version is null ? "unknown" : version.ToString(3);
        }
    }

    public async Task CheckForUpdatesAsync(FrameworkElement dialogOwner, bool userInitiated)
    {
        if (_disposed || _isBusy || dialogOwner.XamlRoot is null)
        {
            return;
        }

        if (!userInitiated && !CatalogSettingsStore.LoadAutomaticallyCheckForUpdates())
        {
            return;
        }

        _isBusy = true;
        try
        {
            UpdateInfo updateInfo;
            try
            {
                // Seiza owns the prompt and skip preference. Asking NetSparkle to ignore
                // its built-in skipped-version state keeps manual checks deterministic.
                updateInfo = await _updater.CheckForUpdatesQuietly(ignoreSkippedVersions: true);
            }
            catch (Exception exception)
            {
                if (userInitiated)
                {
                    await ShowMessageAsync(
                        dialogOwner,
                        "Couldn’t check for updates",
                        exception.Message);
                }
                return;
            }

            if (updateInfo.Status != UpdateStatus.UpdateAvailable || updateInfo.Updates.Count == 0)
            {
                if (userInitiated)
                {
                    bool checkFailed = updateInfo.Status == UpdateStatus.CouldNotDetermine;
                    string title = checkFailed
                        ? "Couldn’t check for updates"
                        : "Seiza is up to date";
                    string message = checkFailed
                        ? "The update feed could not be reached or verified. Try again later."
                        : $"You’re running the latest version of Seiza for Windows ({CurrentVersion}).";
                    await ShowMessageAsync(dialogOwner, title, message);
                }
                return;
            }

            AppCastItem update = updateInfo.Updates[0];
            if (!userInitiated && string.Equals(
                    CatalogSettingsStore.LoadSkippedUpdateVersion(),
                    update.Version,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ContentDialogResult response;
            try
            {
                response = await ShowUpdateAvailableAsync(dialogOwner, update);
            }
            catch (Exception)
            {
                // Another app dialog may have opened while the background check was
                // running. Avoid interrupting the user; the next check will retry.
                return;
            }
            if (response == ContentDialogResult.Secondary)
            {
                CatalogSettingsStore.SaveSkippedUpdateVersion(update.Version);
                return;
            }

            if (response == ContentDialogResult.Primary)
            {
                CatalogSettingsStore.SaveSkippedUpdateVersion(null);
                await DownloadAndInstallAsync(dialogOwner, update);
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    private static async Task<ContentDialogResult> ShowUpdateAvailableAsync(
        FrameworkElement owner,
        AppCastItem update)
    {
        string version = update.ShortVersion ?? update.Version ?? "new version";
        var details = new StackPanel { Spacing = 12 };
        details.Children.Add(new TextBlock
        {
            Text = $"Seiza for Windows {version} is available. You’re currently running {CurrentVersion}.",
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(update.Description))
        {
            details.Children.Add(new TextBlock
            {
                Text = "What’s new",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            details.Children.Add(new ScrollViewer
            {
                MaxHeight = 240,
                Content = new TextBlock
                {
                    Text = update.Description.Trim(),
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            Title = update.IsCriticalUpdate ? "Critical update available" : "Update available",
            Content = details,
            PrimaryButtonText = "Download and install",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (!update.IsCriticalUpdate)
        {
            dialog.SecondaryButtonText = "Skip this version";
        }

        return await dialog.ShowAsync();
    }

    private async Task DownloadAndInstallAsync(FrameworkElement owner, AppCastItem update)
    {
        try
        {
            _updater.TmpDownloadFileNameWithExtension =
                UpdateInstallerNaming.FromDownloadLink(update.DownloadLink);
        }
        catch (InvalidDataException exception)
        {
            await ShowMessageAsync(owner, "Update download failed", exception.Message);
            return;
        }

        _downloadItem = update;
        _downloadedPath = null;
        _downloadError = null;
        _downloadProgress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = true,
        };
        _downloadStatus = new TextBlock
        {
            Text = "Preparing the download…",
            TextWrapping = TextWrapping.Wrap,
        };
        _downloadDialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            Title = $"Downloading Seiza {update.ShortVersion ?? update.Version}",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    _downloadStatus,
                    _downloadProgress,
                    new TextBlock
                    {
                        Text = "The installer is verified before it is opened. Windows may ask for administrator approval.",
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                            "TextFillColorSecondaryBrush"],
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            CloseButtonText = "Cancel",
        };
        _downloadDialog.Closing += DownloadDialog_Closing;

        Windows.Foundation.IAsyncOperation<ContentDialogResult> dialogOperation =
            _downloadDialog.ShowAsync();
        try
        {
            await _updater.InitAndBeginDownload(update);
        }
        catch (Exception exception)
        {
            CompleteDownload(error: exception);
        }

        await dialogOperation;
        _downloadDialog.Closing -= DownloadDialog_Closing;

        string? downloadPath = _downloadedPath;
        Exception? downloadError = _downloadError;
        bool wasCanceled = downloadPath is null && downloadError is null;
        _downloadDialog = null;
        _downloadProgress = null;
        _downloadStatus = null;
        _downloadItem = null;

        if (downloadError is not null)
        {
            await ShowMessageAsync(owner, "Update download failed", downloadError.Message);
        }
        else if (!wasCanceled && downloadPath is not null)
        {
            try
            {
                _installFailureReason = null;
                await _updater.InstallUpdate(update, downloadPath);
                if (_installFailureReason is InstallUpdateFailureReason failureReason)
                {
                    await ShowMessageAsync(
                        owner,
                        "Couldn’t open the installer",
                        InstallFailureMessage(failureReason));
                }
            }
            catch (Exception exception)
            {
                await ShowMessageAsync(owner, "Couldn’t open the installer", exception.Message);
            }
        }
    }

    private void DownloadDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_downloadedPath is null && _downloadError is null)
        {
            _updater.CancelFileDownload();
        }
    }

    private void Updater_DownloadStarted(AppCastItem item, string path) =>
        RunOnUIThread(() =>
        {
            if (ReferenceEquals(item, _downloadItem) && _downloadStatus is not null)
            {
                _downloadStatus.Text = "Downloading update…";
            }
        });

    private void Updater_DownloadMadeProgress(
        object sender,
        AppCastItem item,
        ItemDownloadProgressEventArgs args) =>
        RunOnUIThread(() =>
        {
            if (!ReferenceEquals(item, _downloadItem) || _downloadProgress is null)
            {
                return;
            }

            _downloadProgress.IsIndeterminate = false;
            _downloadProgress.Value = args.ProgressPercentage;
            if (_downloadStatus is not null)
            {
                _downloadStatus.Text = $"Downloading update… {args.ProgressPercentage}%";
            }
        });

    private void Updater_DownloadFinished(AppCastItem item, string path) =>
        RunOnUIThread(() =>
        {
            if (ReferenceEquals(item, _downloadItem))
            {
                CompleteDownload(path: path);
            }
        });

    private void Updater_DownloadHadError(AppCastItem item, string? path, Exception exception) =>
        RunOnUIThread(() =>
        {
            if (ReferenceEquals(item, _downloadItem))
            {
                CompleteDownload(error: exception);
            }
        });

    private void Updater_DownloadCanceled(AppCastItem item, string path) =>
        RunOnUIThread(() =>
        {
            if (ReferenceEquals(item, _downloadItem))
            {
                _downloadDialog?.Hide();
            }
        });

    private void Updater_DownloadedFileIsCorrupt(AppCastItem item, string path) =>
        RunOnUIThread(() =>
        {
            if (ReferenceEquals(item, _downloadItem))
            {
                CompleteDownload(error: new InvalidDataException(
                    "The downloaded installer did not pass signature verification and was not opened."));
            }
        });

    private void Updater_DownloadedFileThrewWhileCheckingSignature(
        AppCastItem item,
        string path) =>
        RunOnUIThread(() =>
        {
            if (ReferenceEquals(item, _downloadItem))
            {
                CompleteDownload(error: new InvalidDataException(
                    "The downloaded installer could not be verified and was not opened."));
            }
        });

    private bool Updater_InstallUpdateFailed(
        InstallUpdateFailureReason failureReason,
        string? installPath)
    {
        _installFailureReason = failureReason;
        return false;
    }

    private static string InstallFailureMessage(InstallUpdateFailureReason failureReason) =>
        failureReason switch
        {
            InstallUpdateFailureReason.InvalidSignature =>
                "The downloaded installer did not pass signature verification and was not opened.",
            InstallUpdateFailureReason.FileNotFound =>
                "The downloaded installer could not be found. Check for updates and try again.",
            InstallUpdateFailureReason.CouldNotBuildInstallerCommand =>
                "Windows Installer could not be started for the downloaded update.",
            InstallUpdateFailureReason.CanceledByUserViaEvent =>
                "Installation was canceled before Windows Installer started.",
            _ => "Windows Installer could not be started for the downloaded update.",
        };

    private void CompleteDownload(string? path = null, Exception? error = null)
    {
        _downloadedPath = path;
        _downloadError = error;
        _downloadDialog?.Hide();
    }

    private static async Task ShowMessageAsync(
        FrameworkElement owner,
        string title,
        string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    private static void RunOnUIThread(Action action)
    {
        if (App.DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _ = App.DispatcherQueue.TryEnqueue(() => action());
        }
    }

    private static void Updater_CloseApplication() => Application.Current.Exit();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _updater.DownloadStarted -= Updater_DownloadStarted;
        _updater.DownloadMadeProgress -= Updater_DownloadMadeProgress;
        _updater.DownloadFinished -= Updater_DownloadFinished;
        _updater.DownloadHadError -= Updater_DownloadHadError;
        _updater.DownloadCanceled -= Updater_DownloadCanceled;
        _updater.DownloadedFileIsCorrupt -= Updater_DownloadedFileIsCorrupt;
        _updater.DownloadedFileThrewWhileCheckingSignature -=
            Updater_DownloadedFileThrewWhileCheckingSignature;
        _updater.InstallUpdateFailed -= Updater_InstallUpdateFailed;
        _updater.CloseApplication -= Updater_CloseApplication;
        _updater.Dispose();
    }
}
