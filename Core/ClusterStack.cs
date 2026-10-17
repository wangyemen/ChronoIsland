using System.Collections.Generic;
using System.Linq;

namespace ChronoIsland.Core;

/// <summary>一条岛的布局目标</summary>
public readonly record struct Slot(double X, double Width, double Height, IslandRole Role, bool Offscreen);

/// <summary>
/// 集群式堆叠：中央 1 个胶囊 + 左右各 2 个球体（共 5 个可见），超出为 +N。
/// 点击球体与胶囊互换角色。
/// </summary>
public sealed class ClusterStack
{
    private readonly List<IslandItem> _items = new();
    private string? _activeId;
    private bool _expanded;

    public IReadOnlyList<IslandItem> Items => _items;
    public IslandItem? Active => _items.FirstOrDefault(i => i.Event.Id == _activeId);
    public bool IsExpanded => _expanded;
    public int TotalCount => _items.Count;

    /// <summary>可见（非溢出）数量</summary>
    public int VisibleCount => ComputeSlots().Count(kv => !kv.Value.Offscreen);

    /// <summary>溢出数量（+N）</summary>
    public int OverflowCount => ComputeSlots().Count(kv => kv.Value.Offscreen);

    public void Add(IslandItem item)
    {
        _items.Insert(0, item);          // 最新置顶

        // 不抢焦点的事件（如"暂停中的媒体会话"）保持当前胶囊不变
        if (item.Event.StealFocus || _activeId == null)
        {
            _activeId = item.Event.Id;
            _expanded = false;
        }
        else
        {
            item.Role = IslandRole.Sphere;
        }
    }

    public void Remove(IslandItem item)
    {
        item.UnregisterAll();
        _items.Remove(item);
        if (_activeId == item.Event.Id)
        {
            var next = _items.FirstOrDefault();
            _activeId = next?.Event.Id;
            if (next != null) { next.Role = IslandRole.Pill; }
        }
    }

    public void Clear()
    {
        foreach (var i in _items) i.UnregisterAll();
        _items.Clear();
        _activeId = null;
        _expanded = false;
    }

    public void SetActive(string id)
    {
        if (_activeId == id) return;
        var target = _items.FirstOrDefault(i => i.Event.Id == id);
        if (target == null) return;
        _activeId = id;
        _expanded = false;
        target.Role = IslandRole.Pill;
        target.Event.IsUnread = false;
    }

    public void SetExpanded(bool on)
    {
        _expanded = on && Active != null;
        if (Active != null) Active.Role = _expanded ? IslandRole.Card : IslandRole.Pill;
    }

    /// <summary>
    /// 计算所有岛的布局目标。每帧调用——弹簧自己决定怎么走过去。
    /// </summary>
    public Dictionary<string, Slot> ComputeSlots()
    {
        var map = new Dictionary<string, Slot>();
        if (_items.Count == 0) return map;

        var active = Active ?? _items[0];
        var ai = _items.IndexOf(active);

        bool card = _expanded && active.Role == IslandRole.Card;
        double cw = card ? IslandMetrics.CardWidth : (active.MeasuredWidth > 0 ? active.MeasuredWidth : IslandMetrics.MaxWidth);
        cw = Math.Clamp(cw, IslandMetrics.MinWidth, IslandMetrics.MaxWidth);
        // 展开卡高度按"内容实测"取值（MeasuredCardHeight），并夹在 [96, 260] 之间。
        // 【不要写死 176】各卡内容差异很大（媒体卡 ~150 / 通知卡可 3 行正文），
        // 写死会导致内容少的卡底部留一大块空白、内容多的卡被裁切。
        // 上下限按缩放同步放大，保持"缩放后仍是同一张卡"的观感
        // MeasuredCardHeight 已是"未缩放的内容需求"，缩放由渲染端施加，
        // 因此这里上下限也必须用未缩放口径，否则会二次缩放。
        double minH = IslandMetrics.CardMinHeight;
        double maxH = IslandMetrics.CardMaxHeight;
        double ch = card
            ? Math.Clamp(active.MeasuredCardHeight > 0 ? active.MeasuredCardHeight : minH,
                         minH, maxH)
            : IslandMetrics.IslandHeight;

        map[active.Event.Id] = new Slot(0, cw, ch, card ? IslandRole.Card : IslandRole.Pill, false);

        // 除 active 外，交替填充左右：第 0 条→左1，第 1 条→右1，第 2 条→左2 …
        var others = _items.Where(i => i.Event.Id != active.Event.Id).ToList();
        for (int rank = 0; rank < others.Count; rank++)
        {
            var it = others[rank];
            int dir = (rank % 2 == 0) ? -1 : 1;
            int tier = rank / 2;

            if (tier >= IslandMetrics.MaxSideSpheres)
            {
                // 超出 5 个可见上限 → 停在最外层并淡出，计入 +N
                double far = IslandMetrics.SphereOffset(cw, IslandMetrics.MaxSideSpheres, dir);
                map[it.Event.Id] = new Slot(far, IslandMetrics.SphereSize, IslandMetrics.IslandHeight, IslandRole.Sphere, true);
                continue;
            }
            double off = IslandMetrics.SphereOffset(cw, tier, dir);
            map[it.Event.Id] = new Slot(off, IslandMetrics.SphereSize, IslandMetrics.IslandHeight, IslandRole.Sphere, false);
        }
        return map;
    }

    /// <summary>环形切换（键盘 ←/→）</summary>
    public void Cycle(int delta)
    {
        if (_items.Count == 0) return;
        var i = Active != null ? _items.IndexOf(Active) : 0;
        var n = (i + delta + _items.Count) % _items.Count;
        SetActive(_items[n].Event.Id);
    }
}
