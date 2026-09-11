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
        public double? GpuTemp;
        public double? GpuLoad;
        public double? GpuPower;
        public string Warn;

        public bool HasAny
        {
            get { return CpuTemp.HasValue || GpuTemp.HasValue || CpuLoad.HasValue || GpuLoad.HasValue || CpuPower.HasValue || GpuPower.HasValue; }
        }
    }

    public sealed class Sensors
    {
        private Computer _computer;
        private readonly List<IHardware> _cpus = new List<IHardware>();
        private readonly List<IHardware> _gpus = new List<IHardware>();
        private bool _opened;

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
                App.Log("sensors open ok: cpus=" + _cpus.Count + ", gpus=" + _gpus.Count);
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
                if (r.CpuTemp.HasValue && r.CpuLoad.HasValue && r.CpuPower.HasValue) break;
            }

            foreach (IHardware gpu in _gpus)
            {
                if (r.GpuTemp == null) r.GpuTemp = PickTemp(gpu, "GPU");
                if (r.GpuLoad == null) r.GpuLoad = PickLoad(gpu, "GPU");
                if (r.GpuPower == null) r.GpuPower = PickPower(gpu, "GPU");
                if (r.GpuTemp.HasValue && r.GpuLoad.HasValue && r.GpuPower.HasValue) break;
            }

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
