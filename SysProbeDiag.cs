using System;
using ChronoIsland.Native;

public static class SysProbeDiag
{
    public static void Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== 系统状态探测自检 ===");
        var b = SystemProbe.ReadBattery();
        Console.WriteLine($"电池: Has={b.HasBattery} {b.Percent}% 充电={b.Charging} 接电={b.AcOnline} 剩余={b.SecondsRemaining}s 节电={b.SaverOn} 低电量={b.IsLow} 充满={b.IsFull}");
        var w = SystemProbe.ReadWifi();
        Console.WriteLine($"WiFi: 连接={w.Connected} SSID={w.Ssid} 信号={w.SignalPercent}% 速率={w.LinkMbps}Mbps 电台={w.WifiRadioOn}");
        Console.WriteLine($"网速: 下行={Fmt(w.RxBps)} 上行={Fmt(w.TxBps)}");
        var h = SystemProbe.ReadHotspot();
        Console.WriteLine($"热点: 支持={h.Supported} 开启={h.On} 设备={h.Clients} [{string.Join(",", h.ClientAddresses)}]");
        var r = SystemProbe.ReadRadio();
        Console.WriteLine($"飞行模式: {r.AirplaneMode}  WiFi电台: {r.WifiRadioOn}");
        var m = SystemProbe.ReadMicrophone();
        Console.WriteLine($"麦克风: 占用={m.Busy} 程序={m.AppName} 数量={m.Users}");
        Console.WriteLine();
        Console.WriteLine("=== 第二次采样（验证差分） ===");
        System.Threading.Thread.Sleep(1200);
        w = SystemProbe.ReadWifi();
        Console.WriteLine($"网速: 下行={Fmt(w.RxBps)} 上行={Fmt(w.TxBps)}");
    }

    private static string Fmt(double bps)
    {
        if (bps < 1024) return $"{bps:0} B/s";
        if (bps < 1024 * 1024) return $"{bps / 1024:0.0} KB/s";
        return $"{bps / 1024.0 / 1024.0:0.00} MB/s";
    }
}
