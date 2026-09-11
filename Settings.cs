using System;
using System.IO;
using System.Text.Json;

namespace StatsOSD
{
    public sealed class Settings
    {
        public int Corner { get; set; } = 1;          // 0左上 1右上 2左下 3右下
        public bool ClickThrough { get; set; } = true;
        public int BackgroundAlpha { get; set; } = 90; // 0-100：面板背景不透明度（%）
        public bool ForceTopmost { get; set; } = false; // 强制顶层：定时重新抢占 Z 序
        public bool ShowInTaskbar { get; set; } = false; // 小图标位置：true=任务栏，false=仅托盘

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
