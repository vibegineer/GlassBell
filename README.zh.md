<p align="center"><img src="assets/icon-256.png" width="128" alt="GlassBell 图标"></p>

<h1 align="center">GlassBell</h1>

<p align="center">把所有应用的通知显示为 macOS 风格的快速玻璃横幅。适用于 Windows 11。</p>

<p align="center"><a href="README.md">English</a> · <b>中文</b> · <a href="README.hi.md">हिन्दी</a> · <a href="README.es.md">Español</a> · <a href="README.ar.md">العربية</a> · <a href="README.ru.md">Русский</a></p>

---

GlassBell 在后台运行，接收 Windows 收到的每一条通知，并在屏幕右上角显示为玻璃横幅：边缘像透镜一样弯曲背景，带有彩色色边、沿轮廓流动的高光，以及会随背后内容变化的柔和阴影。

## 功能

- **所有应用。** GlassBell 读取所有应用的通知（Windows 通知侦听器），并以自己的横幅显示，最多同时堆叠四条。
- **真正的玻璃，由显卡绘制。** 边缘折射、色散、高光、自适应色调和阴影。四个画质等级，以及可选的实时背景：即使横幅在移动，玻璃也会跟随它背后的内容。
- **与屏幕刷新率同步。** 显卡直接获取横幅背后的屏幕画面并一次性绘制完成，因此玻璃能跟上 60、144、240 Hz（或更高）的显示器，而处理器几乎空闲。1.0 版的经典渲染方式仍保留在设置中；如果显卡无法这样绘制，会自动改用经典方式。
- **关闭一条或全部。** 指针所在横幅的角上会出现圆形 ×，点击即可关闭；有两条或更多横幅时，下方会出现 **全部关闭** 按钮。托盘菜单和命令 `GlassBell.exe --close-all` 也能做到。
- **快速。** 通知到达后 30–60 毫秒内，单条横幅就会出现在屏幕上（在开发者的电脑上测得）。
- **点击即打开。** 点击横幅会打开通知所指向的内容，与点击 Windows 原生通知的效果相同。
- **声音。** 三种在代码中合成的提示音（没有音频文件）；音量可以跟随系统音量。
- **带实时预览的设置**、托盘图标、开机自启动。
- **20 种语言。** 界面跟随 Windows 显示语言（不支持的语言显示英文），也可以在设置底部手动选择。
- **轻量。** 空闲时几乎不占资源，横幅显示时只占一个 CPU 核心的百分之几，内存约 100 MB，显示数百条横幅后依然如此（在开发者的电脑上测得）。

## 安装

1. 从 [Releases](https://github.com/vibegineer/GlassBell/releases/latest) 下载 **GlassBell-Setup.exe** 并运行。不需要管理员权限。
   安装程序没有数字签名，因此 SmartScreen 可能会显示“Windows 已保护你的电脑”：请点击 **更多信息 → 仍要运行**。
2. 打开 **请勿打扰**（设置 → 系统 → 通知），这样 Windows 的横幅就不会和 GlassBell 的横幅同时出现。开启请勿打扰后通知仍会送达，GlassBell 也仍能看到它们。

时钟旁边的图标可以打开设置。卸载 GlassBell：设置 → 应用 → 安装的应用 → GlassBell → 卸载。

**系统要求：** 64 位 Windows 11。其余所需组件（.NET Framework 4.8、Direct3D 11）都已包含在 Windows 中。

## 命令行

GlassBell 也可以为你自己的脚本显示横幅：

```
GlassBell.exe --title "构建完成" --body "214 项测试全部通过" --app "CI"
```

| 命令 | 作用 |
|---|---|
| `GlassBell.exe` | 需要时启动 GlassBell，并打开设置 |
| `--title T --body B [--app A] [--app-id AUMID] [--seconds S]` | 显示一条横幅（`--app-id` 使用已安装应用的图标） |
| `--test` | 显示两条示例横幅 |
| `--close-all` | 关闭屏幕上的所有横幅 |
| `--settings` / `--quit` | 打开设置 / 退出 GlassBell |
| `--install` / `--uninstall` | 开启 / 关闭开机自启动（用于不经安装程序使用的副本） |
| `--stats` | 把正在运行的副本的内存占用写入日志 |

## 文件

| 内容 | 位置 |
|---|---|
| 程序 | `%LOCALAPPDATA%\Programs\GlassBell` |
| 设置 | `%APPDATA%\GlassBell\settings.ini` |
| 日志 | `%LOCALAPPDATA%\GlassBell\log.txt` |

设置中选择的语言保存在 `settings.ini` 中，例如 `lang=zh`（或 `lang/` 中的其他代码）；`lang=auto` 表示跟随 Windows。
如果在 `GlassBell.exe` 旁边放一个 `portable.flag` 文件，设置和日志会改为保存在程序所在的文件夹中。

## 构建

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

该脚本使用 Windows 自带的 .NET Framework 中的 C# 编译器生成 `bin\GlassBell.exe` 和 `dist\GlassBell-Setup.exe`：不需要 SDK、NuGet 或 Visual Studio。

| 文件夹 | 内容 |
|---|---|
| `src/` | 程序本身：横幅、显卡渲染（`GpuEngine.cs`、`Dx.cs`）、经典玻璃渲染、通知镜像、设置、提示音 |
| `setup/` | 安装程序和卸载程序 |
| `lang/` | 界面文本，每种语言一个文件 |
| `assets/` | 图标 |
| `tools/` | 开发时使用的测试和测量脚本，以及图标生成器 |

## 翻译

各语言的翻译（包括本页）由 AI 完成，只检查了是否完整、能否放得下，没有经过母语者审校。欢迎提交修正：每种语言是 [`lang/`](lang) 中的一个纯文本文件，[`lang/en.txt`](lang/en.txt) 解释了每一行的含义。

## 支持

GlassBell 是免费的。如果它对你有用，可以在 [Patreon](https://www.patreon.com/vibegineer) 上支持它的开发。

## 许可证

[MIT](LICENSE)。macOS 是 Apple Inc. 的商标；GlassBell 与 Apple 没有任何关联。
