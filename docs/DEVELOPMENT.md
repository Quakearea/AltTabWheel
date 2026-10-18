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
- Alt/Shift 状态由钩子消息流自行跟踪（v0.4 起），不再调 GetAsyncKeyState。
- UI 工作一律用预分配无闭包委托经 Dispatcher.BeginInvoke 投递（v0.4 起）；
  鼠标移动在钩子侧做 6px 阈值节流，坐标经字段传递（`_pendingMousePt`）。
- 鼠标钩子按需安装：轮盘关闭时卸载 WH_MOUSE_LL（v0.4 起），避免全系统鼠标消息无谓调度。

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

### 2.8 窗口实时预览轮播（DWM Thumbnail，v0.3）

设计移植自 `HalfEllipseCarousel`（tmp8-lbt）：竖直椭圆**左半边**弧（参考原版几何，预览面板停靠工作区左缘），弧长均匀参数化，无限循环 Wrap + 指数阻尼动画（帧率无关，系数 7.5）。

**合成模型（实验证实）**：`DwmRegisterThumbnail` 的缩略图渲染在 destination 窗口客户区
WPF 内容**之下**（实测截图 docs/d2_zorder.png，三个半透明红块稳定盖住缩略图）。
由此每张卡 = 下层实时缩略图矩形 + 上层 WPF 罩层（半透明黑 + 白描边 + 圆角遮罩）：

| 规格表参数 | 实现层 | 手段 |
|---|---|---|
| 卡片不透明度 | 缩略图 | `DWM_TNP_OPACITY`（中心 100% → ±4 15% → ±5 0%） |
| 黑色罩层 | WPF 罩层 | Border.Opacity（±1 20% → ±4 68%） |
| 模糊半径 | —（v1 缺省） | WPF BlurEffect 无法作用于下层元素，需 CompositionBackdropBrush（v2 待办） |

**硬约束**：DWM 缩略图在 `AllowsTransparency`（layered）窗口上**完全不渲染**——宿主必须是
独立非透明窗口（深色底 + DWMWA_WINDOW_CORNER_PREFERENCE 圆角），与轮盘窗口各自 Topmost。

距离分层公式（与参考实现一致，规格表逐点吻合）：
```
opacity: ad<=1 ? 1-0.4*ad : 0.6*max(0,(5-ad)/4)
dim:     ad<=1 ? 0.2*ad : min(0.85, 0.2+0.16*(ad-1))
scale:   1-0.45*min(|u|,1)^1.25（|u|>1 继续收缩至 0.30 底）
u = Wrap(i-current)/4.5，Wrap 环绕到 [-n/2,n/2]（无限循环）
```

- 动画在 `CompositionTarget.Rendering`（vsync 节拍）里只做 `DwmUpdateThumbnailProperties`
  矩形更新——与 glazewm 的 perf 方案一致（keypress 不直接打 DWM）。
- 缩略图注册按 HWND 缓存、跨轮盘开关复用；窗口列表刷新走 diff（新增注册/消失注销）。
- `rcSource` 按目标宽高比居中裁剪（UniformToFill），窗口比例不变形。
- 停靠左缘而非跟随轮心：面板右缘若伸到轮心会盖住左侧扇区（首版实测，d2_full.png vs d2_dock.png）。
- 固定项（未运行）无 HWND 不可预览：选中它时预览关闭，切走恢复（表现为 preview open 重复日志）。
- **去黑背景的正解是 SetWindowRgn**（窗口形状 = 可见卡的圆角矩形并集，节流 80ms）：
  WPF 非 layered 窗口交换链不保留 alpha，"透明背景"只是元素树内部概念——
  DwmEnableBlurBehindWindow / SetWindowCompositionAttribute Acrylic / DWMWA_SYSTEMBACKDROP /
  Opacity=0.99 **全部实测无效**（黑块依旧）。region 裁剪后间隙与空白处没有窗口，
  直接露出下层轮盘遮罩。region 外扩 pad 须容纳 2 DIP 选中边框。
- **DWM 缩略图之间的叠放顺序既非更新序也非注册序**（实测两种假设均与观察矛盾）——
  正确层次由几何保证：按距离近→远接受卡片，远卡缩略图矩形裁掉与近卡的垂直重叠带
  （重叠区只显示近卡 = "中间最上，依次往下"的遮挡视觉），罩层保持全尺寸（ZIndex 递降）。
