using System;
using TaskbarNicifier.App.Interop;

namespace TaskbarNicifier.App.Shell;

internal sealed record TaskbarTarget(
    string MonitorKey,
    IntPtr MonitorHandle,
    NativeMethods.MONITORINFOEX MonitorInfo,
    bool IsPrimary,
    IntPtr TaskbarHwnd,
    NativeMethods.RECT? TaskbarRect);

