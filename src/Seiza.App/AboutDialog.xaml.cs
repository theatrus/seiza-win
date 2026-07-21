using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Services;
using Windows.ApplicationModel;

namespace Seiza.App;

public sealed partial class AboutDialog : ContentDialog
{
    private SeizaBuildInfo BuildInfo { get; } = SeizaBuildInfo.Current;

    public string AppVersionText { get; } = $"Version {GetAppVersion()}";

    public string SeizaVersion => BuildInfo.Version;

    public string SeizaCommit => BuildInfo.Commit;

    public Uri SeizaCommitUri => BuildInfo.CommitUri;

    public AboutDialog()
    {
        InitializeComponent();
    }

    private static string GetAppVersion()
    {
        try
        {
            PackageVersion version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch (InvalidOperationException)
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        }
        catch (COMException)
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        }
    }
}
