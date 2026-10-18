# ChronoIsland 时序岛 · v0.0.1 dev

Windows 桌面灵动岛。默认**屏幕上什么都没有**——来通知、开始播放、跨整点时以水珠形态从顶部滴落生成，
悬浮展开详情，已读 / 忽略后缩成一点、以水珠吸附上升消失。

## 本目录

| 路径 | 说明 |
| --- | --- |
| <Sources/> <Core/> <UI/> <Native/> | 程序主体：信号源 / 内核 / 界面 / 原生互操作 |
| <assets/> | 图标与图片资源（程序图标、界面截图、图标概念稿） |
| <ChronoIsland.csproj> | 工程文件（net10.0-windows、x64、自包含） |
| <.tools/dotnet/> | 项目本地 .NET 10 SDK（10.0.402），不写入 C 盘 |

> 设计文档与交互原型位于**独立的设计站点**，不随本仓库分发。源码注释中会引用对应的
> 设计文档章节（如「设计文档 §3.2」），作为维护时的设计出处索引。

## 许可

本项目代码采用 **MIT License**，详见 [LICENSE](LICENSE)。

界面图标全部来自 [Lucide](https://lucide.dev)（**ISC License**），第三方许可声明见
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 图标

程序图标在 <assets/icon.ico>（7 个尺寸 16–256）。双尺寸源文件备份在 <assets/icon-concepts/>。
修改图标：替换 assets/icon.ico 后重新构建即可（exe 主图标 + 托盘 + 窗口标题栏都会更新）。

### 开源矢量图标：Lucide（ISC License）

界面里所有图标都来自 **[Lucide](https://lucide.dev)** —— 一个开源的 SVG 图标库。

| 项 | 内容 |
| --- | --- |
| 官网 | https://lucide.dev |
| 仓库 | https://github.com/lucide-icons/lucide |
| **许可** | **ISC License** —— 允许免费商用、修改、再分发，只需保留版权与许可声明 |
| 许可全文 | https://github.com/lucide-icons/lucide/blob/main/LICENSE |
| 引入方式 | 只下载 SVG 源文件，**不引入任何 JS / 字体 / 运行时依赖** |
| 本项目改动 | 仅去掉 `class` 属性、`stroke-width` 由 2 微调为 1.75；**几何路径原样保留** |

图标文件在 <Assets/Icons/>（50 个 `.svg`），每个文件首行都保留了出处注释：

```
<!-- play — Lucide play (ISC License, https://lucide.dev) -->
```

由 <Native/SvgIcons.cs> 运行时解析成 WPF 几何绘制 —— 这是自研的 SVG 渲染器，**不依赖任何第三方图形库**。
支持 `path` 全命令（含 `A` 弧线转贝塞尔）、`circle`、`ellipse`、`rect`、`line`、`polyline`、`polygon`。

这样做的直接好处：**不再依赖 `Segoe MDL2 Assets` 系统字体**，跨 Windows 版本渲染一致，且可换色、可换粗细。

**新增图标**：把 Lucide 的 `<name>.svg` 放进 <Assets/Icons/> 重新构建即可；若想在代码里按名字取用，
再往 `Native/SvgIcons.Names` 里登记一下（该数组用于核对与设置页枚举）。

图标清单（50 个）：

| 分组 | 图标 |
| --- | --- |
| 基础 | `bell` `bell-off` `camera` `camera-off` `capslock` `check` `clock` `close` `eye` `eye-off` `info` `keyboard` `lock` `monitor` `music` `next` `numlock` `pause` `play` `power` `prev` `scrolllock` `settings` `shield` `sparkles` `target` `timer` `unlock` |
| 麦克风 | `mic` `mic-off` |
| 电源 / 电池 | `battery` `battery-low` `battery-full` `battery-charging` `plug` |
| 网络 | `wifi` `wifi-off` `router` `signal-high` `signal-medium` `signal-low` `signal-zero` `gauge` `arrow-down` `arrow-up` `smartphone` |
| 飞行模式 | `plane` |
| 蓝牙 | `bluetooth` `headphones` |
| 深浅色 | `sun` `moon` `sun-moon`（一个太阳 + 一个月亮，深浅共用一个） |

### 自动更新备用下载源：jsDelivr（MIT / 开源 CDN）

自动更新流程先尝试 **官方 GitHub Releases 资产直链**（<version.txt> 第 2 行）；
失败（国内网络常连不上 `github.com`）时，<Core/Updater.cs> 会把它翻译成 **jsDelivr** 的
等价 `gh-releases` 直链再试一次，保证在国内网络下也能拉到同一份 zip。

| 项 | 内容 |
| --- | --- |
| 官网 | https://www.jsdelivr.com |
| 仓库 | https://github.com/jsdelivr/jsdelivr |
| **许可** | 内容按源文件的许可分发；jsDelivr 服务本身为开源 CDN |
| 用途 | 读 `version.txt`（gh 仓库文件）+ 下载 release 资产（gh-releases 资产直链） |

### 感谢与致谢

本项目的图标、歌词能力与更新回退链路都来自开源生态，感谢：

| 项目 | 用途 |
| --- | --- |
| [Lucide](https://lucide.dev) | 界面 50 个矢量图标（ISC） |
| [jsDelivr](https://www.jsdelivr.com) | 自动更新包备用下载源（国内网络回退） |
| [FluidBar](https://github.com/search?q=FluidBar&type=repositories) | EventBus 架构、媒体会话优先级、应用图标提取链路 |
| [WXRIW/Lyricify-Lyrics-Helper](https://github.com/WXRIW/Lyricify-Lyrics-Helper) | 歌词多源轮转、KRC / QRC 解密（通过 NuGet 包 `Lyricify.Lyrics.Helper` 引入） |

详细许可说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
## 交互说明

### 详情卡展开：悬浮自动 + 点击

两种方式生成的是**同一张卡**，右上角都有 **✕ 关闭按钮**：

| 方式 | 触发 | 说明 |
| --- | --- | --- |
| 悬浮自动展开 | 鼠标停在中央胶囊满 700ms | 无需点击 |
| 点击展开 | 点击当前胶囊 | 再点一次收起 |
| 切换当前岛 | 点击任意球体 | 球体升为胶囊，原胶囊降为球体 |

点 ✕ 关闭后，**本次悬停不会再次自动展开**；鼠标移开再回来即可恢复。

两个时间值互相独立，在「设置 → 悬停」里调整：

| 设置项 | 默认 | 管什么 |
| --- | --- | --- |
| `Island.HoverActivateMs` | 400ms | 悬停多久后**才可交互**（此前保持穿透） |
| `Island.HoverExpandMs` | 700ms | 悬停多久后**自动展开卡片**，设为 0 即关闭该功能 |

### 启动入场队列

程序启动瞬间多个数据源会同时发事件（时钟、时长、媒体、键位）。若一次性全部弹出会糊成一片，
因此启动阶段走**入场队列**：按优先级从高到低、每 900ms 放一条，同 `Id` 自动去重，队列放空后自动恢复实时响应。

实测时序见设计文档「入场队列」一节（§5.6b），完整推导见 `Core/PendingQueue.cs`。

## 运行（三种方式）

| 方式 | 操作 |
| --- | --- |
| **双击运行** | 双击 [启动.bat](<启动.bat>)（优先用 publish 版，否则走本地 SDK） |
| **自包含发布** | 双击 [发布.bat](<发布.bat>) → 生成 publish\ChronoIsland.exe，整个文件夹可拷走双击运行 |
| **命令行** | 见下方 |

### 自包含发布（无需安装 .NET）

`ChronoIsland.csproj` 已设 `<SelfContained>true</SelfContained>` + `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`，
因此 `dotnet publish`（或 [发布.bat](<发布.bat>)）会把 .NET 10 运行时一并打进 publish 目录——
**目标机器不需要安装任何 .NET**，整个 publish 文件夹拷走即可双击运行。

> 背景：之前 `SelfContained=false` 时 publish 出的是"框架依赖"exe，启动时会去系统里找
> .NET 10 Windows Desktop 运行时，找不到就弹"您必须安装或更新 .NET"。改为自包含后彻底绕开这个问题。
>
> 代价：publish 目录会大很多（约 209 MB、533 个文件）。分发 / 拷走时用整个文件夹，不要只拷 exe。

## 构建与运行

```bat
:: 在项目目录下（PowerShell）
# 在项目根目录执行（把 .tools 换成你自己的路径）
$env:DOTNET_ROOT='.\.tools\dotnet'
$env:NUGET_PACKAGES='.\.tools\nuget'
& "$env:DOTNET_ROOT\dotnet.exe" build -c Debug
& "$env:DOTNET_ROOT\dotnet.exe" run -- smoke      :: 冒烟：注入 6 条验证集群上限
& "$env:DOTNET_ROOT\dotnet.exe" run -- media      :: 诊断：打印所有 GSMTC 媒体会话

:: 常用：直接双击 启动.bat 运行；双击 发布.bat 生成自包含 publish\
```

SDK 与 NuGet 缓存都在项目内 `.tools`，不写入 C 盘。

## 当前进度

- [x] M0 交互原型（状态图库 + 交互演示，浏览器可视化验证）
- [x] M1 岛窗口 + SpringEngine + 母形态
- [x] M2 水珠生成 / 收起动效
- [x] M3 EventBus + 集群栈（胶囊 ⇄ 球体互换）
- [x] M4 时间日期
- [x] M5 Windows 通知接管
- [x] M6 使用时长统计
- [x] M7 媒体显示与控制
- [x] M8 游戏隐藏 + 后台静默
- [x] M9 系统状态（键位 / 解锁 / 摄像头）+ Lucide 矢量图标 + 悬浮自动展开 + 启动入场队列
- [x] M10 系统状态扩展六项（设计文档 §5.8–§5.13）：
  - [x] **电池 / 电源** — 插拔电源、充放电切换、低电量、充满（每周期只提醒一次）
  - [x] **Wi-Fi 连接与实时网速** — SSID / 信号 / 协商速率，上下行速率每帧刷新（可常驻）
  - [x] **移动热点连接数** — 读 ICS 邻居表，设备接入 / 离开实时可见
  - [x] **飞行模式开关** — 同时区分「飞行模式下单独开 Wi-Fi」
  - [x] **蓝牙连接与已连接设备** — 胶囊里直接显示设备名，展开看设备清单
  - [x] **麦克风占用提醒** — 读隐私授权库，零副作用，值级忽略与摄像头同语义
  - [x] **深色 / 浅色切换提示** — 太阳月亮合成图标 `sun-moon`，深浅共用一条岛
- [x] 设置界面（玻璃拟态 + 弹簧动效）· 托盘 · 应用图标
- [x] 媒体：真实 GSMTC（遍历全部会话 + 优先级排序 + 浏览器降级）
- [x] **应用图标提取**：AUMID → 进程 → 窗口 WM_GETICON → exe 图标 → LRU 缓存
- [x] 通知 / 时钟 / 时长 / 媒体 / 系统状态的胶囊内容渲染

## 诊断命令（系统状态扩展）

```bat
ChronoIsland.exe sysprobe      :: 五类快照的真实读数
ChronoIsland.exe sysdiff       :: 差分逻辑（注入假快照，不改机器状态）
ChronoIsland.exe sysrender     :: 六类岛渲染 → PNG + 宽度 / 墨迹边距自检
ChronoIsland.exe sysisland     :: 在线链路（源 → 总线 → 栈）
ChronoIsland.exe wifiui        :: 网速显示稳定性（任意流量下胶囊宽度极差必须为 0）
ChronoIsland.exe pillwidth     :: 全部胶囊宽度公式 vs 排版
```