- **罩层透明度必须放 Background 的 alpha 通道**（Border.Opacity 恒 1）：
  选中卡 dim≈0 时 Border.Opacity=0 会把亮蓝高亮边框一起透明掉。
- 布局为竖直椭圆**左半边**（参考原版 cx=0.72w）：选中卡左凸点在面板水平中部 0.46w、
  y=cy 垂直居中 —— 与轮心同高水平线，窗口位置与轮盘对应。
  宿主 500×760 DIP（≈屏幕 1/3 宽）；选中卡亮蓝边框 #7AA5FF 2px（ad<0.5）。
- 窗口数 > 21（MaxThumbs）时超出项不参与轮播（v1 截断）。

## 3. 已修复的典型 bug（回归警示）

| Bug | 根因 | 教训 |
| --- | --- | --- |
| 枚举恒为空 | GetWindowTextW(hwnd, null, 0) 在 nMaxCount=0 时恒返回 0 | 长度查询用 GetWindowTextLengthW |
| 扇区不渲染 | BuildWheel 开头调 ClearWheel 误清 _windows | 数据生命周期分离：容器清理 ≠ 数据清理 |
| Alt 松开无响应 | Show() 先于 IsOpen=true，IsVisibleChanged 同步把 hook 状态覆写回 false | 状态赋值顺序即协议 |
| 组合扇区崩溃 | image 先赋 border.Child 又放进 Grid.Children | WPF 逻辑父唯一，换容器先断旧父 |
| 高 DPI 模糊 | 显示器物理像素直接当 WPF DIP 用 | DIP/物理换算必须按目标屏 DPI |
| Edge 标题乱码 | 环境 win32k 标题层失真，GetWindowTextW 不可靠 | 见 §7：UIA 优先 |

| 轮播预览（v0.3） | WPF 非 layered 窗口"透明背景"只是元素树内部概念，交换链不保留 alpha | BlurBehind/Acrylic/SYSTEMBACKDROP/Opacity<1 全输出黑块；用 SetWindowRgn 裁剪卡片轮廓 |
| 轮播预览（v0.3） | DWM 缩略图之间的叠放顺序既非更新序也非注册序 | z 层由几何保证：远卡缩略图矩形裁掉与近卡的重叠带 |
| 轮播预览（v0.3） | Border.Opacity 作用于整个元素（含边框） | 罩层透明度走 Background alpha，选中卡亮蓝边框才不丢 |
| 轮播预览（v0.3） | SetWindowRgn 的 pad 未容纳 2 DIP 高亮边框 | region 外扩 pad = ceil(2×dpi)+1 |


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
- 预览轮播：模糊半径未实现（需 CompositionBackdropBrush）；窗口 >21 截断；
  缩略图互相之间的 z 序由 DWM 注册顺序决定（罩层已弱化远处卡，视觉上不明显）。
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

### v0.3（2026-10-17）窗口实时预览轮播 + 响应优化 + 图标高清

**需求来源**（第 4、5 轮反馈）：中心预览改为轮播图，设计移植 tmp8-lbt 的
HalfEllipseCarousel，用户可开关；实时预览指定 DWM Thumbnail API（禁用 PrintWindow/
屏幕捕获）；优化"响应微慢"与"图标像素模糊"；选中卡必须居中（与轮心同高水平线）、
加边框、z 层递降、取消黑色矩形背景、预览加大到约 1/3 屏、兼容合并任务栏组导航。

**技术调研**（multi_search，四条结论）：
- DWM 缩略图在 AllowsTransparency（layered）窗口上**完全不渲染** → 必须独立非透明宿主；
- AutomationElement.FromHandle 偶发秒级卡顿（SO 报告可达 30s）→ "响应微慢"主凶，
  绝不能跑在呼出键按下的 UI 线程上；
- glazewm 的 perf 实践：缩略图矩形更新推迟到 vsync tick（keypress 不直接打 DWM）；
- 合成顺序无文档 → 自行实验（见下）。

**合成模型实验（d2_zorder.png）**：三个半透明红块稳定盖住 DWM 缩略图 → 缩略图渲染在
destination 客户区 WPF 内容**之下**。规格表两个独立参数恰好分层映射：
卡片不透明度 → DWM_TNP_OPACITY（缩略图本体）；黑色罩层 → WPF 罩层 Border
（上层覆盖）；模糊半径无手段（BlurEffect 只模糊元素自身），记为 v2 待办
（CompositionBackdropBrush）。

