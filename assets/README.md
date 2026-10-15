# ChronoIsland 图标资源

## 当前图标

`assets/icon.ico` — 7 个尺寸（16 / 24 / 32 / 48 / 64 / 128 / 256），全部为真实尺寸 PNG。

来源：用户提供的 `favicon_multi.ico`（已复制为 icon.ico）。
我早期合成的版本备份在 `icon-concepts/icon-merged-backup.ico`（其 24px 为 32px 缩放，不如原版）。

**要换图标**：直接替换 `assets/icon.ico` 后重新构建，exe 主图标 + 托盘 + 窗口标题栏都会更新。
**缺失时**程序回退到内置矢量胶囊图标，不会崩溃。

这个图标会用于：
- 应用程序主图标（任务栏、Alt+Tab、窗口标题栏）
- **系统托盘图标**（右键菜单：打开设置 / 暂停 / 退出）
- 发布后的 exe 文件图标

**若文件不存在**，程序会自动使用内置的矢量占位图标（蓝紫渐变胶囊），不会崩溃。
放好图标后重启程序即可生效。

## 源文件

| 文件 | 说明 |
| --- | --- |
| [icon-concepts/concept-C-squircle-3d.png](<icon-concepts/concept-C-squircle-3d.png>) | **选定方案**（2048×2048） |
| [icon-concepts/C-enlarged-v1.png](<icon-concepts/C-enlarged-v1.png>) | C 的扩大版（小尺寸更清晰） |
| [icon-concepts/对比图.png](<icon-concepts/对比图.png>) | 五款概念横向对比 |
| [icon-concepts/扩大版对比.png](<icon-concepts/扩大版对比.png>) | C 三版在 150/48/24/16px 下的可读性对比 |
