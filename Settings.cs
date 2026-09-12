using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace StatsOSD
{
    public sealed class Settings
    {
        public int Corner { get; set; } = 3;          // 0左上 1右上 2左下 3右下（新安装默认右下角）
        public bool ClickThrough { get; set; } = true;
        public int BackgroundAlpha { get; set; } = 50; // 0-100：色块不透明度（%）；新安装默认 50%
        public bool ForceTopmost { get; set; } = false; // 强制顶层：定时重新抢占 Z 序
        public string PositionMode { get; set; } = "corner"; // corner=贴角 | free=自由位置
        public int FreeX { get; set; } = -1;           // 自由位置 X（DIP，-1 表示未设置）
        public int FreeY { get; set; } = -1;           // 自由位置 Y

        // ---- 显示内容与外观（阶段 A：内容 / 形式 / 字体 均可配置）----
        public List<string> Metrics { get; set; } = new List<string>();  // 启用的指标 ID（顺序即显示顺序）；空 = 默认
        public string LayoutPreset { get; set; } = "standard";           // mini | standard | detailed
        public double FontScale { get; set; } = 1.0;                     // 全局字号缩放
        public string FontFamily { get; set; } = "Consolas";             // 全局字体
        public bool TextOutline { get; set; } = true;                    // 字体描边（透明背景下提升可读性，默认开）

        private static string DirPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StatsOSD"); }
        }

        private static string FilePath
        {
            get { return Path.Combine(DirPath, "settings.json"); }
        }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                    if (s != null) return s;
                }
            }
            catch (Exception ex)
            {
                App.Log("settings load error: " + ex.Message);
            }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DirPath);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                App.Log("settings save error: " + ex.Message);
            }
        }
    }
}
