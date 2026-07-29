using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Seiza.App.Services;

namespace Seiza.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // AppInstance must run before XAML initialization. Initializing the
        // WinRT wrappers and dispatcher synchronization context mirrors the
        // Windows App SDK custom-entry-point contract.
        WinRT.ComWrappersSupport.InitializeComWrappers();

        string[] commandLinePaths = CommandLineActivationRelay.GetSupportedPaths();
        AppInstance? mainInstance = null;
        AppActivationArguments? initialActivation = null;
        try
        {
            initialActivation = AppInstance.GetCurrent().GetActivatedEventArgs();
            mainInstance = AppInstance.FindOrRegisterForKey("Seiza.Main");
            if (!mainInstance.IsCurrent)
            {
                try
                {
                    if (commandLinePaths.Length > 0)
                    {
                        CommandLineActivationRelay.SendAsync(
                                mainInstance.ProcessId,
                                commandLinePaths)
                            .GetAwaiter()
                            .GetResult();
                    }
                    else
                    {
                        mainInstance.RedirectActivationToAsync(initialActivation)
                            .GetAwaiter()
                            .GetResult();
                    }
                    return;
                }
                catch (Exception)
                {
                    // If the registered process is unavailable, continue this
                    // launch locally so a file-open request is never discarded.
                    mainInstance = null;
                    initialActivation = null;
                }
            }
        }
        catch (Exception)
        {
            // AppInstance can be unavailable on a damaged runtime install.
            // The application still remains useful as an independent process.
            mainInstance = null;
            initialActivation = null;
        }

        App.ConfigureInitialActivation(
            mainInstance,
            initialActivation,
            commandLinePaths);
        Application.Start(_initializationParameters =>
        {
            DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(dispatcherQueue));
            _ = new App();
        });
    }
}
