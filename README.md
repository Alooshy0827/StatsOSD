# StatsOSD — CPU/GPU 温度 OSD（类似微星小飞机的监控叠加层）

<img src="assets/icon.png" width="96" alt="StatsOSD icon">

透明置顶、鼠标穿透的深色小面板，常驻屏幕角落，实时显示：

- CPU 温度（着色：≥90° 红 / ≥75° 橙 / 正常白）
- CPU 占用率、CPU 功耗（W，需管理员权限才有该传感器）
- GPU 温度、占用率、功耗（W）
- 每核心温度（可关闭，最多显示 12 个核心）

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

| 菜单 | 作用 |
| --- | --- |
| 隐藏 / 显示 OSD | 开关叠加层 |
| 位置 → 四角 | 把面板钉到屏幕某个角 |
| 鼠标穿透（勾选） | 面板不挡游戏鼠标操作，默认开启 |
| 显示每核心温度（勾选） | 是否显示各核心温度行 |
| 背景透明度… | 弹出滑杆，实时调节面板背景不透明度（0% 纯文字 ~ 100% 全不透明） |
| 导出传感器清单 | 把本机所有可用传感器导出到 `%TEMP%\statsosd-sensors.txt`（排查"有没有功耗/每核心数据"用） |
| 以管理员身份重新启动 | 一键提升权限重启，读取完整传感器 |
| 退出 | 退出程序（OSD 是常驻进程，不是游戏结束后自动消失） |

设置自动保存在 `%APPDATA%\StatsOSD\settings.json`，日志在 `%TEMP%\statsosd.log`。

## 命令行调试参数

```
StatsOSD.exe --shot D:\temp\panel.png    # 启动 3 秒后把面板自身渲染成 PNG 并退出（排版自检）
StatsOSD.exe --dump D:\temp\sensors.txt  # 导出全部硬件/传感器清单后退出
StatsOSD.exe --iconshot D:\temp\ico.png  # 导出实际使用的托盘图标后退出（图标自检）
```

## 构建（可选）

```
dotnet build -c Release
# 输出: bin\Release\net8.0-windows\StatsOSD.exe
```

依赖：本地 .NET 8 桌面运行时；传感器读取由 MIT/MPL 开源的
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 库完成。
