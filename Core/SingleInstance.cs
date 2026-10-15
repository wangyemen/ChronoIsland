using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ChronoIsland.Core;

/// <summary>
/// 单实例守卫（命名 Mutex + 跨进程消息）。
/// 第二个实例会把已运行实例的设置窗口唤到前台，然后自己退出。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Global\ChronoIsland.SingleInstance.v1";
    private const string ShowWindowMessage = "ChronoIsland.ShowSettings";

    private Mutex? _mutex;
    private bool _owned;

    /// <summary>是否已存在另一个实例</summary>
    public bool IsAnotherRunning { get; private set; }

    /// <summary>另一个实例请求显示设置窗口时触发（在 UI 线程上）</summary>
    public event Action? ShowSettingsRequested;

    public SingleInstance()
    {
        // 用 Global\ 前缀确保跨会话（含不同用户登录会话）唯一；
        // 若权限不足则回退到 Local\
        try
        {
            // 注意：第三个参数是 createdNew（是否新建），不是"是否拥有"。
            // 首次创建即拥有；已存在时构造不抛异常，需用 WaitOne(0) 判断是否被别人占着。
            _mutex = new Mutex(initiallyOwned: false, MutexName, out _);
            _owned = _mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (UnauthorizedAccessException)
        {
            try
            {
                _mutex = new Mutex(initiallyOwned: false, "Local\\ChronoIsland.SingleInstance.v1", out _);
                _owned = _mutex.WaitOne(TimeSpan.Zero, exitContext: false);
            }
            catch (Exception ex) { App.Log("SingleInstance", ex); return; }
        }
        catch (Exception ex)
        {
            App.Log("SingleInstance", ex);
            return;
        }

        IsAnotherRunning = !_owned;
        App.LogInfo("SingleInstance",
            IsAnotherRunning ? "检测到已有实例在运行，本实例将退出" : "本实例成为唯一实例");
        if (!IsAnotherRunning) StartListener();
    }

    /// <summary>监听来自其他实例的"唤出设置窗口"消息</summary>
    private void StartListener()
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var handle = NativeMethods.CreateWindowEx(0, "STATIC", ShowWindowMessage, 0, 0, 0, 0, 0,
                    NativeMethods.HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (handle == IntPtr.Zero) return;

                while (NativeMethods.GetMessage(out var msg, handle, 0, 0) > 0)
                {
                    if (msg.message == NativeMethods.WM_COPYDATA || msg.message == 0x0400)
                        ShowSettingsRequested?.Invoke();
                }
            }
            catch (Exception ex) { App.Log("SingleInstance", ex); }
        });
    }

    /// <summary>通知已运行的实例打开设置窗口</summary>
    public static void SignalExistingInstance()
    {
        try
        {
            var hwnd = NativeMethods.FindWindow("STATIC", ShowWindowMessage);
            if (hwnd == IntPtr.Zero) return;
            NativeMethods.PostMessage(hwnd, 0x0400, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { App.Log("SingleInstance", ex); }
    }

    public void Dispose()
    {
        try
        {
            if (_owned) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { }
        _mutex = null;
        GC.SuppressFinalize(this);
    }

    private static class NativeMethods
    {
        public static readonly IntPtr HWND_MESSAGE = new(-3);
        public const uint WM_COPYDATA = 0x004A;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
            int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string? className, string windowName);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd; public uint message;
            public IntPtr wParam; public IntPtr lParam;
            public uint time; public int ptX; public int ptY;
        }
    }
}
