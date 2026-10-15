using System.Collections.Generic;
using System.Linq;

namespace ChronoIsland.Core;

/// <summary>游戏/全屏隐藏期间的通知暂存（docs §2.3）。上限 50，同 key 合并。</summary>
public sealed class PendingQueue
{
    private const int MaxSize = 50;
    private readonly List<IslandEvent> _items = new();
    public static PendingQueue Instance { get; } = new();

    public int Count => _items.Count;

    public void Enqueue(IslandEvent ev)
    {
        var existing = _items.FirstOrDefault(i => i.SourceKey == ev.SourceKey);
        if (existing != null) _items.Remove(existing);      // 同 key 合并为最新
        _items.Insert(0, ev);
        while (_items.Count > MaxSize) _items.RemoveAt(_items.Count - 1);
    }

    public IslandEvent? Dequeue()
    {
        if (_items.Count == 0) return null;
        var ev = _items[0];
        _items.RemoveAt(0);
        return ev;
    }

    public void Clear() => _items.Clear();
}
