using System;

namespace TaskbarNicifier.App.Shell;

public sealed record AppWindowItem(
    IntPtr Hwnd,
    int ProcessId,
    string? ProcessPath,
    string? ProcessName,
    string Title,
    string? AppUserModelId = null,
    int? IdentityProcessId = null,
    string? IdentityProcessPath = null,
    string? IdentityProcessName = null
);

