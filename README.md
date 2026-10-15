# AltTabWheel

Alt+Tab 轮盘切换器 —— .NET 8 WPF 实现的 Windows 窗口切换替代方案。

按住 Alt 呼出轮盘，窗口图标环形排布；可用键盘、鼠标指向或 Win+数字 选择，松开 Alt 即切换。

![预览](docs/screenshot.png)

## 交互

| 操作 | 效果 |
| --- | --- |
| Alt+Tab / Alt+Shift+Tab | 呼出轮盘 / 前进 / 后退 |
| Alt+滚轮 | 前进 / 后退 |
| Alt+方向键 | 按方向选择几何最近的窗口 |
| 鼠标指向（可开关） | 移动鼠标选择对应扇区，与键盘导航自动仲裁互不拉扯 |
| 松开 Alt | 切换到选中窗口 |
| Esc | 取消切换 |

## 任务栏式设计（设置中开关）

- **合并任务栏**：同进程窗口合并为一个扇区（右下角数量角标），选中组即自动
  展开级联子轮盘（StarPie 风格：子扇区在组扇区外侧呈弧形展开，首个子项直接选中），
  子轮盘内继续 Tab 循环、越界自动收起回顶层。
- **固定任务栏**：按进程名或 exe 路径固定应用。固定项排在轮盘末尾，无窗口时
  半透明显示，选中并松开 Alt 即启动。已有窗口的固定项自动合并到对应窗口。
- **设置页面**：托盘右键 Settings 或命令行 --settings。配置持久化在
  %AppData%\AltTabWheel\settings.json。

## 实现要点

- **Alt+Tab 拦截**：WH_KEYBOARD_LL + WH_MOUSE_LL 全局低级钩子（方案参考 FlipSwitcher）。
  钩子回调只读 vkCode 指针并判定，不做 IO/分配，UI 工作投递 Dispatcher。
- **窗口枚举**：EnumWindows Z 序遍历（近似 MRU），Raymond Chen IsAltTabWindow 规则，
  DWM cloaked 过滤，topmost 窗口单独成桶追加末尾。
- **标题来源**：优先 UIA 名称（窗口 provider 自报，绕过本环境 win32k 标题层失真，
  Edge/微信等自绘窗口也能拿到正确中文名），GetWindowTextW + TitleCodec 兜底。
- **DPI**：PerMonitorV2，窗口按目标显示器 DPI 把物理像素换算为 DIP，
  覆盖鼠标所在显示器的工作区。
- **图标**：窗口自身图标（WM_GETICON → GCLP_HICON）DrawIconEx 高质量光栅化到 64×64；
  文件图标走 IShellItemImageFactory（Shell 渲染 exe 内最大图标源，最高 256px）。
- **鼠标指向**：WH_MOUSE_LL 观察鼠标移动（不拦截、不抢占鼠标），角度命中最近扇区；
  模式仲裁：鼠标累计移动超阈值接管，任何键盘导航立即切回键盘模式。
- **前台激活**：AttachThreadInput 握手 + SetForegroundWindow，最小化窗口先 SW_RESTORE。

## 构建与运行

```powershell
dotnet build .\AltTabWheel.sln -c Release
.\AltTabWheel\bin\Release\net8.0-windows\AltTabWheel.exe            # 正常运行
.\AltTabWheel\bin\Debug\net8.0-windows\AltTabWheel.exe --selftest   # UI 链路自检
.\AltTabWheel\bin\Debug\net8.0-windows\AltTabWheel.exe --settings   # 直接打开设置
```

托盘图标右键可退出/打开设置。调试日志 %TEMP%\alttabwheel.log。

## 已知限制（v0.2）

- 窗口很多（>16）时扇区拥挤，需要分页/螺旋层级布局
- 图标而非实时缩略图（计划接入 DwmRegisterThumbnail）
- 任务管理器等特殊窗口的激活可能被其自身窗口特性干扰
- 个别窗口标题的环境级编码失真无法完全还原（进程名始终正确）
- Z 序为近似 MRU；管理员窗口需以管理员运行本程序（UIPI）

## 参考

- [FlipSwitcher](https://github.com/dianbanjiu/FlipSwitcher) — Alt+Tab 拦截与窗口服务
- [StarPie](https://github.com/Star-Pie/StarPie) — 轮盘 UI、级联子轮盘与工程规范
- [ExplorerPatcher](https://github.com/valinet/ExplorerPatcher) — 任务栏行为参考
- [Microsoft Learn](https://learn.microsoft.com/windows/win32/dwm/thumbnail-ovw) — DWM/Win32 文档
