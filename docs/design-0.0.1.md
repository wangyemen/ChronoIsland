# ChronoIsland（时序岛） v0.0.1 开发版 — 设计文档

> Windows 桌面「灵动岛」。C# / WPF / .NET 10 / Windows 10 19041+ · Windows 11。
> 参考项目：[FluidBar](https://github.com/Doulor/FluidBar)（EventBus + IslandEvent + 弹簧物理 + 玻璃拟态）。
> 状态与动效可视化原型：<docs/样式.html>（浏览器直接打开）。

---

## 0. 核心产品定义（0.0.1 冻结）

**灵动岛不是常驻控件，而是"事件驱动的浮层"。**

```
默认：屏幕上什么都没有（栈空 → 窗口隐藏 → 零开销）
  │
  ├─ 来通知了        → 岛从顶部"滴落"生成 → 显示简略信息
  ├─ 开始播放媒体    → 岛生成 → 显示封面 + 歌名 + 波形
  ├─ 到整点/解锁     → 时间日期岛临时浮现
  │
  └─ 看完/已读/忽略/播放结束 → 岛以"水珠"收起 → 回到什么都没有
```

### 0.1 三条铁律
1. **无事件即无岛。** 栈空时窗口 `Visibility=Collapsed`，停掉所有 ticker / 定时器，CPU ≈ 0。
2. **先简略，后详情。** 任何岛首帧都是胶囊简略态；鼠标悬浮 → 弹簧展开详情卡；点击 → 打开来源 / 执行动作。
3. **已读即消失。** 通知被读（展开停留 ≥ 判定阈值 / 点击打开 / 点击忽略）后立刻走收起动效，不残留。

### 0.2 本版本五个功能
| 模块 | 触发 | 简略态（胶囊） | 详情态（悬浮展开） | 消失条件 |
| --- | --- | --- | --- | --- |
| Windows 通知 | 系统通知到达 | `[应用图标] 标题 — 正文一行` | 应用名·时间 / 标题 / 正文(≤4行) / [打开][忽略] | 已读后自动、点击忽略、超时 5 s |
| 时间日期 | 跨小时·跨天·锁屏唤醒·手动唤出 | `15:47 · 10月14日 周二` | 大时钟 + 迷你月历（今日高亮） | 3 s 自动 |
| 使用时长 | 整点播报 / 连续 50 min 提醒 / 手动唤出 | `⏱ 今日已用 6h 12m` | Top5 应用时长条 + 总计 | 4 s 自动 |
| 媒体播放 | GSMTC 会话进入 Playing | `[封面] 歌名 · 艺术家` + 波形 | 大封面 / 进度条(可拖) / ⏮⏯⏭ | 暂停 30 s 后、关闭会话、手动忽略 |
| **系统状态** | 键位状态翻转（CapsLock / NumLock / ScrollLock）；锁屏/休眠后解锁进入桌面；摄像头被启用/释放 | `[键帽 glyph] 键名 — 开/关`（开=实心高亮，关=虚线 32%） | 键位矩阵（开=绿点/关=灰点）或解锁卡（离开时长 + 未读数） | 4 s 自动；**值级忽略**（忽略的是当前值，不是信号本身） |

### 0.3 明确不做（0.1.0 及以后）
设置面板 UI、插件系统、剪贴板、音量/亮度/电池/输入法/USB/蓝牙监控、歌词、Agent 事件、多显示器独立岛。
> 注：锁定键（CapsLock / NumLock / ScrollLock）监控已移入 **0.0.1 系统状态模块**（§5.5），不做的是"输入法切换"与"音量/亮度"。
架构已按 `IEventSource` + `EventBus` 解耦，届时纯增量接入。

### 0.4 术语
| 术语 | 含义 |
| --- | --- |
| 岛 / Island | 单条胶囊浮层 |
| 栈 / Stack | 多条岛的纵向列表（栈顶=最新，在最上） |
| 母形态 | 所有状态共用的圆角矩形几何，仅宽高变化，圆角恒 = 高/2 |
| 简略态 / Collapsed | 胶囊形态（默认呈现） |
| 详情态 / Expanded | 悬浮/点击后展开的卡片 |
| 水珠 / Droplet | 生成与收起时的液滴形变阶段 |
| 会话 / Session | 一条岛从生成到消失的完整生命周期 |

---

## 1. 视觉系统

### 1.1 几何基线（8pt 栅格，DPI 感知取整）
| 项 | 值 | 说明 |
| --- | --- | --- |
| 岛高（胶囊） | `36 px` | 对齐 Dynamic Island 观感 |
| 岛宽范围 | `36 – 420 px` | 36 时为正圆（r=18），是"缩成点"的终点几何 |
| 圆角 | `Height / 2` | 恒等于半高 → 永远是胶囊 |
| 位置 | 屏幕顶部居中，`Top = 8 px`（可配 4–16） |
| 堆叠间距 | `8 px` | 最多 3 条，第 4 条起折叠为 `+N` 计数条（高 20 px） |
| 岛内边距 | 左右 `12 px`，图标↔文字 `8 px` | |
| 详情卡 | 宽 `420 px`，高 `96–260 px`，圆角 `28 px` | 不得超出屏幕底部 |
| 悬浮热区 | 岛外扩 `8 px`（防抖动误触退出） | 指针离开后 `200 ms` 宽限再收起 |

### 1.2 配色（跟随 Windows 主题）
| Token | 深色 | 浅色 | 用途 |
| --- | --- | --- | --- |
| `Surface` | `#0B0B0F` @78% | `#FFFFFF` @72% | 岛底（玻璃） |
| `SurfaceElevated` | `#16161D` @92% | `#FFFFFF` @92% | 详情卡 |
| `Stroke` | `#FFF` @12% | `#000` @8% | 1px 内描边 |
| `Separator` | `#FFF` @8% | `#000` @6% | 分隔线 |
| `TextPrimary` | `#F5F6F8` | `#1A1B1F` | 标题 |
| `TextSecondary` | `#A8ADB7` | `#5B6070` | 副文本 |
| `Accent` | `#5E9BFF` | `#2F6BFF` | 进度/按钮 |
| `Accent2` | `#B27BFF` | `#8B4DFF` | 渐变端 |
| `Success` | `#3DDC97` | `#12A87A` | 时长/完成 |
| `Warning` | `#FFC75A` | `#D99000` | 提醒 |
| `Danger` | `#FF6B6B` | `#D64545` | 忽略/错误 |
| `Shadow` | `#000` @45% blur24 dy6 | `#000` @18% | 投影 |

- 玻璃回退链：`Acrylic` → `Mica` → 纯色 + `Blur(24px)` 位图 + 3% 噪点。禁止硬依赖。
- 渐变只用于**进度条 / 图标光晕 / 详情卡边缘光**；岛底保持中性，避免堆叠时花掉。
- 图标：`Segoe Fluent Icons`（系统字体，零资源）；应用图标 `SHGetFileInfo` 提取 + LRU 128 缓存。

### 1.3 字体
| 角色 | 字体 | 字号 / 字重 |
| --- | --- | --- |
| 岛标题 | Segoe UI Variable Display → Segoe UI | 13 / Semibold |
| 岛副文本 | 同上 | 11.5 / Regular |
| 卡标题 | 同上 | 15 / Semibold |
| 卡正文 | 同上 | 13 / Regular |
| 数值（时长/进度/时钟） | 同上 + `TabularFigures` | 11.5 / Medium |

### 1.4 缓动
| 场景 | 曲线 | 时长 |
| --- | --- | --- |
| 内容淡入 | `cubic-bezier(.22,1,.36,1)` | 160 ms |
| 内容淡出 | `cubic-bezier(.4,0,1,1)` | 120 ms |
| 图标交叉替换 | `cubic-bezier(.22,1,.36,1)` | 140 ms |
| 几何变化 | Spring（§3） | 物理收敛 |
| 波形柱 | Spring.Gentle | 90 ms 周期 |

---

## 2. 状态机

### 2.1 单岛生命周期
```
                    ┌────── 悬浮/点击 ──────┐
                    ↓                        │
 (栈空·无窗口) ──▶ Emerging ──▶ Collapsed ───┤
                   水珠展开      简略胶囊     │ 停留超时 / 离开
                                  │  ▲       │
                        已读·忽略·│  │       │
                        超时·源消失 │  │       │
                                  ↓  │       │
                             Collapsing ◀────┘
                          点 → 水珠 → 顶部消失
                                  ↓
                            (栈空·无窗口)
```
- `Emerging`：§4.2 水珠滴落 → 摊开为胶囊（简略态）。**首帧即简略态内容**。
- `Collapsed`：停驻态，显示简略信息。
- `Expanded`：悬浮/点击后的详情卡，与胶囊共享几何（宽可弹到 420，向下长高）。
- `Collapsing`：三段式（§4.1），**不可打断**（打断则先 snap 终点再重新生成）。

### 2.2 "已读"判定（通知）
| 判定 | 条件 |
| --- | --- |
| 显式已读 | 点击"打开"→ 启动来源应用 → 立即收起 |
| 显式忽略 | 点击忽略按钮 / 右滑（后续）→ 立即收起 |
| 隐式已读 | 详情态停留 ≥ `1.2 s` 且指针已进入卡片 → 离开后 `300 ms` 收起 |
| 掠过已读 | 仅悬浮胶囊 ≥ `2.5 s` 未展开 → 视为已读，离开后收起 |
| 超时 | 未交互 `5 s` → 自动收起（系统重要通知 10 s） |
| 悬停暂停 | 指针在热区内，TTL 计时暂停 |

### 2.3 全局状态
| 状态 | 触发 | 表现 |
| --- | --- | --- |
| `Active` | 栈非空 | 展示 |
| `Idle` | 栈空 | 窗口隐藏，ticker/定时器全停 |
| `HiddenByGame` | 全屏 / D3D 独占 / 游戏进程 / `Ctrl+Alt+Shift+H` | 全栈错开 60 ms 收起后隐藏 |
| `Paused` | 锁屏 / UAC 安全桌面 | 直接隐藏，无动效 |

- 隐藏期到达的通知进 `PendingQueue`（上限 50，同 key 合并计 `×N`），恢复后按优先级回放（≤3 条，间隔 120 ms）。
- 优先级：媒体(手动) 5 > 通知 4 > 时长提醒 3 > 媒体(自动) 2 > 时钟 1。

### 2.4 集群式堆叠（Cluster Stack）— v0.0.1 采用
> **替代原纵向堆叠。** 纵向堆叠在 3 条以上会侵占屏幕顶部大片区域、遮挡标题栏、视觉重心不断下移。

**核心形态：中央 1 个胶囊 + 左右对称的球体。**

```
         ○    ○    ○        [ ====== 当前消息胶囊 ====== ]        ○    ○    ○
       较早 ←───────────────  屏幕中轴  ───────────────→ 较新
              ↑ 点击任意球体 → 它变胶囊（显示它的内容）
                               原胶囊变球体 → 退到侧列（互换角色）
```

| 规则 | 说明 |
| --- | --- |
| 只有一条是胶囊 | 顶部中央永远只有一个胶囊，展示**当前**消息 |
| 其余全是球体 | 未查看的较旧消息收为 `36px` 正圆，横向排在胶囊左右，不再向下堆 |
| 点击即互换 | 点击球体 → 它展开为胶囊并显示内容，原胶囊收成球体退到侧列 |
| 侧列只占一行 | 球体紧贴主岛横向排布，间距 `8px`（球心距 `44px`） |
| 最多 5 个 | 中心 1 个胶囊 + 左右各 2 个球体 = **5 个可见**；再多的消息抽象为 `+N` 指示（虚线框） |
| 交替填充 | 第 1、3、5 条往左，第 2、4、6 条往右，保持视觉对称 |
| 球体内容 | **仅显示来源应用图标**（18px 居中）+ 未读蓝点，不承载任何文字；悬停浮出摘要，点击才展开全文 |
| 已读即消失 | 当前胶囊被读/忽略 → 水珠收起 → 最邻近球体自动补位成胶囊 |
| 新消息滴落 | 从顶部水珠落到中心成为新胶囊，原胶囊"退位"到侧列成球 |
| 键盘可达 | `←/→` 切换当前，`Enter` 展开，`Esc` 收起 |

**几何**
- 中心胶囊宽：`内容宽`（36–420），高 `36`
- 球体：`36×36`，圆角 `18`（正圆）
- 第 k 层球体偏移：`±(胶囊宽/2 + 8 + 18) ± (k-1)·(36+8)`，k = 1..2（左右各 2）
- 第 3 层及以外：标记 `off` → 淡出并计入 `+N`
- 超出屏幕边界（距边 < 8px）的球体隐藏并计入 `+N`

**动效**
| 场景 | 弹簧 | 说明 |
| --- | --- | --- |
| 球 → 胶囊 | `Snappy` (300,26) | 宽度 36 → 目标宽，内容 160ms 淡入 |
| 胶囊 → 球 | `Push` (200,24) | 内容淡出 → 宽度收到 36 → 横向移到侧列空位 |
| 侧列补位 | `Push` (200,24) | 球体横向平移，几乎无过冲 |
| 展开详情卡 | `Float` (140,20) | 从胶囊向下摊开到 420×176 |
| 新消息生成 | `Gooey` → `Snappy` | 水珠滴落（§4.2） |
| 已读/忽略 | `Snappy`→`Gooey`→`Dismiss` | 水珠收起（§4.1） |

**为什么球体而非缩略胶囊**：球体只占 36px，一行可容纳 5 条（含中心）；
若用缩略胶囊（约 120px）一行只能放 3 条。球体 + 悬停摘要是在有限宽度内承载更多信息的最优解。

**交互原型**：<体验.html>（可点击操作，含连发、切换、展开、已读、游戏隐藏）

---

## 3. 弹簧动画系统（自研）

### 3.1 模型
阻尼谐振子，半隐式欧拉，固定步长 `1/120 s`（帧率无关，掉帧不漂）：
```
a = -k·(x - target) - c·v          // k=stiffness, c=damping
v += a·dt ;  x += v·dt
收敛：|x-target| < 0.05 && |v| < 0.05 → snap 并停止
```
- `CompositionTarget.Rendering` 驱动**全局单 ticker**（不是每属性一个 DispatcherTimer）。
- 只动画 `Width/Height/Top/Opacity` 与 `ScaleTransform`；岛用 `Canvas` 绝对定位，避免 StackPanel 重排抖动。
- 动画中 `IsHitTestVisible=false`，稳定后恢复（防抖动期误点）。

### 3.2 预设（冻结值）
| 预设 | k | c | m | 用途 |
| --- | --- | --- | --- | --- |
| `Gentle` | 170 | 22 | 1 | 宽度变化、内容替换 |
| `Snappy` | 300 | 26 | 1 | 折叠成点（阶段 A） |
| `Gooey` | 220 | 18 | 1 | 水珠拉伸/吸附（阶段 B） |
| `Float` | 140 | 20 | 1 | 详情卡展开 |
| `Push` | 200 | 24 | 1 | 堆叠推挤/补位 |
| `Dismiss` | 260 | 30 | 1 | 手动忽略/滑出 |

### 3.3 可访问性
读 `SystemParameters.ClientAreaAnimation` / 减少动效：关闭时 Spring 退化为 200 ms 线性淡入淡出，几何瞬变。

---

## 4. 核心动效：水珠生成 / 收起

### 4.1 收起（Collapse）三段式 ≈ 520–620 ms
**A. 缩成点 · Pinch（0–~180 ms）**
- 宽 `W → 36`（Snappy），高恒 36 → 圆角 18 → 视觉成正圆。
- 内容在宽度 < `内容宽+16` 时 `Opacity → 0`（120 ms，先文字后图标）+`ScaleX .86`。
- 圆内高光点 `0 → 1`（液滴核心）。阴影 `blur24→10`，`dy6→2`。

**B. 水珠 · Droplet（~140–~420 ms）**
- 主圆 `ScaleY 1→.82`、`ScaleX 1→1.08`（表面张力：先压扁再拉长）。
- 顶部生成**颈缩（neck）**：主圆顶边 → 屏幕顶边的粘连线，`Path` + 二次贝塞尔，宽度 `18→2 px`，每帧重算控制点。
- 位移 `Top: 8 → -6`（Gooey），被顶边吸附上提。
- `Opacity 1→.85`、`Blur 0→2px`（液体离场的通透感）。

**C. 消散 · Vanish（~380–~620 ms）**
- 颈缩 → 0；`ScaleY .82→.35`、`ScaleX 1.08→.6`（被吸入）。
- `Opacity→0`、`Blur→6px`、阴影 →0。
- 结束 → `Visibility=Collapsed`，移除并触发下方补位。栈空 → `Idle`，停一切。

### 4.2 生成（Emerge）＝ 收起的严格逆序
`Vanish⁻¹ → Droplet⁻¹ → Pinch⁻¹`：顶部下方 `-6` 处以水珠形态滴落 → 拉伸成圆 → 摊开为胶囊 → 内容淡入。
- 参数与收起完全对称（同 k/c，仅方向反演），保证往返一致。
- 允许 Gooey 轻微过冲（"啪"的落地感），但**宽度过冲 ≤ 6 px**，避免文字抖。

### 4.3 WPF 实现要点
| 手法 | 说明 |
| --- | --- |
| Gooey | `Border` + `Blur(6)` + `ColorMatrix` 提对比（经典 goo）；0.0.1 用简化版，性能不足降级为纯形变 |
| 颈缩线 | `Path` + `QuadraticBezierSegment`，每帧更新控制点与 `StrokeThickness` |
| 形变锚点 | `RenderTransformOrigin=(0.5,1)`（底部锚定，向上吸附） |
| 驱动 | 全局 `CompositionTarget.Rendering` → `SpringEngine.Tick` → 写 DP；**不用 Storyboard**（无法中途改 target、难物理化） |
| 性能 | `AllowsTransparency=True` + `WindowStyle=None` + `Topmost=True`；形变期 `CacheMode=BitmapCache` |

---

## 5. 功能模块

### 5.1 Windows 通知
- **数据源**：`UserNotificationListener`（`Windows.UI.Notifications.Management`）。无权限时回退：`WinEventHook` + 托盘气球窗口嗅探（`#32768` / `NotifyIconOverflowWindow`）。
- **接管原生弹窗**：岛活跃期间抑制 Windows 通知框——
  1. 首选：临时写 `HKCU\...\Notifications\Settings\NOC_GLOBAL_SETTING_TOASTS_ENABLED`（**写入前备份原值，退出/异常必须还原**，提供 `--restore-notifications`）；
  2. 兜底：监听到的通知入队即视为已接管，不做二次弹窗。
- **合并**：`key = AppUserModelId + Title`，120 s 内合并为 `×N`。
- **展示**：胶囊 `[图标] 标题 — 正文一行`；详情卡 `[图标][应用名·时间] / 标题 / 正文≤4行 / [打开][忽略]`。
- **行为**：点击胶囊 → 展开详情；"打开" → 启动源应用 + 收起；"忽略" → Dismiss 收起；悬停暂停 TTL。
- **留存**：默认 5 s（重要通知 10 s）；栈内同时最多 3 条通知。

### 5.2 时间日期
- **默认不常驻**，只在：跨小时、跨天、锁屏唤醒、手动唤出时浮现 3 s。
- 秒更新用**对齐下一秒**的 `DispatcherTimer`（渲染后重算间隔，防累积漂移）。
- 详情卡：大时钟 + 当月迷你月历（本地化周首日），今日高亮 Accent。
- 监听 `WM_TIMECHANGE` / `WM_SETTINGCHANGE` 即时刷新格式与主题。

### 5.3 使用时长统计
- **采集**：`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` out-of-context。
- **聚合**：同进程连续前台 ≥ `3 s` 才记；间隔 < `30 s` 合并（防切窗抖动）；`GetLastInputInfo` 空闲 > `5 min` 暂停计时。
- **忽略**：`ApplicationFrameHost`、`ShellExperienceHost`、`TextInputHost`、`dwm`、`SearchHost`、自身；UWP 走 `GetPackageFullName` 解析显示名。
- **存储**：`%LOCALAPPDATA%\ChronoIsland\usage-YYYY-MM.jsonl`（追加写，30 s / 20 条 flush，`lock` 保护，异常不上抛）。
- **展示**：胶囊 `⏱ 今日 6h 12m`；详情卡 Today Top5 横条（名称/时长/占比）+ 总计。
- **提醒**：连续 50 min 弹一次"休息一下"（3 s 自动收起，同应用 60 min 内至多一次）。
- **隐私**：仅本地；窗口标题默认不采集（`Usage.CaptureTitle=false`）。

### 5.4 媒体显示与控制
- **数据源**：`GlobalSystemMediaTransportControlsSessionManager`。选源策略：① 用户 30 min 内手动操作过的会话 ② `Playing` ③ 最近更新 ④ 浏览器会话降级一档。
- **胶囊**：`[封面24 圆角6] 歌名 · 艺术家` + `▶/⏸` 或 4 条波形柱（Playing 时 90 ms 周期随机高度 Spring；Paused 降到 25% 并停摆）。
- **详情卡**：封面 72 圆角 12（无封面用 Accent→Accent2 渐变 + 音符）、歌名/艺术家/专辑、可拖进度条、`⏮ ⏯ ⏭`、来源应用名与图标。
- **控制**：`TryPlayAsync` 等全包 try/catch + 200 ms 单飞去重。
- **省电**：仅 Playing 时 1 s 刷新进度；Paused/Stopped/Closed 停刷新，30 s 后自动收起。
- 旧系统无 GSMTC → 模块自动禁用并记日志，不影响其他模块。

### 5.5 系统状态（键位 + 会话 + 摄像头）— v0.0.1 新增
**定位**：和通知 / 媒体同级的事件源，`Kind = "sys"` / `"unlock"`。核心解决"我刚才到底按没按大小写"这类反复确认。

**监控信号（4 种）**
| 信号 | 触发 | 简略态 | 详情态 |
| --- | --- | --- | --- |
| CapsLock | `GetKeyState(VK_CAPITAL)` 翻转 | `⇪ 大写锁定 — 开/关` | 键位矩阵（3 键） |
| NumLock | `GetKeyState(VK_NUMPAD)` 翻转 | `◫ 数字键盘 — 开/关` | 键位矩阵 |
| ScrollLock | `GetKeyState(VK_SCROLL)` 翻转 | `⎋ 滚动锁定 — 开/关` | 键位矩阵 |
| 解锁 | 会话 `SessionUnlock` / `SUS_RESUMED` | `⛶ 已解锁 · 进入桌面 HH:mm` | 欢迎卡（离开时长 + 未读数） |

**取值方法**
- 键位：`GetKeyState(0x14/0x90/0x91)` 的 bit 0；**不装全局键盘钩子**，仅监听这三个键——用 `SetWindowsHookEx(WH_KEYBOARD_LL)` 过滤这三键，或 250 ms 轮询 `GetKeyState`（开销可忽略）。翻转检测：与前次值异或。
- 解锁：`WTSRegisterSessionNotification` 或 `PowerRegisterSCEventHandler(SGUID_BATTERYSTATUS_CHANGED / SUSPEND/RESUME)` + `ProcessSwitch`；离开时长 = `现在 − 锁屏时刻`。

**值级忽略（关键语义）**
- 忽略粒度是 **`信号 + 值`**，不是 `信号`。忽略"CapsLock=开"后，岛收起并记录 `ignored[(capslock, true)] = 时间戳`。
- 再翻转时：**新值不在忽略表 → 正常弹出**。这就是"忽略后下次调节再弹出"。
- 忽略表条目 60 s 后过期自动清除（避免"忽略开"后永远不弹"开"——用户切到关再切回开时通常想被提醒）。
- 显式"忽略整个信号"（设置里关某键监控）→ 信号级忽略，该键完全不弹。

**交互**
- 胶囊 4 s 自动收起；展开详情后停留暂停 TTL（与其他模块一致）。
- 详情卡按钮：`[稍后提醒]`（本条不弹，下个值仍弹）、`[忽略]`（值级忽略）、键盘矩阵卡带 `[打开键盘筛选器]`（`ms-settings:keyboard`）。
- 解锁卡按钮：`[快速聚焦上次应用]`（`SetForegroundWindow` 到锁屏前焦点窗口）、`[忽略]`。

**与其他模块的关系**
- 同栈堆叠：`kind=sys` / `unlock` 占用球体位时，球体显示键帽 glyph（22px 圆角方块）。
- 解锁可与时钟合并：`SessionResume` 触发时若 3 s 内恰逢跨小时时钟岛，两者合并为一条（时钟 + "已解锁" 副标签），避免双岛。

**设置**（§9 JSON 新增 `sys` 节）
```jsonc
"sys": {
  "enabled": true,
  "capslock": true, "numlock": true, "scrolllock": false,   // scrolllock 极少用，默认关
  "unlock": true,
  "ttlSeconds": 4,
  "ignoreValueExpirySeconds": 60,
  "autoHideNumlockNoNumpad": true   // 无小键盘的笔记本自动隐藏 NumLock 信号
}
```

**CPU / 静默**
- 键位监听：LL hook 仅回调 3 键，其余 `CallNextHookEx` 直传，不阻塞输入线程。
- 轮询兜底 250 ms 仅在 hook 未装成功时启用。
- 无事件时零开销（事件驱动）；锁屏期（`Paused` 态）hook 自动挂起，不计时。

### 5.5b 详情卡展开方式（悬浮自动 / 点击）— v0.0.1 新增

两种方式都会生成**同一张**详情卡，卡片右上角都带**关闭按钮**，行为完全一致。

| 方式 | 触发 | 说明 |
| --- | --- | --- |
| **悬浮自动展开** | 鼠标停在当前胶囊上满 `Island.HoverExpandMs`（默认 700ms） | 无需点击；只对**中央胶囊**生效，球体不触发 |
| **点击展开** | 点击当前胶囊 | 再次点击同一张卡则收起（等价于点关闭） |
| 切换当前岛 | 点击任意球体 | 球体升为胶囊、原胶囊降为球体，卡片自动收起 |

**关闭按钮的行为**

| 场景 | 行为 |
| --- | --- |
| 悬浮展开后点 ✕ | 收起卡片，并**抑制本次悬停的再次自动展开**（`_hoverExpandSuppressed = true`） |
| 鼠标移开再回来 | 抑制解除，可以再次自动展开 |
| 切换到另一条岛 | 抑制自动解除（`_lastHoverId` 变化时重置） |
| 点击展开后点 ✕ | 收起卡片；再点胶囊可重新展开 |

**与"悬停穿透"的关系**：两个时间值独立，不要混淆。

| 设置项 | 默认 | 管什么 |
| --- | --- | --- |
| `Island.HoverActivateMs` | 400ms | 悬停多久后**才可交互**（此前保持穿透，防止鼠标划过误触到下层按钮） |
| `Island.HoverExpandMs` | 700ms | 悬停多久后**自动展开卡片**；设为 0 即关闭该功能，只保留点击展开 |

所有详情卡（通知 / 媒体 / 键位 / 摄像头 / 解锁）统一由 `BuildCard` 出口包一层 `WrapWithClose`，因此**新增卡片类型自动带关闭按钮**，无需各自实现。
### 5.6 摄像头占用提醒 — v0.0.1 新增
**为什么需要**：摄像头被远程会议 / 录屏软件打开时，用户往往毫无察觉。这是**隐私提醒**，优先级高于普通键位（`Priority + 1`）。

| 项 | 设计 |
| --- | --- |
| 触发 | 摄像头从"无人使用"变为"被独占"，或从"被独占"回到"空闲" |
| 简略态 | `摄像头已启用 · HD Webcam` / `摄像头已关闭 · 已不再录制` |
| 详情态 | 大图标 + 状态 + 设备名 + `[稍后提醒][忽略][相机权限]` |
| 球体 | 占用 = 绿色 camera 图标；空闲 = 灰色 camera-off 图标 |
| 停留 | 占用时至少 4 s（留久一点），关闭时 3 s（短暂确认） |
| 抢焦点 | 仅"被启用"时抢（`StealFocus = busy`）；关闭时不打断当前操作 |

**检测原理**（`Native/CameraEnumerator.cs` + `SysStateSource.PollCamera`）

Windows **没有公开的"摄像头是否正在使用"API**（硬件指示灯由驱动私有协议控制）。0.0.1 用**无副作用的只读共享探测**：

1. **枚举摄像头** —— 三级降级链，任何一级成功即停：
   1. `Windows.Devices.Enumeration`（WinRT，`DeviceInformationKind.VideoCapture`）
   2. **DirectShow**（`ICreateDevEnum` 枚举 `CLSID_DirectShowVideoCapture`）—— 标准公开 API
   3. **SetupAPI**（按 Camera 设备类枚举，取 `SPDRP_PHYSICAL_DEVICE_OBJECT_NAME`）
2. **占用判定** —— 对每个设备路径以 `dwDesiredAccess = 0`（仅元数据，不请求任何读写权限）`CreateFileW`：
   - 成功 → 无人独占 → **立即 `CloseHandle`**（不打断任何正在进行的采集）
   - `ERROR_SHARING_VIOLATION(32)` / `ERROR_ACCESS_DENIED(5)` → 有进程独占 → **使用中**
3. 轮询间隔 2 s；无摄像头设备时该功能静默（不弹任何岛）。

**值级忽略同样适用**：忽略"摄像头=开"60 s 后，若程序释放再重新打开摄像头，会再次提醒。

**降级说明**：.NET 5+ 移除了 WinRT 投影程序集，因此实际生效的通常是 DirectShow 或 SetupAPI 路径；三级降级全部失败时按"无摄像头"处理（不误报）。

### 5.6b 启动入场队列 — v0.0.1 新增

**问题**：程序启动瞬间，时钟、使用时长、媒体、键位等多个数据源会在同一帧内各自发出首条事件。
若一次性全部触发 `DropletAnimator` 生成，多条岛会同时"滴落"并互相挤掉中央位置，视觉上糊成一片，用户来不及看清任何一条。

**方案**：启动阶段引入**入场队列**，把到达的事件暂存，按 `Priority` 从高到低逐条入场。

| 项 | 设计 |
| --- | --- |
| 生效区间 | `StartupQueueActive = true`，从事件源接线开始，到队列放空为止 |
| 排序 | 每 900ms 取队内 `Priority` 最大者入场；同优先级保持先来后到 |
| 同 Id 去重 | 入队前先移除同 `Id` 的旧项 —— 媒体源每 250ms 发一次刷新事件，不去重会把队列塞满 |
| 优先级 | 通知 3 > 使用时长 3 > 摄像头 3 > 键位 / 解锁 2 > 时钟 1 |
| 队列放空 | 停止计时器，`StartupQueueActive = false`，此后所有事件恢复实时插入 |
| 启动补报 | 时钟与使用时长在启动时各 `Poke()` 一次，保证开机就能看到"现在几点""今天用了多久" |
| 期间的新事件 | **照常入队**，不绕过 —— 启动这几秒内的真实事件（如你正好收到通知）不会被丢掉 |

**实测时序**（启动后 5 条，间隔 900ms）：

```
12:57:59  启动队列：+clock（1 条）
12:57:59  启动队列：+media:Chrome（2 条）
12:57:59  启动队列：+media:Spotify.exe（3 条）
12:57:59  启动队列：+sys（4 条）
12:57:59  启动队列：+usage（5 条）
12:58:00  启动入场：media:Chrome（剩 4 条）      <- 优先级最高
12:58:01  启动入场：media:Spotify.exe（剩 3 条）
12:58:02  启动入场：usage（剩 2 条）
12:58:03  启动入场：sys（剩 1 条）
12:58:04  启动入场：clock（剩 0 条）
12:58:05  启动入场队列已放完，恢复实时响应
```
### 5.7 矢量图标体系 — v0.0.1 新增

**使用的开源图标库：Lucide**

#### 开源许可与出处

| 项 | 内容 |
| --- | --- |
| 图标库 | **Lucide** |
| 官网 | https://lucide.dev |
| 仓库 | https://github.com/lucide-icons/lucide |
| **许可协议** | **ISC License** |
| 许可全文 | https://github.com/lucide-icons/lucide/blob/main/LICENSE |
| 商用 | **允许**（ISC 允许免费商用、修改、再分发，只需保留版权声明与许可声明） |
| 引入方式 | **下载 SVG 源文件**放入 <Assets/Icons/>，未引入任何 JS / 字体 / 运行时依赖 |
| 本项目改动 | 仅去掉 `class` 属性、把 `stroke-width` 由 2 微调为 1.75；**几何路径未做任何修改** |
| 署名 | 每个 SVG 文件首行保留 `<!-- name — Lucide xxx (ISC License, https://lucide.dev) -->` 注释 |

ISC 协议全文（保留在每个图标文件与本节中）：

```
ISC License

Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part of Feather (MIT). All other copyright (c) for Lucide are held by Lucide Contributors 2022.

Permission to use, copy, modify, and/or distribute this software for any purpose with or without fee is hereby granted, provided that the above copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

#### 交付与渲染

| 项 | 设计 |
| --- | --- |
| 交付形式 | `Assets/Icons/*.svg` 共 26 个，以 `EmbeddedResource`（逻辑名 `ChronoIsland.Assets.Icons.<name>.svg`）嵌入程序集 |
| 渲染器 | `Native/SvgIcons.cs` —— 自研 SVG 到 WPF `Geometry` 解析器，**零第三方图形库** |
| 图标风格 | stroke 风格（`fill=none` + `stroke=currentColor`），线宽随尺寸等比缩放 |
| 支持元素 | `path`（M / L / H / V / C / S / Q / T / A / Z 全命令）、`circle`、`ellipse`、`rect`、`line`、`polyline`、`polygon` |
| 填充 / 描边分离 | `polygon`、`rect` 走**填充层**；`path`、`circle`、`ellipse`、`line` 走**描边层**。两层叠在同一个 `Grid` 上（play / pause / prev / next 因此是实心图形，不会被画成空心轮廓） |
| 弧线转换 | SVG `A` 命令按"端点参数化 → 中心参数化"转换，每段不超过 90°，用三次贝塞尔逼近 |
| 二次曲线 | `Q` / `T` 升阶为等价三次贝塞尔（WPF `StreamGeometryContext` 无 `QuadraticTo`） |
| 闭合路径 | `Z` 通过预扫描写入 `PathFigure.IsClosed`（WPF 的 `LineTo` 无闭合重载） |
| 缩放方式 | **几何层** `ScaleTransform`（连同 `StrokeThickness` 一起缩放，线宽比例正确）+ `Path.Stretch=None`（不二次缩放、不裁切） |
| 坐标约定 | 全部保持 Lucide 原始 24×24 视口坐标，**不按各自 Bounds 平移**（否则填充层与描边层会错位） |
| 缓存 | `name -> (Stroke, Fill)` 字典，`Freeze()` 后跨线程共享，零 GC 压力 |
| 新增图标 | 把 Lucide 的 `<name>.svg` 放进 `Assets/Icons/` 即可，无需改任何代码 |

#### 图标清单（26 个）

`bell` `bell-off` `camera` `camera-off` `capslock` `clock` `close` `eye` `eye-off` `info`
`keyboard` `lock` `monitor` `music` `next` `numlock` `pause` `play` `power` `prev`
`scrolllock` `settings` `shield` `target` `timer` `unlock`
**替换清单**：原先全部使用 `Segoe MDL2 Assets` 字体字形（时钟 `\uE823`、计时 `\uE916`、媒体控制 `\uE892` / `\uE769` / `\uE768` / `\uE893`、解锁 `\u26B6`），现全部改为 Lucide 矢量 —— **不再依赖系统字体**，跨 Windows 版本渲染一致，且可换色、可换粗细。

---

## 6. 后台静默
| 维度 | 做法 |
| --- | --- |
| 任务栏 / Alt+Tab | `ShowInTaskbar=False` + `WS_EX_TOOLWINDOW` |
| 焦点 | `ShowActivated=False`；`Focusable=False`；置顶不抢焦点 |
| 穿透 | 非交互区 `WS_EX_TRANSPARENT`；命中后临时关闭，300 ms 无移动再开 |
| CPU | 栈空停 ticker；通知/前台 hook 为事件驱动；时钟对齐秒；媒体 Playing 才轮询 |
| 内存 | 图标 LRU 128（≤2 MB）；日志滚动 1 MB × 2 |
| DPI | PerMonitorV2 manifest；`WM_DISPLAYCHANGE` 重算居中 |
| 异常 | 全局 `DispatcherUnhandledException` 写日志并**保持运行**（单模块崩溃不拖垮全局） |

---

## 7. 架构

```
ChronoIsland.App
├── Core/
│   ├── EventBus.cs          // Channel<IslandEvent> + 优先级队列，线程安全
│   ├── IslandEvent.cs       // record
│   ├── IslandSession.cs     // 单岛生命周期状态机（§2.1）
│   └── IslandStack.cs       // 入场/补位/替换/上限/+N
├── Animation/
│   ├── SpringEngine.cs      // 全局 ticker + 半隐式欧拉 1/120
│   ├── SpringPresets.cs     // §3.2
│   └── DropletShape.cs      // 水珠 Path（颈缩贝塞尔每帧重算）
├── UI/
│   ├── IslandWindow.xaml    // 透明·置顶·无边框·PerMonitorV2
│   ├── IslandView.xaml      // 母形态 + VisualState
│   ├── Cards/               // NotificationCard / MediaCard / UsageCard / ClockCard
│   ├── GooeyEffect.cs       // Blur + ColorMatrix
│   └── Theme.cs
├── Sources/
│   ├── NotificationSource.cs
│   ├── ClockSource.cs
│   ├── UsageTracker.cs
│   ├── MediaSource.cs
│   └── SysStateSource.cs  // §5.5 键位(CapsLock/NumLock/ScrollLock) + 会话解锁 + §5.6 摄像头占用
├── Native/
│   ├── Win32.cs · IconExtractor.cs · FullscreenDetector.cs
└── Settings/Settings.cs     // 0.0.1 仅 JSON（无面板）
```

### 7.1 关键契约
```csharp
public interface IEventSource : IDisposable {
    string Key { get; }        // "notification" | "clock" | "usage" | "media"
    int    Priority { get; }
    void   Start(EventBus bus);
}

public sealed record IslandEvent(
    string Id, string SourceKey, string Kind, int Priority,
    string Title, string? Subtitle, object? Icon, object? Payload,
    IslandPolicy Policy, DateTimeOffset CreatedAt);

public sealed record IslandPolicy(
    TimeSpan? Ttl,          // null = 需显式关闭（如媒体播放中）
    bool Coalesce,          // 同 SourceKey 合并
    bool Expandable,        // 可否悬浮展开
    bool HoverPauses,       // 悬停暂停 TTL
    bool QueueWhenHidden);  // 隐藏时入队（通知 = true）
```

### 7.2 线程模型
- Source 在后台线程/回调产出 → EventBus 入队 → **单一 UI 线程**消费。
- 洪峰：同类 200 ms 内合并；全局限流 8 条/秒，超出延迟处理。
- Source 回调内禁止 UI 或耗时操作（图标提取、IO 一律 `Task.Run`）。

---

## 8. 游戏 / 全屏隐藏
| 手段 | 说明 |
| --- | --- |
| 前台全屏窗口 | 前台窗口矩形覆盖整屏且非 Shell 窗口 |
| D3D 独占 | `SHQueryUserNotificationState() == QUNS_BUSY / QUNS_RUNNING_D3D_FULL_SCREEN` |
| 进程表 | 内置 `Resources/games.json`，用户可扩展 |
| 手动 | `Ctrl+Alt+Shift+H`；按住 `Alt` 临时隐藏 |
| 轮询 | `WinEventHook` 驱动 + 2 s 兜底（QUNS 查询开销极低） |

- 触发 → 全栈**错开 60 ms** 依次收起（多米诺水珠，而非整体消失）。
- 恢复 → 同错开 60 ms 展开 + PendingQueue 回放。
- 游戏期间**不采集**使用时长（标记 `Scene=Game` 不计入），避免污染统计。

---

## 9. 设置（0.0.1 仅 JSON）
`%LOCALAPPDATA%\ChronoIsland\settings.json`，防抖 400 ms 保存：
```jsonc
{
  "island": { "top": 8, "maxWidth": 420, "scale": 1.0 },
  "theme":  { "mode": "System", "acrylic": true },
  "clock":  { "hour24": true, "showSeconds": false, "persistent": false },
  "notification": { "enabled": true, "ttlSeconds": 5, "suppressNative": true, "maxStacked": 3 },
  "usage":  { "enabled": true, "captureTitle": false, "idleSeconds": 300, "breakMinutes": 50 },
  "media":  { "enabled": true, "priority": "ManualFirst", "browserAsFallback": true },
  "game":   { "hideOnFullscreen": true, "extraProcesses": [] },
  "island": { "hoverActivateMs": 400, "hoverExpandMs": 700 },
  "sys":    { "enabled": true, "capslock": true, "numlock": true, "scrolllock": false, "unlock": true, "camera": true, "ttlSeconds": 4, "ignoreValueExpirySeconds": 60, "autoHideNumlockNoNumpad": true },
  "reduceMotion": "System"
}
```

---

## 10. 日志与诊断
- `%LOCALAPPDATA%\ChronoIsland\logs\app.log`，滚动 1 MB × 2；`CHRONO_LOG=Debug` 提级。
- 记录：模块启停、通知权限状态、媒体会话切换、全屏隐藏/恢复、Spring 收敛超时（>2 s 视为异常）、未处理异常。
- 0.0.1 提供托盘图标（右键：暂停/恢复、打开日志目录、退出）。

---

## 11. 里程碑（0.0.1 dev）
| # | 任务 | 验收 |
| --- | --- | --- |
| M0 | 交互原型 <docs/样式.html> | 全部状态与动效可视化确认 |
| M1 | 岛窗口 + SpringEngine + 母形态 | 空胶囊生成/收起 60 fps |
| M2 | 水珠动效（生成/收起） | 往返一致、无闪烁 |
| M3 | EventBus + 栈管理 | 3 条堆叠不闪跳 |
| M4 | 时间日期岛 | 秒对齐无漂移 |
| M5 | 通知接管 + 已读即消失 | 不重弹、可合并、可忽略 |
| M6 | 使用时长 | 跨天聚合正确 |
| M7 | 媒体显示与控制 | 多会话选择正确 |
| M8 | 游戏隐藏 + 后台静默 | 空闲 CPU ≈ 0 |
| M9 | 系统状态（键位 + 解锁 + 摄像头）+ Lucide 矢量图标 + 悬浮自动展开 + 启动入场队列 | CapsLock 翻转即弹；值级忽略后切回再弹；笔记本无小键盘自动隐藏 NumLock |

---

## 12. 风险与对策
| 风险 | 对策 |
| --- | --- |
| `UserNotificationListener` 权限被拒 | 回退托盘嗅探；提示授权，拒绝后仍工作（不抑制原生） |
| Gooey Effect GPU 开销 | 简化 goo；低性能设备降级为纯 Scale |
| 透明置顶窗口与 D3D 全屏冲突 | 隐藏期彻底隐藏窗口，不抢表面 |
| GSMTC 部分应用不可用 | 模块级 try/catch + 自动禁用 + 日志 |
| 前台 hook 高频 | 30 s 合并窗口 + 3 s 最小会话 |
| 抑制通知注册表误改 | 写入前备份，退出/异常均还原 + 命令行还原入口 |
| LL 键盘钩子阻塞输入 | 钩子内仅查 3 键 bit，<1 ms 返回；hook 挂起失败自动降级 250 ms 轮询 |
| 值级忽略误记（忽略"开"后永远不弹"开"） | 忽略表条目 60 s 过期清除；过期后再次触发正常弹出 |
| 摄像头探测打断正在进行的采集 | `CreateFileW` 用 `dwDesiredAccess = 0`（仅元数据），成功立即 `CloseHandle`，不申请任何读写权限 |
| 摄像头枚举在部分驱动上失败 | 三级降级链（WinRT → DirectShow → SetupAPI），全失败按"无摄像头"静默处理，不误报 |
| SVG 图标解析失败导致空白图标 | 解析异常返回 `null` → 回退渲染 `?` 占位；几何 `Freeze()` 后跨线程共享，零运行时风险 |
| 启动瞬间多条岛同时弹出糊成一片 | 启动阶段走入场队列：按 `Priority` 每 900ms 放一条，同 `Id` 去重，放空后自动恢复实时响应 |
| 点关闭后立刻又被自动展开 | `_hoverExpandSuppressed` 锁住本次悬停；鼠标移开或切换岛后自动解锁 |
| 发布后目标机未装 .NET 10 → 弹"安装 .NET" | csproj 固定 `<SelfContained>true</SelfContained>` + `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`，publish 把运行时打进目录，免安装分发 |

---

## 13. 与 FluidBar 的取舍
| FluidBar | ChronoIsland 0.0.1 | 原因 |
| --- | --- | --- |
| 10 监控器 + 4 插件 | 4 个模块 | 先做深，做出水珠质感差异 |
| 悬停卡片 | 弹簧展开详情卡 | 同理念，参数自定 |
| 多岛快照窗口 | 统一栈内展开 | 减少窗口数，降低闪跳 |
| 酷狗歌词 | 不做 | 网络依赖与版权 |
| 设置面板 | JSON 先行 | 0.1.0 再做，先冻结契约 |
| — | **新增**：点→水珠→顶部消失 | 核心视觉标识 |
| — | **新增**：无事件即无岛 / 已读即消失 | 事件驱动浮层定位 |

---

*文档版本：v0.0.1-dev · 更新：2026-10-14*
*状态可视化：<docs/样式.html>（含时间轴与逐帧播放）*
