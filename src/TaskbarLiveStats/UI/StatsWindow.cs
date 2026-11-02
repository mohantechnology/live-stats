using System.Diagnostics;
using TaskbarLiveStats.Metrics;
using TaskbarLiveStats.Native;
using TaskbarLiveStats.Taskbar;

namespace TaskbarLiveStats.UI;

/// <summary>
/// Stats bar overlaid on the taskbar slot (after Widgets by default).
/// </summary>
internal sealed class StatsWindow : Form
{
    private readonly StatsRenderer _renderer = new();
    private readonly TaskbarEmbedder _embedder;
    private MetricGroup[] _groups = GroupLayout.FromSnapshot(MetricsSnapshot.Empty);
    private bool _barVisible = true;
    private TaskbarInfo _lastInfo;
    private Size _size = new(420, 36);
    private NativeMethods.RECT _lastTrayRect;
    private NativeMethods.RECT _lastNotifyRect;
    private bool _layoutDirty = true;

    public event Action? ContextMenuRequested;

    public IntPtr HandlePublic => IsHandleCreated && NativeMethods.IsWindow(Handle) ? Handle : IntPtr.Zero;

    public StatsWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = _size;
        Location = new Point(-32000, -32000);
        BackColor = ThemeHelper.TaskbarFillColor;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _embedder = new TaskbarEmbedder(IntPtr.Zero)
        {
            DesiredWidth = _size.Width,
            DesiredHeight = _size.Height
        };

        Load += (_, _) => _embedder.Bind(Handle);
        HandleCreated += (_, _) => _embedder.Bind(Handle);

        MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenTaskManager();
            else if (e.Button == MouseButtons.Right)
                ContextMenuRequested?.Invoke();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE
                          | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOPMOST;
            return cp;
        }
    }

    public void EnsureHostAlive()
    {
        if (IsHandleCreated && NativeMethods.IsWindow(Handle))
            return;

        try
        {
            if (IsHandleCreated)
                DestroyHandle();
        }
        catch { /* ignore */ }

        CreateHandle();
        _embedder.Bind(Handle);
        _layoutDirty = true;
        InvalidateLayoutCaches();
    }

    public void UpdateMetrics(MetricsSnapshot snapshot)
    {
        _groups = GroupLayout.FromSnapshot(snapshot);

        int barHeight = _lastInfo.TrayHwnd != IntPtr.Zero
            ? Math.Max(30, _lastInfo.TrayRect.Height)
            : 36;
        var measured = _renderer.Measure(_groups, barHeight);
        if (measured.Width != _size.Width || measured.Height != _size.Height)
        {
            _size = measured;
            _embedder.DesiredWidth = measured.Width;
            _embedder.DesiredHeight = measured.Height;
            _embedder.InvalidateLayoutCache();
            _layoutDirty = true;
        }

        if (_layoutDirty && _lastInfo.TrayHwnd != IntPtr.Zero && _barVisible)
            ApplyTaskbar(_lastInfo, force: true);
        else
            PaintFrame();
    }

    public void ApplyTaskbar(TaskbarInfo info) => ApplyTaskbar(info, force: false);

    private void ApplyTaskbar(TaskbarInfo info, bool force)
    {
        _lastInfo = info;
        EnsureHostAlive();
        if (info.TrayHwnd == IntPtr.Zero || HandlePublic == IntPtr.Zero)
            return;

        bool geometryChanged =
            info.TrayRect.Left != _lastTrayRect.Left ||
            info.TrayRect.Top != _lastTrayRect.Top ||
            info.TrayRect.Right != _lastTrayRect.Right ||
            info.TrayRect.Bottom != _lastTrayRect.Bottom ||
            info.NotifyRect.Left != _lastNotifyRect.Left ||
            info.NotifyRect.Top != _lastNotifyRect.Top ||
            info.NotifyRect.Right != _lastNotifyRect.Right ||
            info.NotifyRect.Bottom != _lastNotifyRect.Bottom;

        if (!force && !geometryChanged && !_layoutDirty)
        {
            _embedder.RaiseZOrder();
            return;
        }

        _lastTrayRect = info.TrayRect;
        _lastNotifyRect = info.NotifyRect;

        int barHeight = Math.Max(30, info.TrayRect.Height);
        var measured = _renderer.Measure(_groups, barHeight);
        _size = measured;
        _embedder.DesiredWidth = measured.Width;
        _embedder.DesiredHeight = measured.Height;
        _embedder.AttachOrReposition(info);

        // Keep Form bounds in sync with the layered destination (screen coords).
        if (_embedder.HasValidLayout)
        {
            var (x, y, w, h) = _embedder.GetClientBounds();
            if (Left != x || Top != y || Width != w || Height != h)
                SetBounds(x, y, w, h);
        }

        _layoutDirty = false;
        PaintFrame();
    }

    public void SetVisible(bool visible)
    {
        bool wasVisible = _barVisible;
        _barVisible = visible;
        EnsureHostAlive();
        if (HandlePublic == IntPtr.Zero) return;

        _embedder.SetVisible(visible);

        if (visible)
        {
            _layoutDirty = true;
            if (_lastInfo.TrayHwnd != IntPtr.Zero)
                ApplyTaskbar(_lastInfo, force: true);
            else
                PaintFrame();
        }
        else if (wasVisible)
        {
            // Ensure hide sticks even if a stale layout pass had SWP_SHOWWINDOW.
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
        }
    }

    public void Reembed(TaskbarInfo info)
    {
        _lastInfo = info;
        EnsureHostAlive();
        if (HandlePublic == IntPtr.Zero) return;
        _layoutDirty = true;
        InvalidateLayoutCaches();
        _embedder.Reembed(info);
        PaintFrame();
    }

    private void InvalidateLayoutCaches()
    {
        _lastTrayRect = default;
        _lastNotifyRect = default;
        _embedder.InvalidateLayoutCache();
    }

    private void PaintFrame()
    {
        if (!_barVisible || HandlePublic == IntPtr.Zero) return;
        if (!_embedder.HasValidLayout) return;

        var (x, y) = _embedder.GetLayeredDestPos();
        _renderer.PaintLayered(Handle, _groups, _size.Width, _size.Height, x, y, asChild: false);
    }

    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    private static void OpenTaskManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "taskmgr.exe",
                UseShellExecute = true
            });
        }
        catch { /* ignore */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _embedder.Dispose();
            _renderer.Dispose();
        }

        base.Dispose(disposing);
    }
}
