using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChronoIsland.Core;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace ChronoIsland.Sources;

/// <summary>
/// Windows 通知接管（docs §5.1）。
///
/// 【重要】直接用强类型 WinRT API，不用反射：
/// 反射调用 GetNotificationsAsync(0) 会因 int→NotificationKinds 无法转换而失败，
/// 这正是"通知读不到"的根因。TFM 为 net10.0-windows10.0.19041.0，编译期即可调用。
/// </summary>
public sealed class NotificationSource : IEventSource
{
    public string Key => "notification";
    public int Priority => 4;

    private EventBus? _bus;
    private UserNotificationListener? _listener;
    private readonly Dictionary<string, DateTime> _recentKeys = new();
    private readonly HashSet<uint> _seenIds = new();
    /// <summary>已接收但尚未从操作中心移除的通知 Id（"接管"的实现方式之一）</summary>
    private readonly List<uint> _ownedIds = new();
    private readonly TimeSpan _mergeWindow = TimeSpan.FromSeconds(120);

    private volatile bool _hasAccess;
    private System.Threading.Timer? _poll;
    private DateTimeOffset _startTime = DateTimeOffset.Now;
    /// <summary>详细日志（排查用，可用环境变量 CHRONO_NOTIFY_DEBUG=1 开启）</summary>
    private static readonly bool _verbose =
        Environment.GetEnvironmentVariable("CHRONO_NOTIFY_DEBUG") == "1";

    /// <summary>轮询间隔（借鉴 FluidBar：1600ms）</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    private void StartPolling()
    {
        _poll?.Dispose();
        _poll = new System.Threading.Timer(_ => { _ = DrainAsync(); },
            null, TimeSpan.FromMilliseconds(150), PollInterval);
    }
    public bool HasAccess => _hasAccess;

    /// <summary>最近一次权限检查结果（供设置界面查询）</summary>
    public static bool? LastKnownAccess { get; private set; }

    private const int MaxMergeCount = 20;

    public void Start(EventBus bus)
    {
        _bus = bus;
        _ = Task.Run(async () =>
        {
            try
            {
                _listener = UserNotificationListener.Current;
                var status = await _listener.RequestAccessAsync();
                _hasAccess = status == UserNotificationListenerAccessStatus.Allowed;
                LastKnownAccess = _hasAccess;

                if (!_hasAccess)
                {
                    App.LogInfo("Notification",
                        $"未取得通知访问权限（状态 {status}），已降级：岛仍可显示通知，但不抑制原生弹窗。");
                    return;
                }

                App.LogInfo("Notification", "已取得通知访问权限，开始轮询监听");

                // 【关键】不用 NotificationChanged 事件：桌面应用（无包标识）上该事件
                // 常常不触发。借鉴 FluidBar 的做法改用定时轮询（1.5 秒）。
                _startTime = DateTimeOffset.Now;

                // 先记录已有通知，避免把历史通知全部推一遍
                try
                {
                    var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                    foreach (var n in existing) _seenIds.Add(n.Id);
                    App.LogInfo("Notification", $"已跳过历史通知 {existing.Count} 条（保留在通知中心，不主动清理）");
                }
                catch (Exception ex) { App.Log("Notification", ex); }

                StartPolling();
            }
            catch (Exception ex)
            {
                App.LogInfo("Notification", "初始化失败，已降级：" + ex.Message);
            }
        });
    }

    /// <summary>主动查询权限（同步等待）</summary>
    public static bool QueryAccess(int timeoutMs = 3000)
    {
        try
        {
            var task = Task.Run(async () =>
            {
                var l = UserNotificationListener.Current;
                return await l.RequestAccessAsync();
            });
            if (!task.Wait(timeoutMs)) return LastKnownAccess ?? false;
            bool granted = task.Result == UserNotificationListenerAccessStatus.Allowed;
            LastKnownAccess = granted;
            return granted;
        }
        catch (Exception ex)
        {
            App.Log("Notification", ex);
            LastKnownAccess = false;
            return false;
        }
    }

