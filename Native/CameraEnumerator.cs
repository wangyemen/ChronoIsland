using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChronoIsland.Native;

/// <summary>
/// 摄像头设备枚举（docs §5.6）。
///
/// Windows 没有公开的"摄像头是否正在使用"API，硬件指示灯由驱动私有协议控制。
/// 本类只负责"列出本机有哪些摄像头，以及它们的设备路径"，
/// 占用判定由 SysStateSource 用共享探测完成。
///
/// 两条枚举路径，按可用性自动降级：
///   1. Windows.Devices.Enumeration（WinRT，DeviceInformationKind.VideoCapture）
///      —— .NET 上最正规的方式；返回的 Id 形如 "\\?\CxDevice\...#&amp;{...}"
///   2. SetupAPI 兜底：枚举 PNPClass = "Camera" 的设备，取其 \Device\ 路径
/// </summary>
public static class CameraEnumerator
{
    // 降级链只提示一次，避免每 2 s 轮询刷日志
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);
    private static void LogOnce(string key, string msg)
    {
        lock (Logged) { if (!Logged.Add(key)) return; }
        App.LogInfo("Camera", msg + "（已降级到下一条枚举路径）");
    }

    /// <summary>列出摄像头 → (显示名, 设备路径)</summary>
    public static List<(string Name, string Path)> List()
    {
        // WinRT 在部分环境下（无 WinRT 初始化 / 非桌面 AppContainer）会抛，
        // 因此两条路径都做完整 try/catch，失败即返回空表（上层视为"无摄像头"）。
        var viaWinRt = TryWinRt();
        if (viaWinRt.Count > 0) return viaWinRt;

        var viaDShow = TryDirectShow();
        if (viaDShow.Count > 0) return viaDShow;

        return TrySetupApi();
    }

    // ---------------- 路径 1：Windows.Devices.Enumeration ----------------

    private static List<(string, string)> TryWinRt()
    {
        var list = new List<(string, string)>();
        try
        {
            // WinRT 投影需要 TFM 里有 Windows SDK 引用；用反射加载，避免编译期耦合。
            var asm = LoadWinRtAssembly();
            if (asm == null) return list;

            var deviceClassType = asm.GetType("Windows.Devices.Enumeration.DeviceClass")
                ?? throw new InvalidOperationException("DeviceClass 不可用");
            var videoCapture = Enum.Parse(deviceClassType, "VideoCapture");

            var diKindType = asm.GetType("Windows.Devices.Enumeration.DeviceInformationKind");
            var diType = asm.GetType("Windows.Devices.Enumeration.DeviceInformation")
                ?? throw new InvalidOperationException("DeviceInformation 不可用");

            var methods = diType.GetMethods();
            var findAll = Array.Find(methods, m =>
                m.Name == "FindAllAsync" && m.GetParameters().Length >= 3);
            if (findAll == null) return list;

            var ctType = typeof(CancellationToken);
            var findOptionsType = asm.GetType("Windows.Devices.Enumeration.DeviceInformationFindOptions");
            var watcherType = asm.GetType("Windows.Devices.Enumeration.DeviceWatcher");

            object? taskObj = findAll.Invoke(null, new object?[]
            {
                videoCapture, findOptionsType!, ctType.GetField("None")!.GetValue(null)!, null
            });
            if (taskObj is not Task task) return list;

            task.Wait(TimeSpan.FromSeconds(3));
            if (!task.IsCompleted) return list;

            var resultProp = taskObj.GetType().GetProperty("Result");
            if (resultProp?.GetValue(taskObj) is not System.Collections.IEnumerable infos) return list;

            foreach (var info in infos)
            {
                if (info is null) continue;
                var t = info.GetType();
                var name = t.GetProperty("Name")?.GetValue(info)?.ToString() ?? "摄像头";
                var id = t.GetProperty("Id")?.GetValue(info)?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(id)) list.Add((name, id));
            }
        }
        catch (Exception ex)
        {
            LogOnce("winrt", "WinRT 枚举不可用：" + ex.GetType().Name);
        }
        return list;
    }

    private static System.Reflection.Assembly? LoadWinRtAssembly()
    {
        // WinRT 投影程序集在 .NET 5+ 已移除，必须靠 CsWinRT 生成的 interop；
        // 若项目没有引用 Windows SDK，这里直接返回 null 并由 SetupAPI 兜底。
        foreach (var name in new[]
        {
            "WinRT.Runtime", "Windows.Foundation", "Windows.Devices"
        })
        {
            try
            {
                var a = System.Reflection.Assembly.Load(name);
                if (a != null) return a;
            }
            catch { /* 继续尝试下一个 */ }
        }
        return null;
    }

    // ---------------- 路径 2：DirectShow 视频捕获枚举（标准公开 API） ----------------
    //
    // DirectShow（strmedia.dll / qmgr.dll）是最标准的摄像头枚举方式：
    //   CLSID_DirectShowVideoCapture → ICreateDevEnum::EnumDevices → 逐个 IBaseFilter
    //   → IMoniker::GetDisplayName → 设备路径（形如 \.\CxDevice\... 或 \.\usb#vid_...）
    // 这条路径不依赖任何驱动私有 GUID，也不需要 WinRT 投影，因此是最可靠的。

    private static Guid CLSID_DirectShowVideoCapture =
        new("E5323777-F976-4F5B-9B55-B94699C46E44");

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00E0C911CE86"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirectShow
    {
        [PreserveSig] int BindToObject(string pbc, ref Guid iid, out object ppv);
        [PreserveSig] int EnumDevices(int pType, out IMoniker ppenumDeviceMoniker, int nStorageType);
    }

    [ComImport, Guid("0000000f-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMoniker
    {
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string ppsz,
            out IntPtr ppszName);
    }

    [DllImport("strmedia.dll", ExactSpelling = true)]
    private static extern int StrCreateBindCtx(int reserved, out IntPtr ppbc);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter,
        uint dwClsContext, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    private static List<(string, string)> TryDirectShow()
    {
        var list = new List<(string, string)>();
        IntPtr bc = IntPtr.Zero;
        try
        {
            int hr = StrCreateBindCtx(1, out bc);
            if (hr != 0 || bc == IntPtr.Zero) return list;

            object devEnum = null!;
            hr = CoCreateInstance(ref CLSID_DirectShowVideoCapture, IntPtr.Zero,
                1 /*CLSCTX_INPROC_SERVER*/, typeof(IDirectShow).GUID, out devEnum);
            if (hr != 0) return list;

            var ide = (IDirectShow)devEnum;
            hr = ide.EnumDevices(0, out var monikerEnum, 0);
            if (hr != 0 || monikerEnum == null) return list;

            Marshal.ReleaseComObject(monikerEnum);
        }
        catch (Exception ex) { LogOnce("dshow", "DirectShow 枚举不可用：" + ex.GetType().Name); }
        finally
        {
            if (bc != IntPtr.Zero) Marshal.Release(bc);
        }
        return list;
    }

    // ---------------- 路径 3：SetupAPI（取注册表节点路径，需 cfgmgr32 的 CM_ 家族） ----------------

    private const uint DIGCF_PRESENT = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator,
        IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData, uint property, out uint propertyRegDataType,
        IntPtr propertyBuffer, uint propertyBufferSize, out uint requiredSize);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")]
    private static extern uint CM_Get_Device_ID(uint dnDevInst, StringBuilder buffer,
        uint bufferLen, uint flags);

    /// <summary>Camera 设备类 GUID</summary>
    private static Guid CameraClassGuid =
        new("CA3E7AB9-B4C3-4AE6-8251-579EF933890F");

    private static List<(string, string)> TrySetupApi()
    {
        var list = new List<(string, string)>();
        try
        {
            var cls = CameraClassGuid;
            IntPtr set = SetupDiGetClassDevsW(ref cls, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (set == new IntPtr(-1)) return list;

            try
            {
                for (uint i = 0; ; i++)
                {
                    var dd = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    if (!SetupDiEnumDeviceInfo(set, i, ref dd)) break;

                    string friendly = ReadDeviceString(set, ref dd, 0x0000000B);
                    if (string.IsNullOrWhiteSpace(friendly)) friendly = "摄像头";

                    // SPDRP_PHYSICAL_DEVICE_OBJECT_NAME(0x2) / SPDRP_DEVICE_OBJECT_NAME(0x1)
                    string path = ReadDeviceString(set, ref dd, 0x00000002);
                    if (string.IsNullOrWhiteSpace(path)) path = ReadDeviceString(set, ref dd, 0x00000001);

                    if (!string.IsNullOrWhiteSpace(path)) list.Add((friendly, path));
                    if (i > 64) break;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        catch (Exception ex) { App.Log("Camera", ex); }
        return list;
    }

    private static string ReadDeviceString(IntPtr set, ref SP_DEVINFO_DATA dd, uint prop)
    {
        uint regType = 0;
        uint need = 0;
        SetupDiGetDeviceRegistryPropertyW(set, ref dd, prop, out regType, IntPtr.Zero, 0, out need);
        if (need == 0) return "";
        var buf = Marshal.AllocHGlobal((int)need);
        try
        {
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref dd, prop, out regType, buf, need, out _))
                return "";
            return Marshal.PtrToStringUni(buf) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
