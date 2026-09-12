using System;
using System.Collections.Generic;
using System.Linq;

namespace StatsOSD
{
    /// <summary>指标着色类型：决定用哪套温度阈值</summary>
    public enum MetricKind
    {
        Normal,      // 用指标自带颜色
        CpuTemp,     // CPU 阈值：80-89 橙、≥90 红
        GpuTemp      // GPU 阈值：75-82 橙、≥83 红
    }

    /// <summary>指标显示样式：Primary 用大字号（通常是温度），Secondary 用小字号</summary>
    public enum MetricStyle
    {
        Primary,
        Secondary
    }

    public sealed class MetricDef
    {
        public string Id;          // 稳定 ID（配置里保存这个）
        public string Group;       // 分组（行标签，如 CPU / GPU / 内存）
        public string Name;        // 完整名称（详细布局用，如 "CPU 温度"）
        public string Suffix;      // 单位后缀
        public int Decimals;       // 小数位
        public string ColorHex;    // Normal 类型的显示颜色
        public MetricKind Kind;
        public MetricStyle Style;
        public Func<Sample, double?> Get;

        /// <summary>可选：自定义显示文本（如 "19.7/32.0"）；为空则按数值+小数位格式化</summary>
        public Func<Sample, string> Text;
    }

    /// <summary>
    /// 指标注册表：所有可显示的数据项都在这里登记。
    /// 新增指标 = 在 Sensors 里取值 + 在下面加一行，其它代码（渲染、配置、界面）无需改动。
    /// </summary>
    public static class Metrics
    {
        private const string CValue = "#FFE8E8F0";   // 白（温度基准色）
        private const string CPower = "#FFD8B36A";   // 琥珀（功耗）
        private const string CLoad1 = "#FF9AD4FF";   // 浅蓝（CPU 占用）
        private const string CLoad2 = "#FFA7F3B0";   // 浅绿（GPU 占用）
        private const string CMisc = "#FFC9CFDA";    // 浅灰（其余）

        public static readonly List<MetricDef> All = new List<MetricDef>
        {
            // ---- CPU ----
            M("cpu.temp",    "CPU",  "CPU 温度", "°C",  0, CValue, MetricKind.CpuTemp, MetricStyle.Primary,   s => s.CpuTemp),
            M("cpu.power",   "CPU",  "CPU 功耗", "W",   0, CPower, MetricKind.Normal,  MetricStyle.Secondary, s => s.CpuPower),
            M("cpu.load",    "CPU",  "CPU 占用", "%",   0, CLoad1, MetricKind.Normal,  MetricStyle.Secondary, s => s.CpuLoad),
            M("cpu.clock",   "CPU",  "CPU 频率", "MHz", 0, CMisc,  MetricKind.Normal,  MetricStyle.Secondary, s => s.CpuClock),

            // ---- GPU ----
            M("gpu.temp",    "GPU",  "GPU 温度", "°C",  0, CValue, MetricKind.GpuTemp, MetricStyle.Primary,   s => s.GpuTemp),
            M("gpu.power",   "GPU",  "GPU 功耗", "W",   0, CPower, MetricKind.Normal,  MetricStyle.Secondary, s => s.GpuPower),
            M("gpu.load",    "GPU",  "GPU 占用", "%",   0, CLoad2, MetricKind.Normal,  MetricStyle.Secondary, s => s.GpuLoad),
            M("gpu.hotspot", "GPU",  "GPU 热点", "°C",  0, CMisc,  MetricKind.GpuTemp, MetricStyle.Secondary, s => s.GpuHotspot),
            M("gpu.clock",   "GPU",  "GPU 频率", "MHz", 0, CMisc,  MetricKind.Normal,  MetricStyle.Secondary, s => s.GpuClock),
            M("gpu.vram",    "GPU",  "显存占用", "MB",  0, CMisc,  MetricKind.Normal,  MetricStyle.Secondary, s => s.GpuVram),
            M("gpu.fan",     "GPU",  "GPU 风扇", "%",   0, CMisc,  MetricKind.Normal,  MetricStyle.Secondary, s => s.GpuFan),

            // ---- 内存（分组名 Mem）----
            M("mem.used",    "Mem",  "内存 已用/总量", "GB", 1, CMisc, MetricKind.Normal, MetricStyle.Primary,   s => s.MemUsedGb, MemUsedTotal),
            M("mem.percent", "Mem",  "内存占用率",     "%",  0, CMisc, MetricKind.Normal, MetricStyle.Secondary, s => s.MemPercent)
        };

        private static MetricDef M(string id, string group, string name, string suffix, int decimals,
                                   string colorHex, MetricKind kind, MetricStyle style, Func<Sample, double?> get,
                                   Func<Sample, string> text = null)
        {
            return new MetricDef
            {
                Id = id,
                Group = group,
                Name = name,
                Suffix = suffix,
                Decimals = decimals,
                ColorHex = colorHex,
                Kind = kind,
                Style = style,
                Get = get,
                Text = text
            };
        }

        /// <summary>"已用/总量" 形式（如 19.7/32.0）</summary>
        private static string MemUsedTotal(Sample s)
        {
            if (!s.MemUsedGb.HasValue) return "--";
            if (!s.MemTotalGb.HasValue) return s.MemUsedGb.Value.ToString("0.#");
            return s.MemUsedGb.Value.ToString("0.#") + "/" + s.MemTotalGb.Value.ToString("0.#");
        }

        public static MetricDef ById(string id)
        {
            return All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>默认显示项（CPU/GPU 温度功耗负载 + 内存 已用/总量）</summary>
        public static List<string> DefaultSelection()
        {
            return new List<string> { "cpu.temp", "cpu.power", "cpu.load", "gpu.temp", "gpu.power", "gpu.load", "mem.used" };
        }

        /// <summary>按配置里的顺序取出已启用指标（无效 ID 自动忽略）</summary>
        public static List<MetricDef> Resolve(List<string> ids)
        {
            var list = new List<MetricDef>();
            if (ids == null || ids.Count == 0) ids = DefaultSelection();
            foreach (string id in ids)
            {
                MetricDef d = ById(id);
                if (d != null && !list.Contains(d)) list.Add(d);
            }
            if (list.Count == 0) list = Resolve(DefaultSelection());
            return list;
        }

        /// <summary>格式化数值（含小数位），无数据返回 "--"</summary>
        public static string Format(MetricDef def, double? value)
        {
            if (!value.HasValue) return "--";
            return value.Value.ToString(def.Decimals == 0 ? "0" : "0.#");
        }
    }
}
