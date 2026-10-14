using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

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

        /// <summary>
        /// Brings the window to the front and sends a key sequence in SendKeys notation (e.g. "^t").
        /// Returns false and sends nothing when the window could not be made the foreground window,
        /// so the keys never land in another application.
        /// </summary>
        public static bool SendKeysTo(IntPtr hwnd, string keys, int settleMs)
        {
            SetForegroundWindow(hwnd);
            Thread.Sleep(Math.Min(settleMs, 500));

            if (GetForegroundWindow() != hwnd)
            {
                return false;
            }

            SendKeys.SendWait(keys);
            return true;
        }

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
