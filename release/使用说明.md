# StatsOSD — CPU/GPU 温度 OSD（类似微星小飞机的监控叠加层）

<img src="assets/icon.png" width="96" alt="StatsOSD icon">

透明置顶、鼠标穿透的深色小面板，常驻屏幕角落，实时显示：

- CPU 温度（着色：80–89° 橙 / ≥90° 红 / 其余白）
- GPU 温度（着色：75–82° 橙 / ≥83° 红 / 其余白）
- 功耗（W，跟随在温度右侧；CPU 功耗需管理员权限才有该传感器）
- 负载（%，跟随在功耗右侧）

## 运行

双击 `StatsOSD.exe`，或命令行：

```
StatsOSD.exe
```

> **重要**：读取 CPU 温度需要访问 CPU 的 MSR/底层传感器，
> Windows 对普通进程屏蔽了这些寄存器。
> 普通权限运行时 OSD 会提示“未读取到传感器”，
> 此时右键**托盘图标** → **“以管理员身份重新启动”** 即可（仅首次或每次以管理员启动）。
> 这也是微星小飞机 / HWiNFO 等工具需要安装底层驱动的原因。

## 操作（全部通过右下角托盘图标）

菜单按用途分为 5 组（用分隔线隔开）：

| 组 | 菜单 | 作用 |
| --- | --- | --- |
| 主操作 | 隐藏 / 显示 OSD | 开关叠加层 |
| 二级界面 | 面板位置 → 四角 | 把面板钉到屏幕某个角 |
| 二级界面 | 图标位置 → 托盘常显区 / 托盘溢出区 | 小图标放在**任务栏右侧常显区**（时钟旁直接可见），还是收进 **`^` 溢出区**（默认） |
| 二级界面 | 透明度… | 弹出滑杆，实时调节面板背景不透明度（0% 纯文字 ~ 100% 全不透明） |
| 开关 | 鼠标穿透 | 面板不挡游戏鼠标操作，默认开启 |
| 开关 | 强制置顶 | 每秒重新抢占一次 Z 序，防止被其他"同样置顶"的窗口压住；默认关闭 |
| 开关 | **开机自启** | 登录 Windows 时自动启动（本组最后一项） |
| 工具 | 导出传感器 | 把本机所有可用传感器导出到 `%TEMP%\statsosd-sensors.txt`（排查"有没有功耗/每核心数据"用） |
| 工具 | 重启资源管理器 | 任务栏会闪一下；用于让"托盘常显/溢出"的设置确实生效 |
| 权限与退出 | 以管理员重启 | 一键提升权限重启，读取完整传感器（已是管理员时显示为灰色"已以管理员运行"） |
| 权限与退出 | 退出 | 退出程序（OSD 是常驻进程，不是游戏结束后自动消失） |

设置自动保存在 `%APPDATA%\StatsOSD\settings.json`，日志在 `%TEMP%\statsosd.log`。

> **关于"开机自启"**：开启时会按当前权限自动选择最合适的方式——
> - **以管理员身份运行时**（推荐）：创建计划任务 `StatsOSD`（`登录时触发` + `以最高权限运行`），
>   所以自启后**直接就带管理员权限，不会弹 UAC**，CPU 温度/功耗开箱可见。
> - **普通权限运行时**：写入注册表 `HKCU\...\CurrentVersion\Run`，自启为普通权限（CPU 温度需手动提权）。
>
> 想手动检查/删除：`schtasks /Query /TN StatsOSD`、`schtasks /Delete /TN StatsOSD /F`，
> 或注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 里的 `StatsOSD` 值。
> 移动程序位置后，程序下次启动会自动修正自启项里的路径。

> **关于"强制顶层显示"**：开启后程序每秒调用一次 `SetWindowPos(HWND_TOPMOST)` 重新抢占 Z 序，
> 实测能压住"在 OSD 之后创建的置顶窗口"（游戏启动器、录屏工具、其他监控面板等）。
> 但对**独占全屏（DirectX exclusive fullscreen）**的游戏无效——那种模式下系统只呈现游戏画面，
> 任何窗口都浮不上去（微星小飞机是靠 RTSS 注入游戏渲染管线才做到的，属于另一种技术路线）。
> 副作用：开启期间它也会盖住其他正常置顶的窗口（如某些弹窗），所以默认关闭，需要时再开。

> **关于"小图标位置"**：Windows 11 把每个托盘图标的显示位置记录在注册表
> `HKCU\Control Panel\NotifyIconSettings\<系统生成的哈希>` 的 `IsPromoted` 值里
> （`1` = 显示在任务栏右侧常显区，`0`/无该值 = 收进 `^` 溢出区）。
> 程序按 `ExecutablePath` 反查自己的条目并写入该值，然后重新注册一次托盘图标促使资源管理器重读；
> 若仍未生效，用菜单里的 **「重启资源管理器」**（任务栏会闪一下）即可。

## 命令行调试参数

```
StatsOSD.exe --shot D:\temp\panel.png    # 启动 3 秒后把面板自身渲染成 PNG 并退出（排版自检）
StatsOSD.exe --dump D:\temp\sensors.txt  # 导出全部硬件/传感器清单后退出
StatsOSD.exe --iconshot D:\temp\ico.png  # 导出实际使用的托盘图标后退出（图标自检）
StatsOSD.exe --demo --shot D:\temp\p.png # 演示模式：喂入合成数据（CPU 85 橙 / GPU 90 红）后截图
StatsOSD.exe --autostart on              # 开启开机自启（on / off / status），等价于托盘开关
StatsOSD.exe --traypos promoted          # 托盘位置（promoted / overflow / status），等价于托盘开关
```

## 构建（可选）

```
dotnet build -c Release
# 输出: bin\Release\net8.0-windows\StatsOSD.exe
```

依赖：本地 .NET 8 桌面运行时；传感器读取由 MIT/MPL 开源的
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 库完成。
