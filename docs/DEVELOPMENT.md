# AltTabWheel 开发文档

> 面向开发者的架构说明、关键决策、调试指南与设计演化记录。
> 用户向说明见 [README.md](../README.md)。

## 1. 总体架构

```
App (Composition Root)
├── GlobalInputHook      WH_KEYBOARD_LL + WH_MOUSE_LL 全局输入拦截
├── WindowService        窗口枚举 / 过滤 / 分组（Z 序 ≈ MRU）
├── TitleCodec           标题清洗（交错零折叠 / 控制符剥离）
├── IconService          高清图标提取（DrawIconEx + IShellItemImageFactory）
├── WindowActivator      前台激活（AttachThreadInput 握手）
├── AppSettings          JSON 配置（%AppData%\AltTabWheel\settings.json）
├── WheelWindow          轮盘覆盖层 UI（唯一常驻窗口）
├── SettingsWindow       设置窗口（--settings / 托盘菜单打开）
└── TrayIcon             NotifyIcon + 菜单
```

数据流：

```
物理按键 → LL 钩子回调（判定/吞掉）→ Dispatcher 事件
    → App 协调逻辑 → WindowService.BuildItems（分组/固定项）
    → WheelWindow.Open/Select/SelectDelta/CloseWheel
    → 松开 Alt → WindowActivator.Activate(hwnd)
```

## 2. 关键设计决策

### 2.1 钩子热路径纪律

LL 钩子回调在每次系统输入时执行，必须零成本：

- 只允许：读 vkCode（unsafe 指针）、几个布尔判定、返回 1 或 CallNextHookEx。
- 禁止：IO、日志、分配（闭包）、Marshal.PtrToStructure（鼠标钩子除外，移动低频）、UI 操作。
- UI 工作一律 Dispatcher.BeginInvoke 投递；鼠标移动在钩子侧做 6px 阈值节流。

### 2.2 轮盘几何约定（确定性，保肌肉记忆）

顶层扇区从屏幕正上方（-90°）开始顺时针均布：

```
angle(i) = -90° + i * (360° / n)        // 屏幕坐标 y 向下
radius   = clamp(max(150, (item+gap) / (2·sin(π/n))), 150, 330)
```

级联子轮盘（合并任务栏）：子扇区以组角度为中心扇形展开——

```
子项角距 = min(42°, 150° / (m-1))，m 为组内窗口数
子半径 R2 = R + 100，子尺寸 48
```

任何布局重构必须保持索引到角度的映射不变。

### 2.3 窗口枚举与标题来源

- EnumWindows 即 Z 序（顶到底），近似系统 Alt+Tab 的 MRU 顺序。
- 过滤（Raymond Chen IsAltTabWindow + DWM）：可见、未 cloaked、非纯 WS_EX_TOOLWINDOW、
  仅根所有者、排除 shell 类名与本进程。
- WS_EX_TOPMOST 窗口单独成桶追加末尾，避免 PiP/通知抢占 MRU 首位。
- **标题来源：UIA 优先**（见 §7）。GetWindowTextW + TitleCodec 兜底。

### 2.4 前台激活

后台进程 SetForegroundWindow 受 foreground lock 限制：

```
AttachThreadInput(current, foregroundThread, true)
if IsIconic → ShowWindow(SW_RESTORE)
BringWindowToTop + SetForegroundWindow
AttachThreadInput(..., false)
```

### 2.5 覆盖层窗口

- WindowStyle=None + AllowsTransparency + Topmost，覆盖鼠标所在显示器的工作区。
- WPF 尺寸是 DIP，显示器 API 返回物理像素：必须按目标屏 GetDpiForMonitor 逐屏换算，
  否则高 DPI 屏窗口尺寸翻倍整体降采样（模糊的根因之一）。
- 扩展样式 WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW：不抢焦点、不进 Alt+Tab 列表。
- IsHitTestVisible=False：鼠标事件穿透（鼠标指向检测因此放在 WH_MOUSE_LL 被动观察）。
- **AllowsTransparency 窗口上 DwmRegisterThumbnail 不渲染**（layered window 合成路径），
  实时缩略图方案不可用；窗口快照需用 PrintWindow(PW_RENDERFULLCONTENT)。

### 2.6 图标高清链路

