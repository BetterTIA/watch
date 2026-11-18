using BtiaWatch;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace BtiaWatch.Test
{
    /// <summary>PrintWindow on a window of this test process; no TIA Portal involved.</summary>
    [TestClass]
    public class FloatingEditorMatchTests
    {
        private static IntPtr Pick(string name, params string[] titles) =>
            WindowCapture.PickFloating(titles.Select((t, i) => (new IntPtr(i + 1), t)), name);

        [TestMethod]
        public void SingleWholeTokenMatch_IsPicked() => Assert.AreEqual(new IntPtr(2), Pick("Watch_1", "TIA Portal", "Watch_1 - PLC_1"));

        [TestMethod]
        public void NameInsideLongerName_IsNotPicked()
        {
            Assert.AreEqual(IntPtr.Zero, Pick("DB1", "DB10 - PLC_1"));
            Assert.AreEqual(IntPtr.Zero, Pick("Watch", "My_Watch_table"));
        }

        [TestMethod]
        public void TwoMatches_PickNothing() => Assert.AreEqual(IntPtr.Zero, Pick("Watch_1", "Watch_1 - PLC_1", "PLC_2 / Watch_1"));

        [TestMethod]
        public void NoMatchOrEmptyName_PickNothing()
        {
            Assert.AreEqual(IntPtr.Zero, Pick("Watch_1", "Other"));
            Assert.AreEqual(IntPtr.Zero, Pick("", "Other"));
        }
    }

    [TestClass]
    public class WindowCaptureTests
    {
        [TestMethod]
        public void CapturePng_OwnWindow_ReturnsPngOfWindowSize()
        {
            byte[]? png = null;
            Size size = default;
            Exception? error = null;

            // WinForms wants an STA thread with a message loop of its own.
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new Form { Width = 320, Height = 200, BackColor = Color.Red, ShowInTaskbar = false };
                    form.Show();
                    Application.DoEvents();
                    size = form.Size;
                    png = WindowCapture.CapturePng(form.Handle);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            Assert.IsNull(error, error?.ToString());
            Assert.IsNotNull(png);
            CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png!.Take(4).ToArray());
            using var image = Image.FromStream(new MemoryStream(png));
            Assert.AreEqual(size, image.Size);
        }

        [TestMethod]
        public void CapturePng_InvalidHandle_Throws()
        {
            Assert.ThrowsException<InvalidOperationException>(() => WindowCapture.CapturePng(IntPtr.Zero));
        }
    }
}
