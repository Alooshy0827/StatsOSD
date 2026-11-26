using System;
using System.Collections.Generic;
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

        /// <summary>查找 Windows 为当前程序路径保存的托盘位置记录。</summary>
        private static IEnumerable<string> FindOurKeyNames(string exe)
        {
            using (RegistryKey baseKey = Registry.CurrentUser.OpenSubKey(BasePath))
            {
                if (baseKey == null) yield break;
                foreach (string sub in baseKey.GetSubKeyNames())
                {
                    using (RegistryKey k = baseKey.OpenSubKey(sub))
                    {
                        string p = k?.GetValue(ExeValueName) as string;
                        if (p != null && string.Equals(p, exe, StringComparison.OrdinalIgnoreCase)) yield return sub;
                    }
                }
            }
        }

        /// <summary>true=常显，false=溢出；没有记录、记录不一致或无法读取时返回 null。</summary>
        public static bool? IsPromoted()
        {
            try
            {
                bool? promoted = null;
                foreach (string sub in FindOurKeyNames(Environment.ProcessPath))
                {
                    using RegistryKey k = Registry.CurrentUser.OpenSubKey(BasePath + "\\" + sub);
                    if (k == null) continue;
                    bool value = Convert.ToInt32(k.GetValue(PromotedValueName)) != 0;
                    if (promoted.HasValue && promoted.Value != value) return null;
                    promoted = value;
                }
                return promoted;
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
                bool found = false;
                foreach (string sub in FindOurKeyNames(Environment.ProcessPath))
                {
                    using RegistryKey k = Registry.CurrentUser.OpenSubKey(BasePath + "\\" + sub, true);
                    if (k == null) { message = "无法写入托盘设置项（权限不足）"; return false; }
                    k.SetValue(PromotedValueName, promoted ? 1 : 0, RegistryValueKind.DWord);
                    found = true;
                }
                if (!found)
                {
                    message = "Windows 没有可写的图标位置记录，可在任务栏设置中调整托盘图标。";
                    return false;
                }

                message = promoted
                    ? "已设为「托盘常显区」（任务栏右侧直接可见）"
                    : "已设为「托盘溢出区」（收进 ^ 里）";
                App.Log("tray placement -> promoted=" + promoted);
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
