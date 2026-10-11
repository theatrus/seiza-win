using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Seiza.App.Controls;

/// <summary>Native tracking limits expressed as a DPI-independent client area.</summary>
internal sealed unsafe partial class ParallaxWindowSizing : IDisposable
{
    private const uint GetMinMaxInfo = 0x0024;
    private const uint NonClientDestroy = 0x0082;
    private const nuint SubclassId = 0x53455A41;
    private readonly double _minimumWidth;
    private readonly double _minimumHeight;
    private nint _window;
    private GCHandle _owner;

    public ParallaxWindowSizing(nint window, double minimumWidth, double minimumHeight)
    {
        _window = window;
        _minimumWidth = minimumWidth;
        _minimumHeight = minimumHeight;
        _owner = GCHandle.Alloc(this);
        if (!SetWindowSubclass(window, &WindowProcedure, SubclassId, (nuint)GCHandle.ToIntPtr(_owner)))
        {
            _owner.Free();
            _window = 0;
            throw new InvalidOperationException("Could not configure the Parallax Video window’s minimum size.");
        }
    }

    public void Dispose()
    {
        Detach(windowDestroyed: false);
        GC.SuppressFinalize(this);
    }

    private void Detach(bool windowDestroyed)
    {
        if (_window != 0)
        {
            bool removed = RemoveWindowSubclass(_window, &WindowProcedure, SubclassId);
            if (removed || windowDestroyed)
            {
                _window = 0;
                if (_owner.IsAllocated) _owner.Free();
            }
            // If removal failed on a still-live HWND, keep its reference data
            // valid until WM_NCDESTROY makes further callbacks impossible.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint referenceData)
    {
        nint result = 0;
        bool forwarded = false;
        try
        {
            var owner = GCHandle.FromIntPtr((nint)referenceData).Target as ParallaxWindowSizing;
            if (message == NonClientDestroy) owner?.Detach(windowDestroyed: true);
            // Let WinUI and the OS populate their normal maximization/tracking
            // values before changing only the minimum tracking extent.
            result = DefSubclassProc(window, message, wParam, lParam);
            forwarded = true;
            if (message == GetMinMaxInfo && lParam != 0 && owner is not null)
                owner.ApplyMinimum((MinMaxInfo*)lParam);
            return result;
        }
        catch
        {
            // Managed exceptions must never cross a native window callback.
            return forwarded ? result : DefSubclassProc(window, message, wParam, lParam);
        }
    }

    private void ApplyMinimum(MinMaxInfo* info)
    {
        uint dpi = GetDpiForWindow(_window);
        if (dpi == 0) dpi = 96;
        var rectangle = new NativeRect
        {
            Right = (int)Math.Ceiling(_minimumWidth * dpi / 96),
            Bottom = (int)Math.Ceiling(_minimumHeight * dpi / 96),
        };
        uint style = unchecked((uint)GetWindowLongPtr(_window, -16));
        uint extendedStyle = unchecked((uint)GetWindowLongPtr(_window, -20));
        AdjustWindowRectExForDpi(ref rectangle, style, false, extendedStyle, dpi);
        int width = rectangle.Right - rectangle.Left;
        int height = rectangle.Bottom - rectangle.Top;
        // Keep the complete window reachable on smaller/high-DPI displays.
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(MonitorFromWindow(_window, 2), ref monitor))
        {
            width = Math.Min(width, monitor.Work.Right - monitor.Work.Left);
            height = Math.Min(height, monitor.Work.Bottom - monitor.Work.Top);
        }
        info->MinimumTrackSize.X = Math.Max(info->MinimumTrackSize.X, width);
        info->MinimumTrackSize.Y = Math.Max(info->MinimumTrackSize.Y, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaximumSize;
        public NativePoint MaximumPosition;
        public NativePoint MinimumTrackSize;
        public NativePoint MaximumTrackSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [LibraryImport("comctl32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint window,
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback,
        nuint subclassId, nuint referenceData);
    [LibraryImport("comctl32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint window,
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint subclassId);
    [LibraryImport("comctl32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetDpiForWindow(nint window);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetWindowLongPtr(nint window, int index);
    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustWindowRectExForDpi(ref NativeRect rectangle, uint style,
        [MarshalAs(UnmanagedType.Bool)] bool menu, uint extendedStyle, uint dpi);
    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint MonitorFromWindow(nint window, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo information);
}