**布局三易**（每版都截图验证）：
1. 圆扇形（下半弧，选中卡在底部正中）：圆心段切线水平，n 小时卡片肩并肩挤成一团；
2. 竖直椭圆右半弧（弧顶朝轮盘）：可用，但选中卡在面板右缘、左半空着；
3. 最终：竖直椭圆**左半弧**参考原版几何（cx=0.72w，弧长均匀参数化），选中卡左凸点在
   面板水平中部 0.46w、y=cy 垂直居中 —— 与轮心同高水平线，"窗口位置与轮盘对应"。
另外：宿主改停靠工作区左缘（面板右缘若伸到轮心会盖住左侧扇区，首版实测）；
尺寸 460×620 → 500×760 DIP（约屏幕 1/3 宽）。

**去黑背景四连败 + 正解**（每步都构建截图验证）：
DwmEnableBlurBehindWindow → SetWindowCompositionAttribute Acrylic →
DWMWA_SYSTEMBACKDROP_TYPE + Opacity=0.99 → 全部黑块依旧。根因：WPF 非 layered 窗口
交换链不保留 alpha，"透明背景"只是元素树内部概念。**正解：SetWindowRgn 把窗口裁剪成
可见卡的圆角矩形并集**（80ms 节流）——间隙与空白处没有窗口，直接露出轮盘遮罩。

**z 层两实验**：先假设叠放顺序=更新序（按距离远→近更新属性），结果中心卡正确但
相邻卡之间仍反 → 结论 DWM 缩略图叠放顺序不可控（非更新序、非注册序，与观察均矛盾）。
**正解：几何确定性方案** —— 按距离近→远接受卡片，远卡缩略图矩形裁掉与近卡的垂直
重叠带，重叠区只显示近卡（"被遮挡"的视觉由几何保证），罩层保持全尺寸 + ZIndex 递降。

**图标模糊根治**：64px 源在 1.75× DPI 屏上被上采样放大（选中 1.3× 时 64→112 物理 px）→
统一 128px 2× 渲染 + RenderOptions.BitmapScalingMode=Fant 锐利下采样。

**响应慢根治（快照策略）**：启动 2.5s 后台预热枚举（填图标/进程名/标题缓存）→
呼出时用上次缓存快照立即显示 → 后台线程重新枚举（UIA 卡顿不阻塞 UI）→ 完成后按 HWND
保持选中静默重建（WindowService 双缓存加锁）。selftest 改走 OpenWheel 全链路。

**合并任务栏兼容**：轮播目标是"当前激活目标窗口"（含组内子窗口）——
Select/SelectChild 都 raise SelectionChanged，App 层按 HWND 映射回展平列表索引；
组选中自动展开子轮盘时预览跟到组内首个子窗口；固定项（未运行）无 HWND 不可预览，
选中时预览关闭、切走恢复。真实使用日志验证：反复切换 msedge/QQ/explorer/Terminal 正常。

**遗留**：模糊罩层待 CompositionBackdropBrush（v2）；窗口 >21（MaxThumbs）截断；
DWM 缩略图源窗口销毁后由 Refresh diff 注销。

### v0.4（2026-10-18）性能优化 + 选中窗口↔轮盘对应修复

性能审计后的第一批优化，Debug/Release 构建 0 警告，selftest + 物理按键 + 截图验证通过：

- **预览渲染循环收敛即停**（最大收益）：原来预览打开期间 CompositionTarget.Rendering
  永久 vsync 运行——即使动画收敛仍每帧全量布局 + 逐卡 DwmUpdateThumbnailProperties
  （21 卡 × 60fps）。现在 `_current` 收敛且无挂起变化（`_needsFrame`）时退订渲染循环，
  `SetTarget`/`Refresh`/`Open` 重新订阅；收敛帧强制刷 SetWindowRgn（补偿动画期 80ms 节流）。
  预览静止期间 CPU 归零；DWM 缩略图内容实时性由 DWM 自绘保证，与本循环无关。
- **UpdateLayoutFrame 零分配**：FrameLayout/排序数组/可见卡列表全部池化复用；
  `frames.OrderBy+Sort` 换 Array.Sort 键值对一次排序（接受 pass 正序、应用 pass 逆序）；
  每卡 2 个 new SolidColorBrush 换 256 级 alpha 量化共享冻结画刷（选中边框单例）。
  原来 60fps × 21 卡 ≈ 每秒 2500+ 次画刷分配持续打 Gen0 GC。
