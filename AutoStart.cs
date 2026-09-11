using System;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace StatsOSD
{
    /// <summary>
    /// 开机自启管理。
    /// 优先级：管理员权限下建"登录时以最高权限运行"的计划任务（自启即带管理员权限，免 UAC 弹窗）；
    /// 否则退回注册表 HKCU\...\Run 项（普通权限启动）。
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "StatsOSD";
        private const string TaskName = "StatsOSD";

        public static bool IsEnabled()
        {
            try
            {
                if (ReadRunValue() != null) return true;
            }
            catch (Exception ex) { App.Log("autostart: 读取 Run 项失败 " + ex.Message); }
            return TaskExists();
        }

        /// <summary>开启自启，how 返回实际采用的方式描述</summary>
        public static bool Enable(out string how)
        {
            how = "";
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) { how = "无法获取程序路径"; return false; }

            if (App.IsAdmin() && CreateTask(exe))
            {
                RemoveRunValue();
                how = "计划任务 · 登录时以管理员权限启动（免 UAC）";
                return true;
            }

            if (WriteRunValue(exe))
            {
                how = App.IsAdmin()
                    ? "注册表 Run 项（计划任务创建失败，已退回；自启为普通权限）"
                    : "注册表 Run 项（自启为普通权限，CPU 温度需手动提权）";
                return true;
            }

            how = "写入自启项失败";
            return false;
        }

        public static bool Disable(out string how)
        {
            bool removedRun = RemoveRunValue();
            bool removedTask = DeleteTask();
            how = (removedRun || removedTask) ? "已移除自启项" : "原本就没有自启项";
            return true;
        }

        /// <summary>程序路径变化（比如移动了文件夹）时自动修正自启项</summary>
        public static void Sync()
        {
            try
            {
                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;

                string runValue = ReadRunValue();
                if (runValue != null && !string.Equals(runValue.Trim('"'), exe, StringComparison.OrdinalIgnoreCase))
                {
                    App.Log("autostart: Run 项路径已过期，正在修正 -> " + exe);
                    Enable(out _);
                    return;
                }

                if (TaskExists() && App.IsAdmin() && !TaskPointsTo(exe))
                {
                    App.Log("autostart: 计划任务路径已过期，正在重建 -> " + exe);
                    Enable(out _);
                }
            }
            catch (Exception ex) { App.Log("autostart sync error: " + ex.Message); }
        }

        // ---------- 注册表 Run 项 ----------

        private static string ReadRunValue()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            {
                return k?.GetValue(ValueName) as string;
            }
        }

        private static bool WriteRunValue(string exe)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (k == null) return false;
                    k.SetValue(ValueName, "\"" + exe + "\"", RegistryValueKind.String);
                }
                App.Log("autostart: Run 项已写入 -> " + exe);
                return true;
            }
            catch (Exception ex)
            {
                App.Log("autostart: 写 Run 项失败 " + ex.Message);
                return false;
            }
        }

        private static bool RemoveRunValue()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k?.GetValue(ValueName) == null) return false;
                    k.DeleteValue(ValueName, false);
                }
                App.Log("autostart: Run 项已删除");
                return true;
            }
            catch (Exception ex)
            {
                App.Log("autostart: 删 Run 项失败 " + ex.Message);
                return false;
            }
        }

        // ---------- 计划任务 ----------

        private static bool TaskExists()
        {
            return RunSchtasks("/Query", "/TN", TaskName) == 0;
        }

        private static bool TaskPointsTo(string exe)
        {
            string xml = RunSchtasksCapture("/Query", "/TN", TaskName, "/XML");
            return xml != null && xml.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool CreateTask(string exe)
        {
            int code = RunSchtasks(
                "/Create", "/TN", TaskName,
                "/TR", "\"" + exe + "\"",
                "/SC", "ONLOGON",
                "/RL", "HIGHEST",
                "/F");
            if (code == 0) App.Log("autostart: 计划任务已创建 -> " + exe);
            else App.Log("autostart: 创建计划任务失败，退出码 " + code);
            return code == 0;
        }

        private static bool DeleteTask()
        {
            int code = RunSchtasks("/Delete", "/TN", TaskName, "/F");
            if (code == 0) App.Log("autostart: 计划任务已删除");
            return code == 0;
        }

        private static int RunSchtasks(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (string a in args) psi.ArgumentList.Add(a);

                using (Process p = Process.Start(psi))
                {
                    if (p == null) return -1;
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                        return -1;
                    }
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                App.Log("autostart: schtasks 调用失败 " + ex.Message);
                return -1;
            }
        }

        private static string RunSchtasksCapture(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                foreach (string a in args) psi.ArgumentList.Add(a);

                using (Process p = Process.Start(psi))
                {
                    if (p == null) return null;
                    string output = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return p.ExitCode == 0 ? output : null;
                }
            }
            catch (Exception ex)
            {
                App.Log("autostart: schtasks 读取失败 " + ex.Message);
                return null;
            }
        }
    }
}
