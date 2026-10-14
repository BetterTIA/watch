using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace BtiaWatch
{
    /// <summary>Window capture with PrintWindow and keystrokes to a window. Plain Win32, no TIA Portal.</summary>
    public static class WindowCapture
    {
        // PW_RENDERFULLCONTENT: captures DirectX/WPF content too, without bringing the window to the front.
        private const uint PrintFullContent = 2;

        /// <summary>Captures the whole window as PNG bytes. The window may be covered, but not minimized.</summary>
        public static byte[] CapturePng(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                throw new InvalidOperationException("The window handle is not valid.");
            }

            if (IsIconic(hwnd))
            {
                throw new InvalidOperationException("The window is minimized; restore it before capturing.");
            }

            if (!GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            {
                throw new InvalidOperationException("The window has no visible area.");
            }

            using var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    if (!PrintWindow(hwnd, hdc, PrintFullContent))
                    {
                        throw new InvalidOperationException("PrintWindow failed.");
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        /// <summary>Shows a minimized window in its last size without activating it; PrintWindow cannot capture it minimized.</summary>
        public static void Restore(IntPtr hwnd)
        {
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, ShowNoActivate);
                Thread.Sleep(300);
            }
        }

        public static IntPtr Foreground() => GetForegroundWindow();

        public static void Activate(IntPtr hwnd) => SetForegroundWindow(hwnd);

        /// <summary>
        /// Brings the window to the front and presses the keys with SendInput (no Alt: it would open the menu bar).
        /// Returns false and presses nothing when the window could not be made the foreground window,
        /// so the keys never land in another application.
        /// </summary>
        public static bool PressKeys(IntPtr hwnd, params ushort[] keys)
        {
            SetForegroundWindow(hwnd);
            Thread.Sleep(200);
            if (GetForegroundWindow() != hwnd)
            {
                return false;
            }

            var inputs = keys.Select(k => Key(k, false)).Concat(Enumerable.Reverse(keys).Select(k => Key(k, true))).ToArray();
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
        }

        public const ushort VkControl = 0x11, VkT = 0x54, VkEscape = 0x1B;

        /// <summary>Visible top-level windows with a title that belong to the process of <paramref name="hwnd"/>, except that window.</summary>
        public static IReadOnlyList<IntPtr> OtherWindowsOfProcess(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            var found = new List<IntPtr>();
            EnumWindows((w, _) =>
            {
                GetWindowThreadProcessId(w, out var owner);
                if (w != hwnd && owner == pid && IsWindowVisible(w) && GetWindowTextLength(w) > 0)
                {
                    found.Add(w);
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static Input Key(ushort vk, bool up) => new() { Type = 1, Vk = vk, Flags = up ? 2u : 0u };

        // INPUT with its keyboard member, laid out for 64-bit processes only (both projects build x64).
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct Input
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public ushort Vk;
            [FieldOffset(10)] public ushort Scan;
            [FieldOffset(12)] public uint Flags;
            [FieldOffset(16)] public uint Time;
            [FieldOffset(24)] public IntPtr ExtraInfo;
        }

        private const int ShowNoActivate = 4;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();
    }
}

namespace BtiaWatch
{
    /// <summary>
    /// Reads whether the visible editor monitors, from the pressed state of its "Monitor" toolbar button.
    /// TIA Portal draws its toolbars itself; UI Automation shows no buttons there, MSAA (oleacc) does:
    /// name e.g. "InterfaceEditor.Monitor_ICO_PE_TbMonitor"; monitoring = role check button with state bit CHECKED,
    /// off = TIA swaps the role to push button (spike, V21).
    /// Read-only: only asks for names, roles and states.
    /// </summary>
    public static class MonitorButton
    {
        private const int RoleToolbar = 22, RolePushButton = 43, RoleCheckButton = 44, StateChecked = 0x10;
        private const uint ObjidClient = 0xFFFFFFFC;

        // shortcut: floating (undocked) editors are separate top-level windows and are not searched; add them when someone undocks.
        /// <summary>True/false when every Monitor button found agrees; null when none is found or they disagree (split editor space).</summary>
        public static bool? IsChecked(IntPtr hwnd)
        {
            var states = new List<bool>();
            EnumChildWindows(hwnd, (child, _) =>
            {
                // Hidden editors keep their windows; only the visible ones count.
                if (!IsWindowVisible(child) || FromWindow(child) is not IAccessible toolbar || Role(toolbar, 0) != RoleToolbar)
                {
                    return true;
                }
                foreach (var kid in Children(toolbar))
                {
                    var (owner, id) = kid is IAccessible element ? (element, (object)0) : (toolbar, kid);
                    var name = Safe(() => owner.get_accName(id)) ?? string.Empty;
                    var role = Role(owner, id);
                    // Suffix, not substring: the neighbouring "..._TbMonitorOnce" button must not count.
                    if ((role == RoleCheckButton || role == RolePushButton) && name.EndsWith("_TbMonitor", StringComparison.OrdinalIgnoreCase))
                    {
                        states.Add(role == RoleCheckButton && (State(owner, id) & StateChecked) != 0);
                    }
                }
                return true;
            }, IntPtr.Zero);
            return states.Count > 0 && states.Distinct().Count() == 1 ? states[0] : null;
        }

        private static IAccessible? FromWindow(IntPtr hwnd)
        {
            var iid = typeof(IAccessible).GUID;
            return AccessibleObjectFromWindow(hwnd, ObjidClient, ref iid, out var obj) == 0 ? obj as IAccessible : null;
        }

        private static object[] Children(IAccessible parent)
        {
            var count = Safe(() => parent.get_accChildCount());
            if (count <= 0)
            {
                return new object[0];
            }
            var kids = new object[count];
            return AccessibleChildren(parent, 0, count, kids, out var got) >= 0 ? kids.Take(got).ToArray() : new object[0];
        }

        private static int Role(IAccessible a, object id) => Safe(() => a.get_accRole(id)) is int r ? r : -1;

        private static int State(IAccessible a, object id) => Safe(() => a.get_accState(id)) is int s ? s : 0;

        private static T? Safe<T>(Func<T> read)
        {
            try
            {
                return read();
            }
            catch (COMException)
            {
                return default;
            }
        }

        // IAccessible up to get_accState, in vtable order; declared here so no extra assembly reference is needed.
        [ComImport, Guid("618736E0-3C3D-11CF-810C-00AA00389B71"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IAccessible
        {
            [return: MarshalAs(UnmanagedType.IDispatch)] object get_accParent();
            int get_accChildCount();
            [return: MarshalAs(UnmanagedType.IDispatch)] object get_accChild(object child);
            string get_accName(object child);
            string get_accValue(object child);
            string get_accDescription(object child);
            object get_accRole(object child);
            object get_accState(object child);
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);

        [DllImport("oleacc.dll")]
        private static extern int AccessibleChildren(IAccessible parent, int start, int count,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int obtained);
    }

    /// <summary>The window operations watch_screen needs; replaceable so tests need no real window behind a fake portal.</summary>
    public interface IWindowOps
    {
        byte[] Capture(IntPtr hwnd);
        void Restore(IntPtr hwnd);
        IntPtr Foreground();
        void Activate(IntPtr hwnd);
        /// <summary>Ctrl+T ("Monitor all" toggle in every STEP 7 editor of V21); false when nothing was sent.</summary>
        bool SendMonitorShortcut(IntPtr hwnd);
        bool PressEscape(IntPtr hwnd);
        IReadOnlyList<IntPtr> OtherWindowsOfProcess(IntPtr hwnd);
        /// <summary>Whether the visible editor monitors; null when that cannot be read.</summary>
        bool? IsMonitoring(IntPtr hwnd);
    }

    public sealed class Win32Windows : IWindowOps
    {
        public byte[] Capture(IntPtr hwnd) => WindowCapture.CapturePng(hwnd);
        public void Restore(IntPtr hwnd) => WindowCapture.Restore(hwnd);
        public IntPtr Foreground() => WindowCapture.Foreground();
        public void Activate(IntPtr hwnd) => WindowCapture.Activate(hwnd);
        public bool SendMonitorShortcut(IntPtr hwnd) => WindowCapture.PressKeys(hwnd, WindowCapture.VkControl, WindowCapture.VkT);
        public bool PressEscape(IntPtr hwnd) => WindowCapture.PressKeys(hwnd, WindowCapture.VkEscape);
        public IReadOnlyList<IntPtr> OtherWindowsOfProcess(IntPtr hwnd) => WindowCapture.OtherWindowsOfProcess(hwnd);
        public bool? IsMonitoring(IntPtr hwnd) => MonitorButton.IsChecked(hwnd);
    }
}