- **键盘钩子热路径零 P/Invoke**：Alt/Shift 状态由钩子消息流自行跟踪（所有键盘消息
  必经本回调），替代每次按键 3-4 次 GetAsyncKeyState；初值在 Install 冷路径同步一次。
  投递全部改用预分配无闭包委托（`_raiseTabNext` 等字段），热路径真正零分配。
- **鼠标钩子按需安装**：WH_MOUSE_LL 常驻会让全系统每个鼠标消息都调度进本进程消息
  循环；现在 `IsWheelOpen` setter 联动安装/卸载，轮盘关闭期间鼠标路径零成本。
- **MouseMoved 双重调度修复**：钩子侧已 BeginInvoke 到 UI 线程，App 层原来再包一层
  Dispatcher.BeginInvoke（双重调度 + 闭包分配），改为直接调 WheelWindow.HandleMouseMove。

**选中窗口↔轮盘对应修复（根因两处叠加）**：

- **预览条目顺序滞留**：SyncEntries 旧实现只按首次注册序追加、跨呼出复用，而
  调用方按当前 z-order 算索引传 SetTarget——任何一次窗口切换改变 z-order 后，
  索引就指向错误的条目，预览高亮/居中错窗口。现在 SyncEntries 把 `_entries`
  重排到与传入列表严格同序（条目对象复用，DWM 缩略图注册按 HWND 原样保留），
  并用序列比较检测增/删/换位：完全不变时连罩层池重建都跳过。
- **预览顺序 ≠ 轮盘顺序**：预览数据源从扁平 `_windows`（z-order）改为
  `_items` 按轮盘遍历序展平（组内窗口原位展开，无窗口固定项不产生条目），
  轮盘相邻扇区 = 轮播相邻卡，Tab/方向键/鼠标导航方向感一致。
- **去索引映射**：`SetTarget(int)` 改 `SetTarget(IntPtr handle)`，预览侧按 HWND
  自查条目索引，跨列表索引映射这一整类 bug 从结构上消除；超 MaxThumbs 截断的
  窗口返回 false，App 收掉预览（任何卡片高亮都会误导）。

**第二批优化（刷新链路 / 缓存 / 日志 / GPU 资源）**：

- **后台刷新 diff 跳过重建**：`ItemsStructurallyEqual`（项数/固定标记/组内 HWND
  序列）没变时跳过 RebuildItems + 重选（原来每次呼出后 ~1s 必有一次全量
  BuildWheel 重建 + 闪烁）；选中窗口在刷新期间关闭时落到原索引最近存活项
  （对齐系统 Alt+Tab 位置保持）。
- **刷新合并防抖**：枚举中的请求合并为一次尾随刷新；距上次开始 <900ms 的请求
  进防抖定时器——快速开关轮盘不再每次付全量 UIA 枚举。
- **进程名直查 syscall**：`Process.GetProcessById` → OpenProcess(QUERY_LIMITED)
  + QueryFullProcessImageNameW（一次句柄 + 一次 syscall，无对象分配/访问拒绝
  异常）；`_processNameCache` 按本次枚举存活 pid 修剪（pid 复用陈旧名随进程
  退出清除；旧进程退出与新进程复用 pid 发生在同次枚举间隔内的残余边缘除外）。
- **固定项图标缓存**：`IconService.GetFileIcon` 按路径缓存（Shell COM 提取是
  BuildItems 里最贵的一步，原来每次刷新对每个未运行固定项都重做）。
- **日志常驻 StreamWriter**：AppendAllText 每行 open/ACL/write/close 一次文件，
  改 AutoFlush 常驻写入器（锁内，任意线程可写）。
- **轮盘扇区共享冻结阴影**：每扇区 new DropShadowEffect（每个占一份 GPU 资源）
  → 单个冻结静态实例全扇区共享。

验证：`--selftest-reorder`（新测试钩子：开轮盘 → t+5s 程序化激活非选中窗口
改变 z-order → t+6s 后台刷新触发预览条目重排 → t+7s 导航一次）。截图确认：
呼出时预览高亮卡 = 轮盘选中（微信）；z-order 重排 + 导航后预览高亮卡 = 新选中
（WINWORD），全程无错位。Debug/Release 0 警告。

遗留优化候选：UIA 标题事件驱动失效（WinEvent EVENT_OBJECT_NAMECHANGE）；
Alt+Tab 无条件吞键在缓存为空时导致无法切换（应回退系统行为）。

见 git log。
