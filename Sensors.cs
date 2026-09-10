using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace CpuHud
{
    public sealed class CoreTemp
    {
        public string Name;
        public double Temp;
    }

    public sealed class Sample
    {
        public double? CpuTemp;
        public double? CpuLoad;
        public double? GpuTemp;
        public double? GpuLoad;
        public List<CoreTemp> Cores = new List<CoreTemp>();
        public string Warn;

        public bool HasAny
        {
            get { return CpuTemp.HasValue || GpuTemp.HasValue || CpuLoad.HasValue || GpuLoad.HasValue; }
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
                if (r.Cores.Count == 0) r.Cores.AddRange(PickCores(cpu, 24));
                if (r.CpuTemp.HasValue && r.CpuLoad.HasValue) break;
            }

            foreach (IHardware gpu in _gpus)
            {
                if (r.GpuTemp == null) r.GpuTemp = PickTemp(gpu, "GPU");
                if (r.GpuLoad == null) r.GpuLoad = PickLoad(gpu, "GPU");
                if (r.GpuTemp.HasValue && r.GpuLoad.HasValue) break;
            }

            if (!r.HasAny)
            {
                r.Warn = App.IsAdmin()
                    ? "等待传感器数据…（若长时间无数据，说明主板/CPU 不支持直接读取）"
                    : "未读取到传感器：请右键托盘图标 → 以管理员身份重新启动（读取温度需要底层驱动）";
            }
            else if (!r.CpuTemp.HasValue && !r.Cores.Any() && !App.IsAdmin())
            {
                r.Warn = "CPU 温度需要管理员权限（托盘菜单可一键重启）";
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

        private static IEnumerable<CoreTemp> PickCores(IHardware hw, int maxCount)
        {
            var list = hw.Sensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue)
                .Where(s => s.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
                .Where(s => !s.Name.Contains("Max", StringComparison.OrdinalIgnoreCase))
                .Where(s => !s.Name.Contains("Average", StringComparison.OrdinalIgnoreCase))
                .Where(s => !s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                .Select(s => new CoreTemp { Name = s.Name.Replace("#", "C"), Temp = s.Value.Value })
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Take(maxCount)
                .ToList();
            return list;
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