旧版 CreateBitmapSourceFromHIcon 只取系统默认 32×32，显示在 ~46px 区域必虚。
现统一以 64×64 产出：

1. 窗口自身图标 WM_GETICON(ICON_BIG → ICON_SMALL2) → DrawIconEx 双三次光栅化 64×64。
2. 窗口类图标 GCLP_HICON → 同上。
3. exe 文件图标 → IShellItemImageFactory.GetImage(64, RESIZETOSTRETCH|BIGGERSIZEOK)，
   Shell 提取 exe 内最大图标源（最高 256px）平滑缩放。
4. SHGetFileInfo / ExtractIconEx 兜底。

来源原则：优先应用运行时设置的窗口图标（与系统 Alt+Tab 一致），
失败才用文件图标。个别应用（如 Windows Terminal）源仅 32px，放大后有颗粒感，
这是源的天花板。

### 2.7 配置系统

- AppSettings：JSON 持久化，%AppData%\AltTabWheel\settings.json。
- 所有行为开关默认关闭/保守值，新行为必须显式开启。
- 固定项 PinnedApps 支持进程名或 exe 绝对路径。

## 3. 已修复的典型 bug（回归警示）

| Bug | 根因 | 教训 |
| --- | --- | --- |
| 枚举恒为空 | GetWindowTextW(hwnd, null, 0) 在 nMaxCount=0 时恒返回 0 | 长度查询用 GetWindowTextLengthW |
| 扇区不渲染 | BuildWheel 开头调 ClearWheel 误清 _windows | 数据生命周期分离：容器清理 ≠ 数据清理 |
| Alt 松开无响应 | Show() 先于 IsOpen=true，IsVisibleChanged 同步把 hook 状态覆写回 false | 状态赋值顺序即协议 |
| 组合扇区崩溃 | image 先赋 border.Child 又放进 Grid.Children | WPF 逻辑父唯一，换容器先断旧父 |
| 高 DPI 模糊 | 显示器物理像素直接当 WPF DIP 用 | DIP/物理换算必须按目标屏 DPI |
| Edge 标题乱码 | 环境 win32k 标题层失真，GetWindowTextW 不可靠 | 见 §7：UIA 优先 |

## 4. 调试

- 日志：%TEMP%\alttabwheel.log（关键事件；钩子里禁止记日志；Dispatcher 未处理异常也落日志）。
- 自检：AltTabWheel.exe --selftest 跳过钩子，2 秒后自动打开轮盘（验证 UI 链路）。
- --settings 直接打开设置窗口。
- UI 结构检查：UIAutomation 按窗口标题 AltTabWheel 查找
  （Process.MainWindowHandle 对 ToolWindow 返回 0，勿用）。
- **pwsh 沙箱内的 P/Invoke 结果不可信**（DPI/标题全面失真），诊断必须独立进程。
- 合成按键测试注意：keybd_event 注入的按键进程退出后仍保持按下状态；
  SetCursorPos 不产生 LL 鼠标消息（用 mouse_event）；selftest 轮盘需注入 Alt 按住才能走导航分支。
- 改代码前必须先杀 AltTabWheel 进程再等 2 秒，否则 exe 锁定导致构建失败。

## 5. 鼠标指向的设计

覆盖层 IsHitTestVisible=False 拿不到鼠标，指向检测放在 WH_MOUSE_LL：

- 只观察不拦截：鼠标始终归下层窗口/系统使用。
- WM_MOUSEMOVE 且轮盘打开时，钩子侧 6px 阈值节流后投递；UI 线程计算相对轮盘中心的
  角度命中最近扇区（子轮盘展开时按距离分带：外环带子项）。
- 模式仲裁：鼠标累计移动超过 MouseTakeoverTravelPx 进入 mouse 模式；任何键盘导航
  （Tab/方向键/滚轮）调用 NotifyKeyboardNavigation 切回 keyboard 模式并重置移动基线，
  两种输入互不拉扯。
- 最小生效半径 MouseMinRadius 内的移动不触发（防止中心抖动）。

## 6. 构建

```powershell
dotnet build .\AltTabWheel.sln -c Release   # 必须 0 error，新 warning 需解释
dotnet build .\AltTabWheel.sln -c Debug
```

## 7. 环境失真与标题解码

