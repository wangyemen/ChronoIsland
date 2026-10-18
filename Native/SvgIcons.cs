using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfPath = System.Windows.Shapes.Path;
using System.Xml.Linq;
using ChronoIsland.UI;

namespace ChronoIsland.Native;

/// <summary>
/// SVG 图标渲染器（docs §5.6）。
///
/// 图标来源：Lucide（https://lucide.dev），ISC 许可，免费开源，可商用。
/// 原始文件在 Assets/Icons/*.svg，以 EmbeddedResource 嵌入程序集，
/// 运行时解析为 WPF Geometry 绘制 —— 不引入任何第三方图形库。
///
/// Lucide 是 stroke 风格（fill=none + stroke=currentColor），
/// 因此 fill 恒为 null，由 Pen 描边；线宽随目标尺寸等比换算。
/// </summary>
public static class SvgIcons
{
    private const string ResourcePrefix = "ChronoIsland.Assets.Icons.";

    // name -> 解析结果。stroke=描边几何（path/circle/ellipse/line），
    // fill=填充几何（polygon/rect 或显式 fill="currentColor" 的元素）。
    // 两者分开，渲染时叠成同一个 Grid，这样 play/pause 这类实心图标才不会被画成空心轮廓。
    public sealed record IconGeometry(Geometry? Stroke, Geometry? Fill);

    private static readonly Dictionary<string, IconGeometry?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>可用图标名（设置页枚举与文档用）</summary>
    public static readonly string[] Names =
    {
        // ---- 通用 / 原有 ----
        "bell", "bell-off", "camera", "camera-off", "capslock", "check", "clock", "close",
        "eye", "eye-off", "info", "keyboard", "lock", "monitor",
        "music", "next", "numlock", "pause", "play", "power", "prev",
        "scrolllock", "settings", "shield", "sparkles", "target", "timer", "unlock",
        // ---- 0.0.1 系统状态补充（电源 / 网络 / 麦克风 / 外观）----
        "mic", "mic-off", "battery", "battery-low", "battery-full", "battery-charging",
        "plug", "wifi", "wifi-off", "router", "plane", "sun", "moon", "sun-moon",
        "arrow-down", "arrow-up", "smartphone", "gauge",
        "signal-high", "signal-medium", "signal-low", "signal-zero"
    };

    /// <summary>Lucide 统一 viewBox 边长</summary>
    private const double ViewBox = 24.0;

    // ================= 对外 API =================

    /// <summary>取图标几何（24×24 坐标系，已 Freeze）。仅描边层，供外部自绘使用。</summary>
    public static Geometry? GetGeometry(string name)
    {
        var g = GetIcon(name);
        if (g == null) return null;
        // 填充优先（实心图标只取填充即可），否则取描边
        return g.Fill ?? g.Stroke;
    }

    /// <summary>取图标的填充 / 描边几何</summary>
    public static IconGeometry? GetIconParts(string name) => GetIcon(name);

