using System;
using System.Runtime.InteropServices;

namespace ChronoIsland.Native;

/// <summary>
/// 应用激活器 —— 用 Windows 自己的方式打开通知来源应用。
///
/// 关键洞察：Windows 原生通知能打开应用，靠的是 AUMID +
/// IApplicationActivationManager::ActivateApplication（Shell 内部用的就是这个 COM 接口）。
/// 所以拿到 AUMID 就能用同样的方式激活，不需要去猜 exe 路径。
///
/// 三级降级：
///   ① IApplicationActivationManager.ActivateApplication(aumid)
///   ② explorer.exe shell:AppsFolder\{aumid}   （走 Shell 的 AUMID 解析）
///   ③ 手动映射的 exe 路径 / 进程匹配（兜底）
/// </summary>
public static class AppLauncher
{
    // ---------- COM: IApplicationActivationManager ----------
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            uint options,
            out uint processId);

        int ActivateForFile(IntPtr appUserModelId, IntPtr itemArray, IntPtr verb, out uint processId);
        int ActivateForProtocol(IntPtr appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    /// <summary>用 AUMID 激活应用（Windows 打开通知来源用的就是这个）</summary>
    public static bool ActivateByAumid(string aumid, out uint pid)
    {
        pid = 0;
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        try
        {
            var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
            // AO_NONE = 0
            int hr = mgr.ActivateApplication(aumid, null, 0, out pid);
            if (hr >= 0)
            {
                App.LogInfo("AppLauncher", $"ActivateApplication 成功：{aumid} (PID {pid})");
                return true;
            }
            App.LogInfo("AppLauncher", $"ActivateApplication 失败：{aumid} HRESULT=0x{hr:X8}");
            return false;
        }
        catch (Exception ex)
        {
            App.LogInfo("AppLauncher", $"ActivateApplication 异常：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 【Windows 的真实做法】用通知注册表里的 CustomActivator 打开应用。
    /// HKCU\Software\Classes\AppUserModelId\{aumid} 下的 CustomActivator 
    /// 是一个 COM CLSID，Windows 点击通知时就是激活它。
    /// </summary>
    public static bool ActivateViaCustomActivator(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\AppUserModelId\{aumid}");
            if (key == null)
            {
                App.LogInfo("AppLauncher", $"AUMID 未注册：{aumid}");
                return false;
            }

            var activator = key.GetValue("CustomActivator")?.ToString();
            if (string.IsNullOrWhiteSpace(activator))
            {
                App.LogInfo("AppLauncher", $"AUMID 无 CustomActivator：{aumid}");
                return false;
            }

            if (!Guid.TryParse(activator.Trim('{', '}'), out var clsid))
            {
                App.LogInfo("AppLauncher", $"CustomActivator 格式错误：{activator}");
                return false;
            }

            App.LogInfo("AppLauncher", $"使用 CustomActivator {clsid} 激活 {aumid}");
            // 激活 COM 对象即触发应用打开
            var t = Type.GetTypeFromCLSID(clsid, throwOnError: false);
            if (t == null)
            {
                App.LogInfo("AppLauncher", $"CLSID {clsid} 未注册");
                return false;
            }
            var obj = Activator.CreateInstance(t);
            if (obj != null)
            {
                App.LogInfo("AppLauncher", "CustomActivator 已激活 ✓");
                // 部分实现是 INotificationActivationCallback，需要调用 Activate
                TryInvokeActivate(obj, aumid, "");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            App.LogInfo("AppLauncher", $"CustomActivator 激活失败：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>尝试调用 INotificationActivationCallback.Activate（若实现了该接口）</summary>
    private static void TryInvokeActivate(object comObj, string aumid, string args)
    {
        try
        {
            var t = comObj.GetType();
            var m = t.GetMethod("Activate");
            if (m == null) return;
            var ps = m.GetParameters();
            // 签名: Activate(string appUserModelId, string invokedArgs, NOTIFICATION_USER_INPUT_DATA[] data, uint count)
            if (ps.Length == 4)
            {
                var dataArray = Array.CreateInstance(ps[2].ParameterType.GetElementType()!, 0);
                m.Invoke(comObj, new object[] { aumid, args, dataArray, 0u });
                App.LogInfo("AppLauncher", "INotificationActivationCallback.Activate 已调用");
            }
        }
        catch (Exception ex)
        {
            App.LogInfo("AppLauncher", "Activate 调用跳过：" + ex.Message);
        }
    }

    /// <summary>用 shell:AppsFolder 打开（Shell 会自己解析 AUMID）</summary>
    public static bool ActivateViaShell(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{aumid}",
                UseShellExecute = true
            });
            App.LogInfo("AppLauncher", $"已通过 shell:AppsFolder 打开：{aumid}");
            return true;
        }
        catch (Exception ex)
        {
            App.LogInfo("AppLauncher", "shell 打开失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 用 Shell 的 AppsFolder 枚举找到该 AUMID 对应的应用并启动。
    /// Chrome 这类应用未注册 HKCU\AppUserModelId，但会出现在
    /// shell:AppsFolder 虚拟文件夹里（Windows 自己就从这里解析）。
    /// </summary>
    public static bool ActivateViaShellEnumeration(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;

            // 遍历 shell:AppsFolder
            dynamic appsFolder = shell.NameSpace("shell:AppsFolder");
            if (appsFolder == null) return false;

            dynamic items = appsFolder.Items();
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                string id = "";
                try { id = item.Path ?? ""; } catch { }
                if (string.IsNullOrEmpty(id)) continue;

                // AUMID 匹配（Chrome 的 id 形如 "Chrome"）
                bool match = id.Equals(aumid, StringComparison.OrdinalIgnoreCase);
                if (!match)
                {
                    // 也允许部分匹配（去掉 .exe 后缀比较）
                    var a = System.IO.Path.GetFileNameWithoutExtension(aumid);
                    var b = System.IO.Path.GetFileNameWithoutExtension(id);
                    match = !string.IsNullOrEmpty(a) && a.Equals(b, StringComparison.OrdinalIgnoreCase);
                }
                if (!match) continue;

                // 找到 → 用 Shell 的动词启动（等同双击）
                foreach (dynamic verb in item.Verbs())
                {
                    string verbName = "";
                    try { verbName = verb.Name ?? ""; } catch { }
                    // 中文系统是"打开(&O)"，英文是 "&Open"
                    if (verbName.Contains("打开") || verbName.Contains("Open", StringComparison.OrdinalIgnoreCase))
                    {
                        verb.DoIt();
                        App.LogInfo("AppLauncher", $"已通过 AppsFolder 启动：{item.Name} (id={id})");
                        return true;
                    }
                }
                // 没找到动词 → 直接调用 InvokeVerb
                try
                {
                    item.InvokeVerb("open");
                    App.LogInfo("AppLauncher", $"已通过 InvokeVerb 启动：{id}");
                    return true;
                }
                catch { }
            }
            App.LogInfo("AppLauncher", $"AppsFolder 中未找到 AUMID：{aumid}");
            return false;
        }
        catch (Exception ex)
        {
            App.LogInfo("AppLauncher", "AppsFolder 枚举失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 预检：该 AUMID 是否支持 COM 激活（即 Windows 原生通知能否打开它）。
    /// 依据是 HKCU\Software\Classes\AppUserModelId\{aumid} 下是否存在 CustomActivator。
    /// 有 → 通知可打开；无 → Windows 自己也打不开，岛上就不显示"打开"按钮。
    /// </summary>
    public static bool CanActivateByCom(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\AppUserModelId\{aumid}");
            if (key == null) return false;
            var activator = key.GetValue("CustomActivator")?.ToString();
            return !string.IsNullOrWhiteSpace(activator);
        }
        catch { return false; }
    }

    /// <summary>
    /// 仅用 COM 激活（不做任何 exe 猜测）。
    /// 失败时返回 false，调用方据此提示"该应用不支持 COM 激活"。
    /// </summary>
    public static bool ActivateComOnly(string aumid, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(aumid)) { reason = "无 AUMID"; return false; }

        if (!CanActivateByCom(aumid))
        {
            reason = "该应用不支持 COM 激活";
            App.LogInfo("AppLauncher", $"{reason}：{aumid}");
            return false;
        }

        if (ActivateViaCustomActivator(aumid)) return true;
        reason = "COM 激活调用失败";
        App.LogInfo("AppLauncher", $"{reason}：{aumid}");
        return false;
    }

    /// <summary>启动一个可执行文件</summary>
    public static bool StartExe(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return false;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path, UseShellExecute = true
            });
            App.LogInfo("AppLauncher", $"已启动：{path}");
            return true;
        }
        catch (Exception ex)
        {
            App.LogInfo("AppLauncher", $"启动失败 {path}：{ex.Message}");
            return false;
        }
    }

    /// <summary>把已运行进程的窗口带到前台</summary>
    public static bool BringRunningToFront(string processName)
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName))
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    UI.WindowChromeHelper.BringToForeground(p.MainWindowHandle);
                    App.LogInfo("AppLauncher", $"已激活运行中的 {processName}");
                    return true;
                }
            }
        }
        catch { }
        return false;
    }
}
