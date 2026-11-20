using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Squish.App;

/// <summary>
/// Taskbar button progress via ITaskbarList3, using source-generated COM so it survives trimming
/// (built-in COM interop is disabled in trimmed builds).
/// </summary>
internal sealed partial class TaskbarProgress
{
    [GeneratedComInterface]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    internal partial interface ITaskbarList3
    {
        // ITaskbarList
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(nint hwnd);
        [PreserveSig] int DeleteTab(nint hwnd);
        [PreserveSig] int ActivateTab(nint hwnd);
        [PreserveSig] int SetActiveAlt(nint hwnd);
        // ITaskbarList2
        [PreserveSig] int MarkFullscreenWindow(nint hwnd, int fullscreen);
        // ITaskbarList3 (first two members)
        [PreserveSig] int SetProgressValue(nint hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(nint hwnd, int flags);
    }

    private static readonly Guid ClsidTaskbarList = new("56fdf344-fd6d-11d0-958a-006097c9a090");
    private static readonly Guid IidTaskbarList3 = new("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");
    private const uint CLSCTX_INPROC_SERVER = 1;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint ppv);

    private const int TBPF_NOPROGRESS = 0, TBPF_NORMAL = 2, TBPF_ERROR = 4;
    private readonly nint _hwnd;
    private readonly ITaskbarList3? _list;

    public TaskbarProgress(nint hwnd)
    {
        _hwnd = hwnd;
        try
        {
            if (CoCreateInstance(ClsidTaskbarList, 0, CLSCTX_INPROC_SERVER, IidTaskbarList3, out var ptr) == 0 && ptr != 0)
            {
                var wrappers = new StrategyBasedComWrappers();
                _list = (ITaskbarList3)wrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);
                Marshal.Release(ptr);
                _list.HrInit();
            }
        }
        catch (Exception) { _list = null; } // no taskbar progress is not worth failing over
    }

    public void Set(double? fraction, bool error = false)
    {
        if (_list is null) return;
        try
        {
            if (fraction is null) { _list.SetProgressState(_hwnd, TBPF_NOPROGRESS); return; }
            _list.SetProgressState(_hwnd, error ? TBPF_ERROR : TBPF_NORMAL);
            _list.SetProgressValue(_hwnd, (ulong)(Math.Clamp(fraction.Value, 0, 1) * 1000), 1000);
        }
        catch (Exception) { }
    }
}