    /// <summary>读取通知列表并推送到岛</summary>
    private async Task DrainAsync()
    {
        if (_listener == null || !_hasAccess) return;
        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (notifications == null) return;
            if (_verbose) App.LogInfo("Notification", $"轮询：当前 {notifications.Count} 条，已知 {_seenIds.Count} 条");

            foreach (var n in notifications.OrderBy(x => x.CreationTime))
            {
                // 只处理新出现的通知（纯 Id 去重）。
                // 不再用 CreationTime 过滤：PowerShell 等工具发的通知
                // 时间戳可能早于程序启动，会被误判为"历史通知"而漏掉。
                if (!_seenIds.Add(n.Id)) continue;

                var (appName, title, body, aumid) = Extract(n);
                App.LogInfo("Notification", $"捕获通知：{appName} | {title}");

                // 【鸠占鹊巢】顺序很重要：先把数据取出来，然后**立刻移除系统通知**，
                // 最后再推到岛。移除越早，原生横幅越可能来不及完全展开。
                RemoveFromCenter(n.Id);
                Emit(appName, title, body, aumid);
            }

            // 控制去重集合大小（保留最近 200 条）
            if (_seenIds.Count > 200)
            {
                var keep = _seenIds.Skip(_seenIds.Count - 200).ToHashSet();
                _seenIds.Clear();
                foreach (var id in keep) _seenIds.Add(id);
            }
        }
        catch (Exception ex) { App.Log("Notification", ex); }
    }

    /// <summary>
    /// 把系统通知从操作中心移除（"鸠占鹊巢"）。
    /// RemoveNotification 已实测可用：19 条 → 18 条。
    /// 移除后数据只存在岛上，由用户点"打开/忽略"决定何时消失，
    /// 不再跟随系统通知的消失逻辑。
    /// </summary>
    private void RemoveFromCenter(uint id)
    {
        try
        {
            _listener?.RemoveNotification(id);
            App.LogInfo("Notification", $"已从操作中心移除通知 id={id}");
        }
        catch (Exception ex) { App.Log("Notification", ex); }
    }

    /// <summary>从 UserNotification 提取应用名 / 标题 / 正文</summary>
    private static (string AppName, string Title, string Body, string Aumid) Extract(UserNotification n)
    {
        string appName = "应用", aumid = "";
        try { appName = n.AppInfo?.DisplayInfo?.DisplayName ?? "应用"; } catch { }
        try { aumid = n.AppInfo?.AppUserModelId ?? ""; } catch { }

        string title = appName, body = "";
        try
        {
            var visual = n.Notification?.Visual;
            if (visual != null)
            {
                foreach (var binding in visual.Bindings)
                {
                    var texts = binding.GetTextElements()?
                        .Select(t => t.Text)
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .ToList();
                    if (texts is { Count: > 0 })
                    {
                        title = texts[0]!;
                        if (texts.Count > 1) body = string.Join(" · ", texts.Skip(1));
                    }
                    break;      // 只取第一个 binding
                }
            }
        }
        catch { }
        return (appName, title, body, aumid);
    }

    private void Emit(string appName, string title, string body, string aumid = "")
    {
        var key = appName + "|" + title;
        int merge = 1;
        lock (_recentKeys)
        {
            if (_recentKeys.TryGetValue(key, out var last) && DateTime.Now - last < _mergeWindow)
            {
                merge = 2;      // 同 key 短时间内重复 → 标记合并（UI 侧显示 ×N）
            }
            _recentKeys[key] = DateTime.Now;
            // 清理过期 key
            if (_recentKeys.Count > 200)
            {
                var cutoff = DateTime.Now - _mergeWindow;
                foreach (var k in _recentKeys.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                    _recentKeys.Remove(k);
            }
        }

        _bus?.Publish(new IslandEvent(
            Id: Guid.NewGuid().ToString("N"),
            SourceKey: key,
            Kind: "notification",
            Priority: Priority,
            Title: title,
            Subtitle: body,
            // Ttl = null：通知不自动收起，必须用户点"打开"或"忽略"才消失。
            // 因为系统那条已被 RemoveNotification 移除，用户只能通过岛来处理它。
            Policy: new IslandPolicy(
                Ttl: null,
                Coalesce: true,
                Expandable: true,
                HoverPauses: true,
                QueueWhenHidden: true))
        {
            AppKey = ToAppKey(appName),
            SourceAumid = aumid,
            SourceDisplayName = appName,
            MergeCount = merge
        });
    }

    /// <summary>通知的显示名 → 可用于图标查找的进程名/AUMID</summary>
    private static string ToAppKey(string displayName)
    {
        var map = new (string Key, string Proc)[]
        {
            ("企业微信", "WXWork"), ("微信", "WeChat"), ("WeChat", "WeChat"),
            ("QQ", "QQ"), ("钉钉", "DingTalk"), ("飞书", "Feishu"), ("Lark", "Feishu"),
            ("Outlook", "outlook"), ("邮件", "outlook"), ("Mail", "outlook"),
            ("Teams", "Teams"), ("Slack", "slack"), ("Discord", "Discord"),
            ("Telegram", "Telegram"), ("Edge", "msedge"), ("Chrome", "chrome"),
            ("Firefox", "firefox"), ("Steam", "steam"), ("Spotify", "Spotify"),
            ("设置", "SystemSettings"), ("Windows 更新", "usoclient"),
            ("照片", "Photos"), ("计算器", "Calculator"), ("商店", "WinStore.App"),
            ("安全中心", "SecHealthUI"), ("Defender", "SecHealthUI"),
            ("资源管理器", "explorer"), ("文件资源管理器", "explorer")
        };
        foreach (var (k, proc) in map)
            if (displayName.Contains(k, StringComparison.OrdinalIgnoreCase)) return proc;
        return displayName;
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        _bus = null;
    }
}
