using System;
using Microsoft.Win32;

namespace StatsOSD
{
    /// <summary>
    /// 托盘图标"常显区 / 溢出区"管理。
    ///
    /// Windows 11 把每个托盘图标的显示位置记录在：
    ///   HKCU\Control Panel\NotifyIconSettings\&lt;系统生成的哈希&gt;
    /// 其中
    ///   IsPromoted = 1  → 显示在任务栏右侧常显区（时钟旁边那几个）
    ///   IsPromoted = 0 或该值不存在 → 收进 ^ 溢出区
    ///
    /// 键名是系统生成的哈希、无法直接推算，因此只能用 ExecutablePath 反查自己的条目。
    /// </summary>
    internal static class TrayPlacement
    {
        private const string BasePath = @"Control Panel\NotifyIconSettings";
        private const string ExeValueName = "ExecutablePath";
        private const string PromotedValueName = "IsPromoted";

        /// <summary>反查本程序对应的托盘条目名；null = 系统还没登记（图标从未出现过）</summary>
        private static string FindOurKeyName(string exe)
        {
            using (RegistryKey baseKey = Registry.CurrentUser.OpenSubKey(BasePath))
            {
                if (baseKey == null) return null;
                foreach (string sub in baseKey.GetSubKeyNames())
                {
                    using (RegistryKey k = baseKey.OpenSubKey(sub))
                    {
                        string p = k?.GetValue(ExeValueName) as string;
                        if (p != null && string.Equals(p, exe, StringComparison.OrdinalIgnoreCase)) return sub;
                    }
                }
            }
            return null;
        }

        /// <summary>true=常显，false=溢出；null=系统尚未登记该图标</summary>
        public static bool? IsPromoted()
        {
            try
            {
                string sub = FindOurKeyName(Environment.ProcessPath);
                if (sub == null) return null;
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(BasePath + "\\" + sub))
                {
                    object v = k?.GetValue(PromotedValueName);
                    if (v == null) return false;                    // 值缺失 = 未常显
                    return Convert.ToInt32(v) != 0;
                }
            }
            catch (Exception ex)
            {
                App.Log("tray placement read error: " + ex.Message);
                return null;
            }
        }

        /// <summary>设置常显/溢出</summary>
        public static bool SetPromoted(bool promoted, out string message)
        {
            try
            {
                string sub = FindOurKeyName(Environment.ProcessPath);
                if (sub == null)
                {
                    message = "系统还没登记本程序的托盘图标，请先重启一次本程序再设置";
                    return false;
                }

                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(BasePath + "\\" + sub, true))
                {
                    if (k == null) { message = "无法写入托盘设置项（权限不足）"; return false; }
                    k.SetValue(PromotedValueName, promoted ? 1 : 0, RegistryValueKind.DWord);
                }

                message = promoted
                    ? "已设为「托盘常显区」（任务栏右侧直接可见）"
                    : "已设为「托盘溢出区」（收进 ^ 里）";
                App.Log("tray placement -> promoted=" + promoted + " (key " + sub + ")");
                return true;
            }
            catch (Exception ex)
            {
                message = "写入失败：" + ex.Message;
                App.Log("tray placement write error: " + ex.Message);
                return false;
            }
        }
    }
}
