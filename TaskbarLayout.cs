using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace StatsOSD
{
    internal sealed class TaskbarLayout
    {
        public IntPtr Host { get; }
        public Rectangle Area { get; }
        public bool Vertical { get; }

        private TaskbarLayout(IntPtr host, Rectangle area, bool vertical)
        {
            Host = host;
            Area = area;
            Vertical = vertical;
        }

        public static IntPtr CurrentTaskbar() => FindWindow("Shell_TrayWnd", null);

        private static readonly Condition VisibleControls = BuildControlFilter();

        private static Condition BuildControlFilter()
        {
            var types = new[] { ControlType.Button, ControlType.ListItem, ControlType.TabItem,
                ControlType.MenuItem, ControlType.CheckBox, ControlType.RadioButton, ControlType.Edit,
                ControlType.SplitButton, ControlType.Hyperlink, ControlType.ComboBox };
            var conditions = Array.ConvertAll(types, t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t));
            return new AndCondition(new PropertyCondition(AutomationElement.IsOffscreenProperty, false), new OrCondition(conditions));
        }

        public static TaskbarLayout Locate(double fontScale)
        {
            IntPtr host = CurrentTaskbar();
            if (host == IntPtr.Zero || !GetWindowRect(host, out var bar)) return null;
            IntPtr tray = FindWindowEx(host, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray == IntPtr.Zero || !GetWindowRect(tray, out var notification)) return null;
            var barBounds = bar.ToRectangle();
            var trayBounds = Rectangle.Intersect(barBounds, notification.ToRectangle());
            if (trayBounds.Width <= 0 || trayBounds.Height <= 0) return null;
            bool vertical = barBounds.Width < barBounds.Height;

            var occupied = new List<Rectangle> { trayBounds };
            var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
            cache.Add(AutomationElement.BoundingRectangleProperty);
            bool foundButtons = false;
            var root = AutomationElement.FromHandle(host);
            using (cache.Activate())
            {
                foreach (AutomationElement item in root.FindAll(TreeScope.Descendants, VisibleControls))
                {
                    var r = item.Cached.BoundingRectangle;
                    if (r.IsEmpty || r.Width < 1 || r.Height < 1) continue;
                    var bounds = Rectangle.FromLTRB((int)Math.Floor(r.Left), (int)Math.Floor(r.Top),
                        (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));
                    bounds.Intersect(barBounds);
                    if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                    occupied.Add(bounds);
                    if (!trayBounds.Contains(bounds)) foundButtons = true;
                }
            }
            if (!foundButtons) return null;
            Rectangle area = FindSpace(barBounds, occupied, vertical, GetDpiForWindow(host) / 96.0, fontScale);
            return area.IsEmpty ? null : new TaskbarLayout(host, area, vertical);
        }

        // Return physical pixels relative to the taskbar. Prefer the last free interval before the tray.
        internal static Rectangle FindSpace(Rectangle bar, List<Rectangle> occupied, bool vertical, double scale, double fontScale)
        {
            int origin = vertical ? bar.Top : bar.Left;
            int end = vertical ? bar.Bottom : bar.Right;
            int padding = (int)Math.Ceiling(4 * scale);
            double desiredLength = Math.Ceiling((vertical ? 102 : 135) * scale * fontScale);
            if (!(desiredLength > 0 && desiredLength <= end - origin - 2 * padding)) return Rectangle.Empty;
            int length = (int)desiredLength;
            int thickness = Math.Min(vertical ? bar.Width : bar.Height,
                (int)Math.Ceiling((vertical ? 48 : 34) * scale * fontScale));
            int lastFree = -1;
            int cursor = origin;
            occupied.Sort((a, b) => vertical ? a.Top.CompareTo(b.Top) : a.Left.CompareTo(b.Left));
            foreach (var item in occupied)
            {
                var rect = Rectangle.Intersect(bar, item);
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                int start = vertical ? rect.Top : rect.Left;
                if (start - cursor >= length + 2 * padding) lastFree = start - padding - length - origin;
                cursor = Math.Max(cursor, vertical ? rect.Bottom : rect.Right);
            }
            if (end - cursor >= length + 2 * padding) lastFree = end - padding - length - origin;
            if (lastFree < 0 || thickness <= 0) return Rectangle.Empty;
            return vertical ? new Rectangle((bar.Width - thickness) / 2, lastFree, thickness, length)
                            : new Rectangle(lastFree, (bar.Height - thickness) / 2, length, thickness);
        }

        public static void ShowAt(IntPtr handle, Rectangle area)
        {
            const uint NoActivate = 0x0010, ShowWindow = 0x0040;
            if (!SetWindowPos(handle, IntPtr.Zero, area.X, area.Y, area.Width, area.Height, NoActivate | ShowWindow))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
            public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr handle);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int w, int h, uint flags);
    }
}
