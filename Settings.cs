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
        public bool ShowDesktopOverlay { get; set; } = true;
        public bool ShowTaskbarReadings { get; set; }
        public int BackgroundAlpha { get; set; } = 50; // 0-100：色块不透明度（%）；新安装默认 50%
        public bool ForceTopmost { get; set; } = false; // 强制顶层：定时重新抢占 Z 序
        public string PositionMode { get; set; } = "corner"; // corner=贴角 | free=自由位置
        public int FreeX { get; set; } = -1;           // 自由位置 X（DIP，副屏坐标可以为负）
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

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static readonly object FileLock = new();
        internal event Action<string> SaveFailed;

        public static Settings Load()
        {
            lock (FileLock) return LoadFromDisk();
        }

        private static Settings LoadFromDisk()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var s = JsonSerializer.Deserialize<Settings>(reader.ReadToEnd());
                    if (s != null) return s;
                }
            }
            catch (Exception ex)
            {
                App.Log("settings load error: " + ex.Message);
            }
            // 新安装使用当前预设。已有配置缺少的字段仍沿用上面定义的旧默认值。
            return new Settings
            {
                ShowDesktopOverlay = false,
                ShowTaskbarReadings = true,
                FreeX = 1229,
                FreeY = 710,
                Metrics = new List<string>
                {
                    "cpu.temp", "cpu.power", "gpu.temp", "gpu.power", "mem.used", "cpu.load", "gpu.load"
                },
                LayoutPreset = "mini",
                FontScale = 1.2
            };
        }

        public void Save()
        {
            string error;
            lock (FileLock) error = SaveAtomically();
            if (error != null) SaveFailed?.Invoke(error);
        }

        private string SaveAtomically()
        {
            string temporary = null;
            try
            {
                Directory.CreateDirectory(DirPath);
                string candidate = Path.Combine(DirPath, Path.GetRandomFileName());
                using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    temporary = candidate;
                    JsonSerializer.Serialize(stream, this, JsonOptions);
                }
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.Move(temporary, FilePath, overwrite: true);
                        break;
                    }
                    catch (Exception ex) when (attempt < 2 && (ex.HResult & 0xffff) is 5 or 32)
                    {
                        // Windows file watchers may briefly hold the old file without delete sharing.
                        System.Threading.Thread.Sleep(15);
                    }
                }
                temporary = null;
                return null;
            }
            catch (Exception ex)
            {
                App.Log("settings save error: " + ex.Message);
                return ex.Message;
            }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException ex) { App.Log("settings cleanup: " + ex.Message); }
                    catch (UnauthorizedAccessException ex) { App.Log("settings cleanup: " + ex.Message); }
                }
            }
        }
    }
}
