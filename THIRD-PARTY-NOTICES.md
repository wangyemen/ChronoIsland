# 第三方许可声明

本项目的界面图标与第三方依赖的许可信息如下。主项目本体采用 **MIT License**，见 [LICENSE](LICENSE)。

## Lucide 图标（ISC License）

- 项目：<https://lucide.dev>
- 仓库：<https://github.com/lucide-icons/lucide>
- 许可：**ISC License** —— 允许免费使用、修改、再分发（含商用），只需保留版权与许可声明。
- 许可全文：<https://github.com/lucide-icons/lucide/blob/main/LICENSE>

本项目以源码形式引入了 26 个 Lucide SVG 图标（位于 `Assets/Icons/`，每个文件首行均保留出处注释），
并在运行时由 `Native/SvgIcons.cs` 解析为 WPF 几何图形。改动仅限：去掉 `class` 属性、
`stroke-width` 由 2 微调为 1.75，几何路径原样保留。

ISC 许可全文：

```
ISC License

Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part of project
(https://github.com/lucide-icons/lucide). All other copyright (c) for project Lucide are
held by Lucide Contributors 2022.

Permission to use, copy, modify, and/or distribute this software for any purpose with or
without fee is hereby granted, provided that the above copyright notice and this permission
notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO
THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT
SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR
ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF
CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE
OR PERFORMANCE OF THIS SOFTWARE.
```

## Windows SDK / GSMTC

本项目通过 `Windows.Media.Control` 的 **GlobalSystemMediaTransportControlsSession (GSMTC)**
读取系统媒体会话并发送控制指令，属于 Windows 运行时公开 API，不含第三方代码分发。

## jsDelivr（备用下载源）

自动更新流程（`Core/Updater.cs`）优先读取 `raw.githubusercontent.com` 上的 `version.txt`，
失败时回退到 jsDelivr 等价地址；release 资产下载失败时同样自动回退 jsDelivr 的 `gh-releases`
直链。国内网络下 `github.com` 资产链经常连不上，jsDelivr 作为备选保证更新可用。

- 项目：<https://www.jsdelivr.com>
- 仓库：<https://github.com/jsdelivr/jsdelivr>
- 用途：读 `version.txt`（`gh/` 前缀）+ 下载 GitHub Releases 资产（`gh-releases/` 前缀）

