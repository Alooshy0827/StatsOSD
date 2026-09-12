# StatsOSD

<img src="assets/icon.png" width="88" alt="StatsOSD icon">

**版本：v1.2.0** ｜ [下载最新版](https://github.com/Alooshy0827/StatsOSD/releases/latest) ｜ Windows 10/11 x64 ｜ [许可：专有软件](LICENSE)

透明置顶、鼠标穿透的硬件监控小面板，常驻屏幕角落，实时显示 CPU / GPU / 内存的温度、功耗、负载、频率等（类似微星小飞机的监控叠加层）。

## 功能

- **13 项可选指标**：CPU 温度/功耗/占用/频率 · GPU 温度/功耗/占用/热点/频率/显存/风扇 · 内存 已用/总量 GB
- **硬件配色**：Intel 蓝 / AMD 橙红 / NVIDIA 绿；色块包住该硬件的全部信息，块内显示型号与内存代数频率（如 `DDR5 5600`）
- **三种布局**：迷你 / 标准 / 详细，列宽随数值自适应，永不重叠
- **字体**：四档字号 + 实心描边（默认开启，背景全透明也清晰）
- **温度分级着色**：CPU 80–89° 橙 / ≥90° 红；GPU 75–82° 橙 / ≥83° 红
- **不打扰**：鼠标穿透、强制置顶、四角或自由拖动、色块不透明度 0–100%
- **开机自启**：管理员模式下创建计划任务，登录即启动且免 UAC 弹窗

> 新安装默认：右下角 · 标准布局 · 中号字体 · 色块 50% 不透明度 · 字体描边开启

## 运行

1. 解压 `StatsOSD-v1.2.0-win-x64.zip`，双击 `StatsOSD.exe`
2. 需要 **.NET 8 桌面运行时**（[下载](https://dotnet.microsoft.com/download/dotnet/8.0)）
3. 建议**以管理员身份运行**：CPU 温度与功耗需要底层传感器读取（程序会自动释放 `StatsOSD.sys`，仅用于读取硬件寄存器）

## 托盘菜单

| 组 | 项目 |
| --- | --- |
| 主操作 | 隐藏 / 显示 OSD |
| 显示与外观 | 预设面板位置 · 拖动调整位置 · 显示内容 · 布局预设 · 字体大小 · 字体描边 · 图标位置 · 透明度 |
| 开关 | 鼠标穿透 · 强制置顶 · 开机自启 |
| 工具 | 导出传感器 · 重启资源管理器 |
| 权限与退出 | 以管理员重启 · 退出 |

设置 `%APPDATA%\StatsOSD\settings.json` ｜ 日志 `%TEMP%\statsosd.log`

## 技术栈与许可

C# / WPF（.NET 8）；传感器读取由 [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 完成。

**专有软件，保留所有权利（All Rights Reserved）** —— 详见 [LICENSE](LICENSE)。

未经作者书面许可，不得复制、修改、分发或用于商业用途。
