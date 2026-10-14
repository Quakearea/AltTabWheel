# AltTabWheel 开发文档

> 面向开发者的架构说明、关键决策与调试指南。
> 用户向说明见 [README.md](../README.md)。

## 1. 总体架构

```
App (Composition Root)
├── GlobalInputHook      WH_KEYBOARD_LL + WH_MOUSE_LL 全局输入拦截
├── WindowService        窗口枚举 / 过滤 / 分组（Z 序 ≈ MRU）
├── IconService          图标提取（多级回退）
├── WindowActivator      前台激活（AttachThreadInput 握手）
├── WheelWindow          轮盘覆盖层 UI（唯一可见窗口）
└── TrayIcon             NotifyIcon + 退出菜单
```

数据流：

```
物理按键 → LL 钩子回调（判定/吞掉）→ Dispatcher 事件
    → App 协调逻辑 → WheelWindow.Select/Open/CloseWheel
    → 松开 Alt → WindowActivator.Activate(hwnd)
```

## 2. 关键设计决策

### 2.1 钩子热路径纪律

LL 钩子回调在每次系统输入时执行，必须零成本：

- 只允许：读 vkCode（unsafe 指针）、几个布尔判定、返回 1 或 CallNextHookEx。
- 禁止：IO、日志、分配（闭包）、Marshal.PtrToStructure、UI 操作。
- UI 工作一律 Dispatcher.BeginInvoke 投递。

### 2.2 轮盘几何约定（确定性）

扇区从屏幕正上方（-90°）开始，顺时针均布：

```
angle(i) = -90° + i * (360° / n)        // 屏幕坐标 y 向下
radius   = clamp(max(150, (item+gap) / (2·sin(π/n))), 150, 330)
```

该约定保证用户的肌肉记忆在版本迭代中不被破坏；任何布局重构必须保持索引到角度映射不变。

### 2.3 窗口枚举与过滤

- EnumWindows 即 Z 序（顶到底），近似系统 Alt+Tab 的 MRU 顺序。
- 过滤规则（Raymond Chen IsAltTabWindow + DWM）：可见、未 cloaked、非纯 WS_EX_TOOLWINDOW、仅根所有者（无 owner）、标题非空、排除已知 shell 类名与本进程。
- WS_EX_TOPMOST 窗口单独成桶追加到末尾，避免 PiP/通知等永久占据 MRU 首位。
- HWND 级实例缓存复用已提取图标。

### 2.4 前台激活

后台进程 SetForegroundWindow 受 foreground lock 限制，经典解法：

```
AttachThreadInput(current, foregroundThread, true)
if IsIconic → ShowWindow(SW_RESTORE)
BringWindowToTop + SetForegroundWindow
AttachThreadInput(..., false)
```

### 2.5 覆盖层窗口

- WindowStyle=None + AllowsTransparency + Topmost，覆盖鼠标所在显示器的 rcMonitor。
- 扩展样式 WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW：不抢焦点、不进 Alt+Tab 列表。
- IsHitTestVisible=False：鼠标事件穿透到下层窗口（见 §5 鼠标指向的设计）。

## 3. 已修复的典型 bug（回归警示）

| Bug | 根因 | 教训 |
| --- | --- | --- |
| 枚举恒为空 | GetWindowTextW(hwnd, null, 0) 在 nMaxCount=0 时恒返回 0 | 长度查询用 GetWindowTextLengthW |
| 扇区不渲染 | BuildWheel 开头调 ClearWheel 误清 _windows | 数据生命周期分离：容器清理不等于数据清理 |
| Alt 松开无响应 | Show() 先于 IsOpen=true，IsVisibleChanged 同步把 hook 状态覆写回 false | 状态赋值顺序即协议 |

## 4. 调试

- 日志：%TEMP%\alttabwheel.log（关键事件；钩子里禁止记日志）。
- 自检：AltTabWheel.exe --selftest 跳过钩子，2 秒后自动打开轮盘（验证 UI 链路）。
- UI 结构检查：UIAutomation 按窗口标题 AltTabWheel 查找（Process.MainWindowHandle 对 ToolWindow 返回 0，勿用）。

## 5. 鼠标指向的设计（v0.2）

覆盖层 IsHitTestVisible=False 拿不到鼠标，因此指向检测放在 WH_MOUSE_LL：

- WM_MOUSEMOVE 且轮盘打开时，计算鼠标相对轮盘中心的角度，命中最近扇区。
- 模式仲裁：鼠标移动超过阈值进入 mouse 模式；任何键盘导航立即切回 keyboard 模式并重置移动基线，避免按键与鼠标互相拉扯。
- 最小生效半径：距中心小于 MinMouseRadius 的移动不触发（防止中心抖动）。

## 6. 构建

```powershell
dotnet build .\AltTabWheel.sln -c Release   # 必须 0 error，新 warning 需解释
dotnet build .\AltTabWheel.sln -c Debug
```

## 7. 版本历史

见 git log。当前 v0.1（2026-10-14）：轮盘切换核心链路。
