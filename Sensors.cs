using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace StatsOSD
{
    public sealed class Sample
    {
        public double? CpuTemp;
        public double? CpuLoad;
        public double? CpuPower;
        public double? CpuClock;
        public double? GpuTemp;
        public double? GpuLoad;
        public double? GpuPower;
        public double? GpuHotspot;
        public double? GpuClock;
        public double? GpuVram;
        public double? GpuFan;
        public double? MemPercent;
        public double? MemUsedGb;
        public double? MemTotalGb;
        public string Warn;

        public bool HasAny
        {
            get
            {
                return CpuTemp.HasValue || GpuTemp.HasValue || CpuLoad.HasValue || GpuLoad.HasValue
                    || CpuPower.HasValue || GpuPower.HasValue || CpuClock.HasValue || GpuHotspot.HasValue
                    || GpuClock.HasValue || GpuVram.HasValue || GpuFan.HasValue || MemPercent.HasValue;
            }
        }

        /// <summary>浅拷贝（UI 侧要在不改动采样缓存的前提下附加提示文字）</summary>
        public Sample Clone()
        {
            return (Sample)MemberwiseClone();
        }
    }

    public sealed class Sensors
    {
        private Computer _computer;
        private readonly List<IHardware> _cpus = new List<IHardware>();
        private readonly List<IHardware> _gpus = new List<IHardware>();
        private bool _opened;

        /// <summary>硬件厂商（用于分组配色）：Intel / AMD / NVIDIA / 空</summary>
        public string CpuVendor { get; private set; } = "";
        public string GpuVendor { get; private set; } = "";

        /// <summary>硬件型号（显示在色块内）</summary>
        public string CpuName { get; private set; } = "";
        public string GpuName { get; private set; } = "";
        public string MemType { get; private set; } = "";

        /// <summary>型号精简：去品牌前缀（Intel/AMD/NVIDIA）、Intel 的 "Core"、显卡结尾的 "GPU"；笔记本显卡保留 Laptop</summary>
        public static string ShortModel(string name, bool isGpu)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string s = name.Replace("(R)", "").Replace("(r)", "").Replace("(TM)", "").Replace("(tm)", "").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");

            string[] prefixes = { "Intel Core ", "Intel ", "AMD ", "NVIDIA " };
            foreach (string p in prefixes)
            {
                if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { s = s.Substring(p.Length).Trim(); break; }
            }

            if (isGpu)
            {
                if (s.StartsWith("GeForce ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(8).Trim();
                if (s.EndsWith(" GPU", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4).Trim();
            }

            return s;
        }

        // ---------- SMBIOS：内存代数与频率（如 "DDR5 5600"） ----------

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[] buffer, uint size);

        /// <summary>内存型号文本："DDR5 5600"（读 SMBIOS Type 17，无需管理员权限）</summary>
        public static string ReadMemoryType()
        {
            try
            {
                const uint RSMB = 0x52534D42;   // 'RSMB' 原始 SMBIOS 表
                uint size = GetSystemFirmwareTable(RSMB, 0, null, 0);
                if (size < 16) return "";
                var buf = new byte[size];
                uint got = GetSystemFirmwareTable(RSMB, 0, buf, size);
                if (got < 16) return "";

                int end = (int)got;
                int pos = 8;   // 前 8 字节：签名 + 表长度
                while (pos + 4 <= end)
                {
                    byte type = buf[pos];
                    byte len = buf[pos + 1];
                    if (type == 127 || len < 4) break;

                    if (type == 17 && len > 0x16 && pos + len <= end)
                    {
                        int sizeKb = buf[pos + 0x0C] | (buf[pos + 0x0D] << 8);
                        if (sizeKb != 0)   // 该插槽已安装内存
                        {
                            byte memType = buf[pos + 0x12];
                            int speed = 0;
                            if (len > 0x21) speed = buf[pos + 0x20] | (buf[pos + 0x21] << 8);   // ConfiguredMemorySpeed
                            if (speed == 0) speed = buf[pos + 0x15] | (buf[pos + 0x16] << 8);   // Speed
                            string name = MemTypeName(memType);
                            string txt = (name + " " + (speed > 0 ? speed.ToString() : "")).Trim();
                            if (txt != "") return txt;
                        }
                    }

                    // 跳过格式化区 + 字符串区（以双 0 结束）
                    int next = pos + len;
                    while (next + 1 < end && !(buf[next] == 0 && buf[next + 1] == 0)) next++;
                    pos = next + 2;
                }
            }
            catch { }
            return "";
        }

        /// <summary>SMBIOS 7.18.2 Memory Device Type（与 dmidecode 一致；0x15-0x17 为 Reserved）</summary>
        private static string MemTypeName(byte t)
        {
            switch (t)
            {
                case 0x12: return "DDR";
                case 0x13: return "DDR2";
                case 0x14: return "DDR2 FB-DIMM";
                case 0x18: return "DDR3";
                case 0x19: return "FBD2";
                case 0x1A: return "DDR4";
                case 0x1B: return "LPDDR";
                case 0x1C: return "LPDDR2";
                case 0x1D: return "LPDDR3";
                case 0x1E: return "LPDDR4";
                case 0x1F: return "NVDIMM";
                case 0x20: return "HBM";
                case 0x21: return "HBM2";
                case 0x22: return "DDR5";
                case 0x23: return "LPDDR5";
                case 0x24: return "HBM3";
                default: return "";
            }
        }

        private static string DetectCpuVendor(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            if (name.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0) return "Intel";
            if (name.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0) return "AMD";
            return "";
        }

        public void Open()
        {
            try
            {
                var c = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = false,
                    IsMotherboardEnabled = false,
                    IsControllerEnabled = false,
                    IsNetworkEnabled = false,
                    IsStorageEnabled = false
                };
                c.Open();
                _computer = c;
                RefreshAll();

                foreach (IHardware hw in _computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.Cpu)
                        _cpus.Add(hw);
                    else if (hw.HardwareType == HardwareType.GpuNvidia ||
                             hw.HardwareType == HardwareType.GpuAmd ||
                             hw.HardwareType == HardwareType.GpuIntel)
                        _gpus.Add(hw);
                }
                _opened = true;
                CpuVendor = _cpus.Count > 0 ? DetectCpuVendor(_cpus[0].Name) : "";
                CpuName = _cpus.Count > 0 ? _cpus[0].Name : "";
                MemType = ReadMemoryType();
                if (_gpus.Count > 0)
                {
                    HardwareType t = _gpus[0].HardwareType;
                    GpuVendor = t == HardwareType.GpuNvidia ? "NVIDIA"
                              : t == HardwareType.GpuAmd ? "AMD"
                              : t == HardwareType.GpuIntel ? "Intel" : "";
                    GpuName = _gpus[0].Name;
                }
                App.Log("sensors open ok: cpus=" + _cpus.Count + ", gpus=" + _gpus.Count
                        + ", cpuVendor=" + (CpuVendor == "" ? "?" : CpuVendor)
                        + ", gpuVendor=" + (GpuVendor == "" ? "?" : GpuVendor)
                        + ", mem=" + (MemType == "" ? "?" : MemType));
            }
            catch (Exception ex)
            {
                App.Log("sensors open error: " + ex.Message);
            }
        }

        public Sample Sample()
        {
            var r = new Sample();
            if (!_opened || _computer == null)
            {
                r.Warn = "传感器初始化中…";
                return r;
            }

            try { RefreshAll(); }
            catch (Exception ex)
            {
                r.Warn = "读取失败：" + ex.Message;
                return r;
            }

            foreach (IHardware cpu in _cpus)
            {
                if (r.CpuTemp == null) r.CpuTemp = PickTemp(cpu, "CPU");
                if (r.CpuLoad == null) r.CpuLoad = PickLoad(cpu, "CPU");
                if (r.CpuPower == null) r.CpuPower = PickPower(cpu, "CPU");
                if (r.CpuClock == null) r.CpuClock = PickClock(cpu, "CPU");
                if (r.CpuTemp.HasValue && r.CpuLoad.HasValue && r.CpuPower.HasValue && r.CpuClock.HasValue) break;
            }

            foreach (IHardware gpu in _gpus)
            {
                if (r.GpuTemp == null) r.GpuTemp = PickTemp(gpu, "GPU");
                if (r.GpuLoad == null) r.GpuLoad = PickLoad(gpu, "GPU");
                if (r.GpuPower == null) r.GpuPower = PickPower(gpu, "GPU");
                if (r.GpuHotspot == null) r.GpuHotspot = PickNamedTemp(gpu, "Hot Spot");
                if (r.GpuClock == null) r.GpuClock = PickClock(gpu, "GPU");
                if (r.GpuVram == null) r.GpuVram = PickSmallData(gpu, "GPU Memory Used");
                if (r.GpuFan == null) r.GpuFan = PickLoad(gpu, "Fan", "Fan");
                if (r.GpuTemp.HasValue && r.GpuLoad.HasValue && r.GpuPower.HasValue
                    && r.GpuHotspot.HasValue && r.GpuClock.HasValue && r.GpuVram.HasValue) break;
            }

            double?[] mem = ReadMemory();
            r.MemPercent = mem[0];
            r.MemUsedGb = mem[1];
            r.MemTotalGb = mem[2];

            if (!r.HasAny)
            {
                r.Warn = App.IsAdmin()
                    ? "等待传感器数据…"
                    : "未读到传感器：请以管理员身份重启";
            }
            else if (!r.CpuTemp.HasValue && !App.IsAdmin())
            {
                r.Warn = "CPU 温度需管理员权限（托盘重启）";
            }

            return r;
        }

        // ---------- 刷新 ----------

        private void RefreshAll()
        {
            foreach (IHardware hw in _computer.Hardware)
                RefreshHardware(hw);
        }

        private void RefreshHardware(IHardware hw)
        {
            hw.Update();
            foreach (IHardware sub in hw.SubHardware)
                RefreshHardware(sub);
        }

        // ---------- 选择逻辑 ----------

        private static double? PickTemp(IHardware hw, string kind)
        {
            List<ISensor> temps = hw.Sensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue)
                .ToList();
            if (temps.Count == 0) return null;

            if (kind == "CPU")
            {
                foreach (string pri in new[] { "Package", "Tctl", "Average", "Core Max" })
                {
                    ISensor hit = temps.FirstOrDefault(s => s.Name.Contains(pri, StringComparison.OrdinalIgnoreCase));
                    if (hit != null) return hit.Value;
                }
                // Intel：无 Package 时取最高核心温度
                var cores = temps.Where(s => s.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase)
                                             && !s.Name.Contains("Max", StringComparison.OrdinalIgnoreCase)
                                             && !s.Name.Contains("Average", StringComparison.OrdinalIgnoreCase)).ToList();
                if (cores.Count > 0) return cores.Max(s => s.Value.Value);
                return temps.Max(s => s.Value.Value);
            }
            else
            {
                foreach (string pri in new[] { "GPU Core", "GPU" })
                {
                    ISensor hit = temps.FirstOrDefault(s => s.Name.Contains(pri, StringComparison.OrdinalIgnoreCase));
                    if (hit != null) return hit.Value;
                }
                return temps.Max(s => s.Value.Value);
            }
        }

        private static double? PickPower(IHardware hw, string kind)
        {
            List<ISensor> powers = hw.Sensors
                .Where(s => s.SensorType == SensorType.Power && s.Value.HasValue)
                .ToList();
            if (powers.Count == 0) return null;

            string[] names = kind == "CPU"
                ? new[] { "Package", "CPU Package", "Total", "Cores" }
                : new[] { "GPU Power", "GPU", "Package", "Total" };

            foreach (string pri in names)
            {
                ISensor hit = powers.FirstOrDefault(s => s.Name.Contains(pri, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit.Value;
            }
            return powers.Max(s => s.Value.Value);
        }

        /// <summary>按名称取温度（如 GPU "Hot Spot"）</summary>
        private static double? PickNamedTemp(IHardware hw, string namePart)
        {
            ISensor hit = hw.Sensors.FirstOrDefault(s =>
                s.SensorType == SensorType.Temperature && s.Value.HasValue &&
                s.Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0);
            return hit?.Value;
        }

        /// <summary>取频率（MHz）：CPU 优先 "CPU Core"/"Core #"，GPU 优先 "GPU Core"</summary>
        private static double? PickClock(IHardware hw, string kind)
        {
            List<ISensor> clocks = hw.Sensors
                .Where(s => s.SensorType == SensorType.Clock && s.Value.HasValue)
                .ToList();
            if (clocks.Count == 0) return null;

            string[] names = kind == "CPU"
                ? new[] { "CPU Core", "Cores", "Core #", "Core" }
                : new[] { "GPU Core", "GPU", "Core" };

            foreach (string pri in names)
            {
                ISensor hit = clocks.FirstOrDefault(s => s.Name.IndexOf(pri, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null) return hit.Value;
            }
            return clocks.Max(s => s.Value.Value);
        }

        /// <summary>按名称取 SmallData（如显存占用 "GPU Memory Used"，单位 MB）</summary>
        private static double? PickSmallData(IHardware hw, string namePart)
        {
            ISensor hit = hw.Sensors.FirstOrDefault(s =>
                s.SensorType == SensorType.SmallData && s.Value.HasValue &&
                s.Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0);
            return hit?.Value;
        }

        /// <summary>按名称取占用率（如 GPU 风扇 "GPU Fan"）</summary>
        private static double? PickLoad(IHardware hw, string kind, string namePart)
        {
            ISensor hit = hw.Sensors.FirstOrDefault(s =>
                s.SensorType == SensorType.Load && s.Value.HasValue &&
                s.Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0);
            return hit?.Value;
        }

        // ---------- 内存（Win32 API，比走 LHM 更快更准） ----------

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(MEMORYSTATUSEX lpBuffer);

        /// <summary>返回 [占用百分比, 已用 GB, 总量 GB]</summary>
        private static double?[] ReadMemory()
        {
            try
            {
                var st = new MEMORYSTATUSEX();
                if (!GlobalMemoryStatusEx(st)) return new double?[] { null, null, null };
                double totalGb = st.ullTotalPhys / 1024.0 / 1024.0 / 1024.0;
                double usedGb = (st.ullTotalPhys - st.ullAvailPhys) / 1024.0 / 1024.0 / 1024.0;
                return new double?[] { st.dwMemoryLoad, Math.Round(usedGb, 1), Math.Round(totalGb, 1) };
            }
            catch
            {
                return new double?[] { null, null, null };
            }
        }

        /// <summary>导出全部硬件与传感器（排查"有没有功耗/每核心数据"用）</summary>
        public string DumpAll()
        {
            var sb = new System.Text.StringBuilder();
            if (_computer == null) return "(未初始化)";
            try { RefreshAll(); } catch { }
            sb.AppendLine("admin=" + App.IsAdmin());
            foreach (IHardware hw in _computer.Hardware) DumpHardware(hw, 0, sb);
            return sb.ToString();
        }

        private static void DumpHardware(IHardware hw, int depth, System.Text.StringBuilder sb)
        {
            string pad = new string(' ', depth * 2);
            sb.AppendLine(pad + "[" + hw.HardwareType + "] " + hw.Name);
            foreach (ISensor s in hw.Sensors)
            {
                string val = s.Value.HasValue ? s.Value.Value.ToString("0.##") : "n/a";
                sb.AppendLine(pad + "   " + s.SensorType + " / " + s.Name + " = " + val);
            }
            foreach (IHardware sub in hw.SubHardware) DumpHardware(sub, depth + 1, sb);
        }

        private static double? PickLoad(IHardware hw, string kind)
        {
            List<ISensor> loads = hw.Sensors
                .Where(s => s.SensorType == SensorType.Load && s.Value.HasValue)
                .ToList();
            if (loads.Count == 0) return null;

            string[] names = kind == "CPU"
                ? new[] { "Total", "Max", "Core" }
                : new[] { "GPU Core", "GPU", "Core" };

            foreach (string pri in names)
            {
                ISensor hit = loads.FirstOrDefault(s => s.Name.Contains(pri, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit.Value;
            }
            return loads.Max(s => s.Value.Value);
        }

        public void Close()
        {
            try { _computer?.Close(); } catch (Exception ex) { App.Log("close error: " + ex.Message); }
            _computer = null;
            _cpus.Clear();
            _gpus.Clear();
        }
    }
}
