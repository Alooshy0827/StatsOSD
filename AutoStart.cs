using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;

namespace StatsOSD
{
    /// <summary>
    /// 开机自启管理。
    /// 使用最高权限的登录计划任务；兼容旧版本的注册表 Run 项。
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "StatsOSD";
        private const string TaskName = "StatsOSD";

        public static bool? ReadState(out string error)
        {
            error = null;
            try
            {
                if (ReadRunValue() != null) return true;
            }
            catch (Exception ex) { error = "读取登录启动项失败：" + ex.Message; }
            try
            {
                if (ReadTaskEnabled() == true) { error = null; return true; }
            }
            catch (Exception ex) { error = "读取计划任务失败：" + ex.GetBaseException().Message; }
            if (error == null) return false;
            App.Log("autostart: " + error);
            return null;
        }

        /// <summary>开启自启，how 返回实际采用的方式描述</summary>
        public static bool Enable(out string how)
        {
            how = "";
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) { how = "无法获取程序路径"; return false; }

            if (CreateTask(exe, out string detail))
            {
                if (!RemoveRunValue())
                {
                    how = "管理员登录计划任务已启用，旧版登录启动项清理失败";
                    return false;
                }
                how = "计划任务 · 登录时以管理员权限启动（免 UAC）";
                return true;
            }

            how = "创建管理员登录计划任务失败：" + detail;
            return false;
        }

        public static bool Disable(out string how)
        {
            try
            {
                bool hasRun = ReadRunValue() != null;
                if (hasRun && !RemoveRunValue()) { how = "未能移除旧版登录启动项"; return false; }
                bool hasTask = ReadTaskEnabled().HasValue;
                if (hasTask && !DeleteTask(out string detail)) { how = "删除计划任务失败：" + detail; return false; }
                how = hasRun || hasTask ? "已移除自启项" : "自启项已关闭";
                return true;
            }
            catch (Exception ex)
            {
                how = "无法检查或移除自启项：" + ex.GetBaseException().Message;
                return false;
            }
        }

        /// <summary>程序路径变化（比如移动了文件夹）时自动修正自启项</summary>
        public static void Sync()
        {
            try
            {
                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;

                string runValue = ReadRunValue();
                if (runValue != null)
                {
                    App.Log("autostart: 迁移旧版 Run 项到管理员登录计划任务 -> " + exe);
                    Enable(out _);
                    return;
                }

                if (App.IsAdmin() && RunSchtasks(out string xml, "/Query", "/TN", TaskName, "/XML") == 0
                    && !TaskPointsTo(xml, exe))
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

        private static bool RemoveRunValue()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k?.GetValue(ValueName) == null) return true;
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

        // null means the task does not exist. Query failures must reach the caller.
        private static bool? ReadTaskEnabled()
        {
            object service = null, folder = null, task = null;
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true));
                Call(service, "Connect");
                folder = Call(service, "GetFolder", "\\");
                try { task = Call(folder, "GetTask", TaskName); }
                catch (Exception ex) when (ex.GetBaseException().HResult == unchecked((int)0x80070002)) { return null; }
                return (bool)task.GetType().InvokeMember("Enabled", BindingFlags.GetProperty, null, task, null);
            }
            finally
            {
                if (task != null) Marshal.ReleaseComObject(task);
                if (folder != null) Marshal.ReleaseComObject(folder);
                if (service != null) Marshal.ReleaseComObject(service);
            }
        }

        private static object Call(object target, string method, params object[] args) =>
            target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);

        private static bool TaskPointsTo(string xml, string exe)
        {
            var task = XDocument.Parse(xml).Root;
            XNamespace ns = task.Name.Namespace;
            string command = (string)task.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command");
            return string.Equals(command?.Trim().Trim('"'), exe, StringComparison.OrdinalIgnoreCase);
        }

        private static bool CreateTask(string exe, out string detail)
        {
            int code = RunSchtasks(out detail,
                "/Create", "/TN", TaskName,
                "/TR", "\"" + exe + "\"",
                "/SC", "ONLOGON",
                "/RL", "HIGHEST",
                "/F");
            if (code == 0) App.Log("autostart: 计划任务已创建 -> " + exe);
            else App.Log("autostart: 创建计划任务失败，退出码 " + code);
            return code == 0;
        }

        private static bool DeleteTask(out string detail)
        {
            int code = RunSchtasks(out detail, "/Delete", "/TN", TaskName, "/F");
            if (code == 0) App.Log("autostart: 计划任务已删除");
            return code == 0;
        }

        private static int RunSchtasks(out string output, params string[] args)
        {
            output = null;
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                foreach (string a in args) psi.ArgumentList.Add(a);
                using Process p = Process.Start(psi);
                if (p == null) { output = "未能启动任务计划程序"; return -1; }
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                try
                {
                    Task.WhenAll(stdout, stderr, p.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                }
                catch (TimeoutException)
                {
                    if (!p.HasExited) p.Kill();
                    App.Log("autostart: schtasks 超时");
                    output = "任务计划程序响应超时";
                    return -1;
                }
                output = p.ExitCode == 0 ? stdout.Result : stderr.Result.Trim();
                if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(output)) output = "系统退出码 " + p.ExitCode;
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                App.Log("autostart: schtasks 调用失败 " + ex.Message);
                output = ex.Message;
                return -1;
            }
        }
    }
}