    private static IconGeometry? GetIcon(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(name, out var hit)) return hit;
            var geo = ParseSvg(LoadSvgText(name));
            Cache[name] = geo;
            return geo;
        }
    }

    /// <summary>
    /// 创建图标控件，自动按目标尺寸缩放并换算线宽。
    /// 填充层（polygon / rect）画在下、描边层（path / circle）画在上，
    /// 因此 play、pause、prev、next 这类实心图标不会被画成空心轮廓。
    /// </summary>
    /// <param name="name">图标名（见 <see cref="Names"/>）</param>
    /// <param name="size">目标边长 px</param>
    /// <param name="stroke">图标颜色（描边层用它，填充层同样用它）</param>
    /// <param name="strokeScale">线宽相对值，1 = Lucide 默认 1.75</param>
    public static FrameworkElement Create(string name, double size, Brush? stroke, double strokeScale = 1.0)
    {
        var parts = GetIcon(name);
        if (parts == null) return Fallback(size, stroke);

        // 固定尺寸 + 居中：Grid 本身是 size×size，内部 Path 填满它，
        // 这样无论谁把它 Stretch 到更大的容器里，图标都保持居中且按比例缩放。
        var host = new Grid
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        if (parts.Fill != null)
            host.Children.Add(MakePath(parts.Fill, size, stroke, strokeScale, fillOnly: true));

        if (parts.Stroke != null)
            host.Children.Add(MakePath(parts.Stroke, size, stroke, strokeScale, fillOnly: false));

        if (host.Children.Count == 0) return Fallback(size, stroke);
        return host;
    }

    /// <summary>
    /// 把 24×24 的几何画进 size×size 的方框。
    ///
    /// 两个必须做对的点：
    /// 1. **不能用 Stretch**。WPF 的 Stretch 是按几何自身 Bounds 等比缩放，
    ///    而 Lucide 图标的元素只占 24×24 里的一部分（例如 pause 的两根竖条
    ///    Bounds 是 6,4 → 18,20），Stretch 会把这个偏窄的 Bounds 撑满方框 →
    ///    图形被压扁变形。
    /// 2. **不能用 RenderTransform + Stretch.None**。WPF 会先按几何原生尺寸绘制、
    ///    再裁剪到 Path 的 Width/Height，最后才施加 RenderTransform ——
    ///    结果只显示图标的一角（这正是"上一首 / 下一首看不见"的原因）。
    ///
    /// 正确解法：给 Path 设 Stretch=None、尺寸=size×size，并把 ScaleTransform
    /// 放进 GeometryGroup.Transform（几何层缩放，会连同 StrokeThickness 一起缩放，
    /// 不会被裁剪）。再把几何平移到以 0,0 为原点，保证缩放后落在方框内。
    /// </summary>
    private static WpfPath MakePath(Geometry geo, double size, Brush? stroke,
        double strokeScale, bool fillOnly)
    {
        double k = size / ViewBox;

        // 在**几何层**做等比缩放，而不是用 Path 的 Stretch 或 RenderTransform：
        //   · 几何层变换会把 StrokeThickness 一并缩放，线宽比例才正确
        //   · 缩放后几何的自然尺寸恰好是 size×size，Path 用 Stretch=None 按自然尺寸绘制
        //     —— 既不二次缩放，也不裁切
        //
        // 关键：**必须按 viewBox 原点 (0,0) 统一缩放**。
        // 绝不能"先按各自 Bounds 平移到原点再缩放"，否则填充层与描边层各自对齐自己的
        // 原点，两者会错位（上一首的三角与竖线不再重合）。
        // Lucide 所有元素坐标本就落在 0..24 内，统一按 k 缩放即天然对齐。
        var scaled = new GeometryGroup();
        scaled.Children.Add(geo);
        scaled.Transform = new ScaleTransform(k, k);
        scaled.Freeze();

        return new WpfPath
        {
            Data = scaled,
            // 填充层用实心色；描边层 fill=null —— Lucide 的 stroke 图标不该被填满，
            // 而 play / pause / prev / next 这类 polygon、rect 是实心图形。
            Fill = fillOnly ? stroke : null,
            Stroke = stroke,
            StrokeThickness = fillOnly ? 0 : 1.75 * strokeScale,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false
        };
    }

    /// <summary>居中包装：把 <see cref="Create(string,double,Brush?,double)"/> 的左上原点锚定改成居中</summary>
    public static FrameworkElement CreateCentered(string name, double size, Brush? stroke,
        double strokeScale = 1.0)
    {
        var el = Create(name, size, stroke, strokeScale);
        el.HorizontalAlignment = HorizontalAlignment.Center;
        el.VerticalAlignment = VerticalAlignment.Center;
        return el;
    }

    /// <summary>
    /// 键帽：圆角方块 + 居中图标（系统状态胶囊用）。
    /// 开=强调色实底，关=行面底色虚描边。
    /// </summary>
    public static FrameworkElement CreateKeycap(string name, double box, bool on, ThemeTokens t)
    {
        var border = new Border
        {
            Width = box,
            Height = box,
            CornerRadius = new CornerRadius(Math.Max(3, box * 0.27)),
            Background = on ? t.Accent : t.RowSurface,
            BorderBrush = on ? t.Accent : t.Stroke,
            BorderThickness = new Thickness(1)
        };
        var host = new Grid();
        host.Children.Add(CreateCentered(name, box * 0.58, on ? Brushes.White : t.TextSecondary, 1.15));
        border.Child = host;
        return border;
    }

    /// <summary>
    /// 球体图标视图：容器铺满球体（36px），图标严格居中。
    /// 关键：host 必须显式 Stretch + ClipToBounds，且内部用 CreateCentered
    /// （Create() 返回的 Grid 是 Left/Top 锚定，直接 Stretch 会把图标推到左上角）。
    /// </summary>
    public static FrameworkElement CreateSphere(string name, double box, Brush stroke)
    {
        var host = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true
        };
        var inner = CreateCentered(name, box, stroke, 1.2);
        host.Children.Add(inner);
        return host;
    }

    private static FrameworkElement Fallback(double size, Brush? stroke) => new TextBlock
    {
        Text = "?",
        FontSize = size * 0.8,
        Foreground = stroke ?? Brushes.Gray,
        Width = size,
        Height = size,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    // ================= 资源读取 =================

    private static string? LoadSvgText(string name)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + name + ".svg");
            if (s != null)
            {
                using var sr = new StreamReader(s, Encoding.UTF8);
                return sr.ReadToEnd();
            }
        }
        catch { /* 回落到磁盘 */ }

        // 回落：开发态从项目目录读
        foreach (var dir in new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Icons"),
            System.IO.Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Icons"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Assets", "Icons")
        })
        {
            try
            {
                var f = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, name + ".svg"));
                if (File.Exists(f)) return File.ReadAllText(f);
            }
            catch { }
        }
        return null;
    }

    // ================= SVG 解析 =================

    /// <summary>
    /// 解析 SVG → 填充几何 / 描边几何。
    ///
    /// 判定规则（Lucide 的约定）：
    ///   · &lt;polygon&gt; &lt;rect&gt; 默认是**实心**图形 → 走 Fill 层
    ///   · &lt;path&gt; &lt;circle&gt; &lt;ellipse&gt; &lt;line&gt; 是**描边**图形 → 走 Stroke 层
    ///   · 若元素显式写了 fill="currentColor" 或 fill="none"，则以显式值为准
    ///   · 圆角矩形的 &lt;rect rx&gt; 会自动转成 Path（避免 RectangleGeometry 只能整体填充的歧义）
    /// </summary>
    private static IconGeometry? ParseSvg(string? svgText)
    {
        if (string.IsNullOrWhiteSpace(svgText)) return null;
        XDocument doc;
        try { doc = XDocument.Parse(svgText, LoadOptions.None); }
        catch { return null; }

        var root = doc.Root;
        if (root == null) return null;

        var strokes = new GeometryGroup();
        var fills = new GeometryGroup();
        bool anyStroke = false, anyFill = false;

        foreach (var el in root.Descendants())
        {
            // 显式 fill 覆盖默认判定
            string? fillAttr = el.Attribute("fill")?.Value?.Trim();

            // 填充图形：polygon / rect
            bool isFillShape = el.Name.LocalName is "polygon" or "rect";

            // 例外：Lucide 的 &lt;rect&gt; 两极分化 ——
            //   pause 的两根竖条是**实心**（w 小 h 大）→ 填充
            //   lock / monitor 的方框是**描边**（w > h 或接近正方形）→ 描边
            // 用宽高比区分：h > w * 1.5 视为实心竖条，否则视为描边方框。
            if (el.Name.LocalName == "rect")
            {
                double rw = Attr(el, "width", 0), rh = Attr(el, "height", 0);
                isFillShape = rh > rw * 1.5;
            }

            // 显式 fill="none" → 强制描边
            if (string.Equals(fillAttr, "none", StringComparison.OrdinalIgnoreCase))
                isFillShape = false;

            var g = ParseElement(el);
            if (g == null) continue;

            if (isFillShape) { fills.Children.Add(g); anyFill = true; }
            else { strokes.Children.Add(g); anyStroke = true; }
        }

        if (!anyStroke && !anyFill) return null;

        // 几何层不做任何缩放/平移：所有图标保持 24×24 原始坐标，
        // 统一的映射由 MakePath 负责（Stretch.Uniform + Bounds 钉在 viewBox），
        // 保证填充层与描边层严格对齐。

        Geometry? fg = null, sg = null;
        if (anyStroke) { strokes.Freeze(); sg = strokes; }
        if (anyFill) { fills.Freeze(); fg = fills; }
        return new IconGeometry(sg, fg);
    }

    private static Geometry? ParseElement(XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "path":
                {
                    var d = el.Attribute("d")?.Value;
                    return string.IsNullOrWhiteSpace(d) ? null : PathGeometryBuilder.Build(d);
                }
            case "circle":
                {
                    double cx = Attr(el, "cx", 0), cy = Attr(el, "cy", 0), r = Attr(el, "r", 0);
                    return r > 0 ? new EllipseGeometry(new Rect(cx - r, cy - r, r * 2, r * 2)) : null;
                }
            case "ellipse":
                {
                    double cx = Attr(el, "cx", 0), cy = Attr(el, "cy", 0);
                    double rx = Attr(el, "rx", 0), ry = Attr(el, "ry", 0);
                    return rx > 0 && ry > 0 ? new EllipseGeometry(new Rect(cx - rx, cy - ry, rx * 2, ry * 2)) : null;
                }
            case "rect":
                {
                    double x = Attr(el, "x", 0), y = Attr(el, "y", 0);
                    double w = Attr(el, "width", 0), h = Attr(el, "height", 0);
                    if (w <= 0 || h <= 0) return null;
                    double rx = Attr(el, "rx", 0), ry = Attr(el, "ry", 0);
                    if (rx <= 0) rx = ry;
                    if (ry <= 0) ry = rx;
                    return new RectangleGeometry(new Rect(x, y, w, h),
                        Math.Min(rx, w / 2), Math.Min(ry, h / 2));
                }
            case "line":
                {
                    double x1 = Attr(el, "x1", 0), y1 = Attr(el, "y1", 0);
                    double x2 = Attr(el, "x2", 0), y2 = Attr(el, "y2", 0);
                    return PathGeometryBuilder.Build(
                        "M " + Fmt(x1) + " " + Fmt(y1) + " L " + Fmt(x2) + " " + Fmt(y2));
                }
            case "polyline":
            case "polygon":
                {
                    var pts = el.Attribute("points")?.Value;
                    if (string.IsNullOrWhiteSpace(pts)) return null;
                    var nums = ParseNumbers(pts);
                    if (nums.Count < 4) return null;
                    var sb = new StringBuilder("M " + Fmt(nums[0]) + " " + Fmt(nums[1]));
                    for (int i = 2; i + 1 < nums.Count; i += 2)
                        sb.Append(" L " + Fmt(nums[i]) + " " + Fmt(nums[i + 1]));
                    // polygon / polyline 都按填充语义处理（子路径自动闭合）
                    return PathGeometryBuilder.Build(sb.ToString(), closeAll: true);
                }
            default:
                return null;
        }
    }

    private static double Attr(XElement el, string name, double fallback)
        => double.TryParse(el.Attribute(name)?.Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static List<double> ParseNumbers(string s)
    {
        var list = new List<double>();
        foreach (var tok in s.Split(new[] { ' ', ',', '\n', '\r', '\t' },
                     StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                list.Add(v);
        return list;
    }
}

/// <summary>SVG path data → WPF StreamGeometry（支持 A 弧线转贝塞尔）</summary>
internal static class PathGeometryBuilder
{
    /// <summary>
    /// 解析 path d。
    /// </summary>
    /// <param name="d">path data</param>
    /// <param name="closeAll">
    /// true → 所有子路径视为闭合（填充语义：SVG 填充时子路径自动闭合）。
    /// 描边语义下则只按显式的 Z 闭合。
    /// </param>
    public static Geometry? Build(string d, bool closeAll = false)
    {
        try
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open()) new PathWalker(ctx, d ?? "", closeAll).Run();
            geo.Freeze();
            return geo;
        }
        catch { return null; }
    }

    private sealed class PathWalker
    {
        private readonly StreamGeometryContext _c;
        private readonly string _d;
        private readonly bool _closeAll;
        private int _i;
        private Point _cur, _start;
        private Point? _lastCubicCtrl;
        private Point? _lastQuadCtrl;
        private char _cmd;

        public PathWalker(StreamGeometryContext c, string d, bool closeAll)
        {
            _c = c; _d = d; _closeAll = closeAll;
            _cur = _start = new Point(0, 0);
        }

        /// <summary>每个子路径是否闭合</summary>
        private bool FigureIsClosed()
        {
            // 填充语义下 SVG 会自动闭合子路径
            if (_closeAll) return true;
            // 描边语义：下一个 M/m 之前出现 Z/z 才算闭合
            for (int k = _i; k < _d.Length; k++)
            {
                char ch = _d[k];
                if (ch == 'Z' || ch == 'z') return true;
                if (ch == 'M' || ch == 'm') return false;
            }
            return false;
        }

        public void Run()
        {
            while (true)
            {
                SkipSep();
                if (_i >= _d.Length) break;

                char ch = _d[_i];
                if (char.IsLetter(ch))
                {
                    _cmd = ch;
                    _i++;
                    if (_cmd is 'Z' or 'z')
                    {
                        _c.LineTo(_start, true, true);
                        _cur = _start;
                        _lastCubicCtrl = _lastQuadCtrl = null;
                        continue;
                    }
                }
                else if (_cmd == 0) break;
                else if (_cmd == 'M') _cmd = 'L';   // 隐式后续按绝对直线
                else if (_cmd == 'm') _cmd = 'l';   // 隐式后续按相对直线

                if (!Step()) break;
            }
        }

        private bool Step() => _cmd switch
        {
            // 大写 = 绝对坐标，小写 = 相对坐标。
            // 原实现把两者写反了："M19 20" 被当成相对增量累加到 (0,0)，
            // 结果 prev 的三角形跑到了 19,20 → 28,32 → 47,36，整体错位。
            'M' => MoveToAbs(),
            'm' => MoveToRel(),
            'L' => LineToAbs(),
            'l' => LineToRel(),
            'H' => HLine(false),
            'h' => HLine(true),
            'V' => VLine(false),
            'v' => VLine(true),
            'C' => Cubic(false),
            'c' => Cubic(true),
            'S' => CubicSmooth(false),
            's' => CubicSmooth(true),
            'Q' => Quad(false),
            'q' => Quad(true),
            'T' => QuadSmooth(false),
            't' => QuadSmooth(true),
            'A' => Arc(false),
            'a' => Arc(true),
            _ => Fail()
        };

        private bool Fail() { _i = _d.Length; return false; }

        // ---------- 词法 ----------

        private void SkipSep()
        {
            while (_i < _d.Length && (char.IsWhiteSpace(_d[_i]) || _d[_i] == ',')) _i++;
        }

        private bool Num(out double v)
        {
            SkipSep();
            int start = _i;
            if (_i < _d.Length && (_d[_i] == '+' || _d[_i] == '-')) _i++;
            while (_i < _d.Length && char.IsDigit(_d[_i])) _i++;
            if (_i < _d.Length && _d[_i] == '.')
            {
                _i++;
                while (_i < _d.Length && char.IsDigit(_d[_i])) _i++;
            }
            if (_i < _d.Length && (_d[_i] == 'e' || _d[_i] == 'E'))
            {
                int save = _i;
                _i++;
                if (_i < _d.Length && (_d[_i] == '+' || _d[_i] == '-')) _i++;
                if (_i < _d.Length && char.IsDigit(_d[_i]))
                    while (_i < _d.Length && char.IsDigit(_d[_i])) _i++;
                else _i = save;
            }
            if (_i == start) { v = 0; return false; }
            return double.TryParse(_d.AsSpan(start, _i - start), NumberStyles.Float,
                CultureInfo.InvariantCulture, out v);
        }

        /// <summary>0/1 标志位（允许与大数无分隔，如 "0 011 1"）</summary>
        private bool Flag(out bool val)
        {
            SkipSep();
            if (_i >= _d.Length || (_d[_i] != '0' && _d[_i] != '1')) { val = false; return false; }
            val = _d[_i] == '1';
            _i++;
            return true;
        }

        /// <summary>相对坐标：当前点 + 增量</summary>
        private Point Pt(bool rel, double x, double y)
            => rel ? new Point(_cur.X + x, _cur.Y + y) : new Point(x, y);

        // ---------- 命令 ----------

        /// <summary>M：绝对坐标，开启新图（子路径起点）</summary>
        private bool MoveToAbs()
        {
            if (!Num(out var x) || !Num(out var y)) return false;
            var p = new Point(x, y);
            _c.BeginFigure(p, true, FigureIsClosed());
            _cur = _start = p;
            _lastCubicCtrl = _lastQuadCtrl = null;
            return true;
        }

        /// <summary>相对 m：开启新图，坐标相对当前点</summary>
        private bool MoveToRel()
        {
            if (!Num(out var x) || !Num(out var y)) return false;
            var p = Pt(true, x, y);
            _c.BeginFigure(p, true, FigureIsClosed());
            _cur = _start = p;
            _lastCubicCtrl = _lastQuadCtrl = null;
            return true;
        }

        /// <summary>L：绝对坐标直线</summary>
        private bool LineToAbs()
        {
            if (!Num(out var x) || !Num(out var y)) return false;
            var p = new Point(x, y);
            _c.LineTo(p, true, true);
            _cur = p;
            _lastCubicCtrl = _lastQuadCtrl = null;
            return true;
        }

        /// <summary>l：相对坐标直线</summary>
        private bool LineToRel()
        {
            if (!Num(out var x) || !Num(out var y)) return false;
            var p = Pt(true, x, y);
            _c.LineTo(p, true, true);
            _cur = p;
            _lastCubicCtrl = _lastQuadCtrl = null;
            return true;
        }

        private bool HLine(bool rel)
        {
            if (!Num(out var x)) return false;
            _cur = new Point(rel ? _cur.X + x : x, _cur.Y);
            _c.LineTo(_cur, true, true);
            _lastCubicCtrl = null;
            return true;
        }

        private bool VLine(bool rel)
        {
            if (!Num(out var y)) return false;
            _cur = new Point(_cur.X, rel ? _cur.Y + y : y);
            _c.LineTo(_cur, true, true);
            _lastCubicCtrl = null;
            return true;
        }

        private bool Cubic(bool rel)
        {
            if (!Num(out var x1) || !Num(out var y1) || !Num(out var x2) ||
                !Num(out var y2) || !Num(out var x) || !Num(out var y)) return false;
            var c1 = Pt(rel, x1, y1);
            var c2 = Pt(rel, x2, y2);
            var e = Pt(rel, x, y);
            _c.BezierTo(c1, c2, e, true, true);
            _cur = e; _lastCubicCtrl = c2; _lastQuadCtrl = null;
            return true;
        }

        private bool CubicSmooth(bool rel)
        {
            if (!Num(out var x2) || !Num(out var y2) || !Num(out var x) || !Num(out var y)) return false;
            var c1 = _lastCubicCtrl.HasValue
                ? new Point(2 * _cur.X - _lastCubicCtrl.Value.X, 2 * _cur.Y - _lastCubicCtrl.Value.Y)
                : _cur;
            var c2 = Pt(rel, x2, y2);
            var e = Pt(rel, x, y);
            _c.BezierTo(c1, c2, e, true, true);
            _cur = e; _lastCubicCtrl = c2; _lastQuadCtrl = null;
            return true;
        }

        private bool Quad(bool rel)
        {
            if (!Num(out var x1) || !Num(out var y1) || !Num(out var x) || !Num(out var y)) return false;
            var q = Pt(rel, x1, y1);
            var e = Pt(rel, x, y);
            // WPF 无 QuadraticTo：二次贝塞尔等价转三次（升阶）
            _c.BezierTo(QuadToCubic1(q), QuadToCubic2(q, e), e, true, true);
            _cur = e; _lastQuadCtrl = q; _lastCubicCtrl = null;
            return true;
        }

        private Point QuadToCubic1(Point q)
            => new Point(_cur.X + 2.0 / 3.0 * (q.X - _cur.X), _cur.Y + 2.0 / 3.0 * (q.Y - _cur.Y));

        private Point QuadToCubic2(Point q, Point e)
            => new Point(e.X + 2.0 / 3.0 * (q.X - e.X), e.Y + 2.0 / 3.0 * (q.Y - e.Y));

        private bool QuadSmooth(bool rel)
        {
            if (!Num(out var x) || !Num(out var y)) return false;
            var q = _lastQuadCtrl.HasValue
                ? new Point(2 * _cur.X - _lastQuadCtrl.Value.X, 2 * _cur.Y - _lastQuadCtrl.Value.Y)
                : _cur;
            var e = Pt(rel, x, y);
            // WPF 无 QuadraticTo：二次贝塞尔等价转三次（升阶）
            _c.BezierTo(QuadToCubic1(q), QuadToCubic2(q, e), e, true, true);
            _cur = e; _lastQuadCtrl = q; _lastCubicCtrl = null;
            return true;
        }

        private bool Arc(bool rel)
        {
            if (!Num(out var rx) || !Num(out var ry) || !Num(out var rot)) return false;
            if (!Flag(out var largeArc)) return false;
            if (!Flag(out var sweep)) return false;
            if (!Num(out var x) || !Num(out var y)) return false;
            var e = Pt(rel, x, y);
            EmitArc(rx, ry, rot, largeArc, sweep, _cur, e);
            _cur = e;
            _lastCubicCtrl = _lastQuadCtrl = null;
            return true;
        }

        /// <summary>SVG 端点参数化 → 中心参数化 → 分段三次贝塞尔</summary>
        private void EmitArc(double rx, double ry, double rotDeg, bool largeArc, bool sweep,
            Point from, Point to)
        {
            if (from == to) return;
            rx = Math.Abs(rx);
            ry = Math.Abs(ry);
            if (rx < 1e-9 || ry < 1e-9) { _c.LineTo(to, true, true); return; }

            double phi = rotDeg * Math.PI / 180.0;
            double cosP = Math.Cos(phi), sinP = Math.Sin(phi);

            double dx2 = (from.X - to.X) / 2.0, dy2 = (from.Y - to.Y) / 2.0;
            double x1p = cosP * dx2 + sinP * dy2;
            double y1p = -sinP * dx2 + cosP * dy2;

            double lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
            if (lam > 1) { double s = Math.Sqrt(lam); rx *= s; ry *= s; }

            double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
            double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
            double co = den < 1e-12 ? 0 : (largeArc != sweep ? 1 : -1) * Math.Sqrt(Math.Max(0, num / den));

            double cxp = co * rx * y1p / ry;
            double cyp = -co * ry * x1p / rx;
            double cx = cosP * cxp - sinP * cyp + (from.X + to.X) / 2.0;
            double cy = sinP * cxp + cosP * cyp + (from.Y + to.Y) / 2.0;

            double ux = (x1p - cxp) / rx, uy = (y1p - cyp) / ry;
            double vx = (-x1p - cxp) / rx, vy = (-y1p - cyp) / ry;

            double ang0 = Math.Atan2(uy, ux);
            double angD = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            if (!sweep && angD > 0) angD -= 2 * Math.PI;
            else if (sweep && angD < 0) angD += 2 * Math.PI;

            int segs = (int)Math.Ceiling(Math.Abs(angD) / (Math.PI / 2.0));
            if (segs < 1) segs = 1;
            double delta = angD / segs;
            double t = 4.0 / 3.0 * Math.Tan(delta / 4.0);

            double ang = ang0;
            for (int k = 0; k < segs; k++)
            {
                double a0 = ang, a1 = ang + delta;
                var p0 = EllipsePoint(cx, cy, rx, ry, cosP, sinP, a0);
                var p1 = EllipsePoint(cx, cy, rx, ry, cosP, sinP, a1);
                var d0 = EllipseDeriv(rx, ry, cosP, sinP, a0);
                var d1 = EllipseDeriv(rx, ry, cosP, sinP, a1);
                _c.BezierTo(new Point(p0.X + t * d0.X, p0.Y + t * d0.Y),
                    new Point(p1.X - t * d1.X, p1.Y - t * d1.Y),
                    p1, true, true);
                ang = a1;
            }
        }

        private static Point EllipsePoint(double cx, double cy, double rx, double ry,
            double cosP, double sinP, double ang)
        {
            double x = rx * Math.Cos(ang), y = ry * Math.Sin(ang);
            return new Point(cx + cosP * x - sinP * y, cy + sinP * x + cosP * y);
        }

        private static Point EllipseDeriv(double rx, double ry, double cosP, double sinP, double ang)
        {
            double x = -rx * Math.Sin(ang), y = ry * Math.Cos(ang);
            return new Point(cosP * x - sinP * y, sinP * x + cosP * y);
        }
    }
}