本机 win32k 标题层与标准 Windows 不一致：所有窗口标题按 4 字节/字符存储，
GetWindowTextW 仍按 WCHAR(2B) 返回 —— 表现为有效字符与 U+0000 交错
（'Windows' → 'W\0i\0n\0...'）。终端类窗口标题还残留 ANSI 控制符（OSC/SGR 序列）。

**最终方案：UIA 优先**。AutomationElement.FromHandle(hwnd).Current.Name 由窗口的
UIA provider 自报（走 COM，不经 win32k 失真层），Edge/微信等自绘窗口也能拿到
正确中文名，且覆盖 GetWindowText 为空的窗口（不再回退进程名）。

TitleCodec 作为兜底保留三级清洗：

1. 交错零检测：奇数索引为 0 的比例 ≥ 80% 时按偶数索引折叠。
2. 控制符清理：剥离 C0（保留 \t）、DEL、C1 区段。
3. 孤立代理项清理。

个别进程（如 Taskmgr）标题呈其他失真形态，折叠无法完美还原 —— 进程名始终正确。

## 8. 级联子轮盘交互

- 选中合并组 **立即自动展开**（首个子项直接选中），无需再按 Tab。
- 子轮盘内 Tab 循环子项；越界自动收起并移到顶层相邻项；Esc/方向键先收起。
- Alt 松开激活当前子项；组未展开时（理论不可达）激活组内 MRU 第一窗口。
- 鼠标指向组同样触发展开；外环带移动选子项。

## 9. 被否决/撤销的设计（防止回潮）

| 设计 | 结局 | 原因 |
| --- | --- | --- |
| Win+数字 激活窗口 | 否决 | 吞系统任务栏快捷键风险大；用户明确要求取消 |
| 轮盘内 Alt+数字 选择（数字角标） | 实现后撤销 | 用户反馈取消数字键设计 |
| DwmRegisterThumbnail 中心实时预览 | 技术不可行 | AllowsTransparency(layered) 窗口上不渲染 |
| PrintWindow 中心快照预览（三段徽章） | 实现后撤销，位置待定 | 用户要求中心恢复圆形；小窗位置候选：A 跟随选中扇区 / B 轮盘下方横条 / C 鼠标旁浮窗 / D 右下角浮窗（待用户定夺） |

## 10. 已知限制

- 窗口很多（>16）时扇区拥挤，需要分页/螺旋层级布局。
- 中心窗口小窗预览待重新设计（见 §9）。
- 任务管理器等特殊窗口的激活可能被其自身窗口特性干扰。
- 个别窗口标题的环境级失真无法完全还原（进程名始终正确）。
- Z 序为近似 MRU；管理员窗口需以管理员运行本程序（UIPI）。

## 11. 版本历史

### v0.1（2026-10-14）轮盘切换核心链路

- WH_KEYBOARD_LL + WH_MOUSE_LL 拦截 Alt+Tab；Tab/Shift+Tab/滚轮/方向键导航。
- EnumWindows Z 序枚举 + IsAltTabWindow 过滤 + topmost 隔离。
- WM_GETICON/ExtractIconEx 图标；AttachThreadInput 前台激活；轮盘 UI 与中心徽章。

### v0.2（2026-10-14 → 10-15）交互增强与修复

- **标题**：诊断发现环境 win32k 标题失真；TitleCodec 折叠清洗 → 最终改 UIA 优先，
  Edge 乱码与空标题（微信→Weixin）一并解决。
- **DPI**：窗口尺寸物理像素→DIP 按目标屏换算，修复 2K 屏模糊。
- **图标**：GCLP_HICON/SHGetFileInfo 兜底 → 最终 DrawIconEx 64px 光栅化 +
  IShellItemImageFactory（256px 源）高清化。
- **鼠标指向选中**：WH_MOUSE_LL 被动观察 + 角度命中 + 键盘/鼠标模式仲裁。
- **设置系统**：AppSettings JSON + 设置窗口（--settings/托盘）。
- **合并任务栏**：进程分组 + 数量角标 + 级联子轮盘（选中即自动展开）。
- **固定任务栏**：固定项半透明展示，无窗口时选中松开 Alt 即启动。
- 设计演化（否决记录见 §9）：Win+数字 → 数字键 → 取消；中心实时预览 → 快照 → 撤销待定。

见 git log。
