using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace ChronoIsland.Native;

/// <summary>
/// 蓝牙连接状态探测（docs §5.14）。
///
/// 【为什么用 WinRT 而不是 PnP / 注册表】
/// `Get-PnpDevice` 里已配对但**没连上**的设备同样显示 `Status=OK`（它们只是"已安装的节点"），
/// 用 PnP 状态判断连接必然误报——实测耳机明明没连，PnP 仍显示 OK。
/// 而 `BluetoothDevice.ConnectionStatus` / `BluetoothLEDevice.ConnectionStatus`
/// 是系统蓝牙栈给出的**真实链路状态**，是这里唯一可信的判据。
///
/// 【经典 + BLE 必须都查】
/// 耳机 / 音箱 / 手机走经典蓝牙（BR/EDR），鼠标 / 键盘 / 手环走 BLE。
/// 只查一边会漏设备：实测本机经典侧只有 3 台，BLE 侧另有 5 台（含那只鼠标）。
///
/// 【为什么用 FromIdAsync 逐台取状态，而不是按状态过滤的 selector】
/// `GetDeviceSelectorFromConnectionStatus` 的 AQS 在部分系统版本上不刷新（缓存旧值），
/// 而逐台 `FromIdAsync` 拿到的是调用时刻的真实状态。代价是每台一次异步调用——
/// 但设备通常只有个位数，且我们本来就是 3s 一次的轮询，完全够用。
/// </summary>
public static class BluetoothProbe
{
    /// <summary>一次蓝牙扫描的完整结果</summary>
    /// <param name="Supported">本机是否有可用的蓝牙适配器</param>
    /// <param name="Connected">当前已连接的设备名（去重、已排序）</param>
    /// <param name="PairedCount">已配对设备总数（含未连接）</param>
    public sealed record Result(bool Supported, IReadOnlyList<string> Connected, int PairedCount)
    {
        public int Count => Connected.Count;
        public static readonly Result Unsupported = new(false, Array.Empty<string>(), 0);
    }

    /// <summary>扫描超时：蓝牙栈卡住时不能让轮询线程挂死</summary>
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMilliseconds(2500);

    /// <summary>近 N 秒内扫过一次就复用结果，避免多个调用方叠加扫描</summary>
    private static Result _last = Result.Unsupported;
    private static DateTime _lastAt = DateTime.MinValue;
    private static readonly object Gate = new();

    /// <summary>
    /// 读蓝牙连接状态（带 1.5s 结果缓存）。
    ///
    /// 【为什么带缓存】这个方法会被"差分轮询"和"详情卡实时读数"两处调用，
    /// 而同一次 UI 刷新里可能连调几次。每次真扫一遍要几十毫秒并打断蓝牙栈的电源管理，
    /// 缓存一下既省电也避免读数在同一次刷新里自相矛盾。
    /// </summary>
    public static Result Read(bool force = false)
    {
        lock (Gate)
        {
            if (!force && DateTime.UtcNow - _lastAt < TimeSpan.FromMilliseconds(1500))
                return _last;
        }

        var result = Scan();
        lock (Gate)
        {
            _last = result;
            _lastAt = DateTime.UtcNow;
        }
        return result;
    }

    private static Result Scan()
    {
        try
        {
            if (!HasAdapter()) return Result.Unsupported;

            var connected = new List<string>();
            var allIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ---- 经典蓝牙（耳机 / 音箱 / 手机）----
            Collect(BluetoothDevice.GetDeviceSelector(), isLe: false, connected, allIds);

            // ---- 低功耗蓝牙（鼠标 / 键盘 / 手环）----
            Collect(BluetoothLEDevice.GetDeviceSelector(), isLe: true, connected, allIds);

            // 同名设备（耳机的 A2DP 与 HFP 两条链路）只报一次
            var distinct = connected
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCulture)
                .ToList();

            return new Result(true, distinct, allIds.Count);
        }
        catch (Exception ex)
        {
            App.Log("Bluetooth", ex);
            return Result.Unsupported;
        }
    }

    /// <summary>枚举一侧设备，把"已连接"的设备名收进来</summary>
    private static void Collect(string selector, bool isLe,
        List<string> connected, HashSet<string> allIds)
    {
        try
        {
            var findTask = DeviceInformation.FindAllAsync(selector).AsTask();
            if (!findTask.Wait(ScanTimeout)) return;
            var devices = findTask.Result;
            if (devices == null) return;

            foreach (var di in devices)
            {
                allIds.Add(di.Id);
                try
                {
                    if (isLe)
                    {
                        var t = BluetoothLEDevice.FromIdAsync(di.Id).AsTask();
                        if (!t.Wait(ScanTimeout)) continue;
                        var dev = t.Result;
                        if (dev == null) continue;
                        try
                        {
                            if (dev.ConnectionStatus == BluetoothConnectionStatus.Connected)
                                connected.Add(PickName(di, dev.Name));
                        }
                        finally { dev.Dispose(); }
                    }
                    else
                    {
                        var t = BluetoothDevice.FromIdAsync(di.Id).AsTask();
                        if (!t.Wait(ScanTimeout)) continue;
                        var dev = t.Result;
                        if (dev == null) continue;
                        try
                        {
                            if (dev.ConnectionStatus == BluetoothConnectionStatus.Connected)
                                connected.Add(PickName(di, dev.Name));
                        }
                        finally { dev.Dispose(); }
                    }
                }
                catch { /* 单台读不到不影响其它设备 */ }
            }
        }
        catch (Exception ex) { App.Log("Bluetooth", ex); }
    }

    /// <summary>
    /// 设备名：DeviceInformation.Name 通常是系统里的友好名，
    /// 取不到时退回蓝牙设备自己报告的名字。
    /// </summary>
    private static string PickName(DeviceInformation di, string? devName)
    {
        if (!string.IsNullOrWhiteSpace(di.Name)) return di.Name;
        if (!string.IsNullOrWhiteSpace(devName)) return devName!;
        return "蓝牙设备";
    }

    /// <summary>本机是否有蓝牙适配器（用系统蓝牙无线电节点的存在性判断）</summary>
    private static bool HasAdapter()
    {
        try
        {
            // 走 WinRT：能拿到任意一台 BluetoothDevice/BluetoothLEDevice 就说明有适配器。
            // 无适配器时下面两个 selector 都返回 0 台，但"有没有适配器"需要单独判，
            // 否则"没有设备"与"没有蓝牙"两种情况会被混为一谈。
            var radios = BluetoothAdapter.GetDefaultAsync().AsTask();
            if (radios.Wait(ScanTimeout))
                return radios.Result != null;
        }
        catch { }
        return false;
    }
}
