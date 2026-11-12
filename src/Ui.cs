using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace StickyNotes
{
    static class Ui
    {
        public const string TextFontName = "Microsoft JhengHei UI";

        public static float Scale = 1f;
        public static Font IconFont;
        public static Font TitleFont;
        public static Font MenuFont;
        static bool hasIconFont;

        public static void Init()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                Scale = g.DpiX / 96f;

            IconFont = new Font("Segoe MDL2 Assets", 10f);
            hasIconFont = IconFont.Name == "Segoe MDL2 Assets";
            if (!hasIconFont)
                IconFont = new Font("Segoe UI Symbol", 10f);

            TitleFont = new Font(TextFontName, 9.5f);
            MenuFont = new Font(TextFontName, 9f);
        }

        // Scales a 96-DPI pixel value to the current DPI.
        public static int S(int value)
        {
            return (int)Math.Round(value * Scale);
        }

        public static string Glyph(string mdl2, string fallback)
        {
            return hasIconFont ? mdl2 : fallback;
        }

        public static Color Blend(Color from, Color to, float amount)
        {
            return Color.FromArgb(
                (int)Math.Round(from.R + (to.R - from.R) * amount),
                (int)Math.Round(from.G + (to.G - from.G) * amount),
                (int)Math.Round(from.B + (to.B - from.B) * amount));
        }
    }

    class NoteColor
    {
        public readonly string Name;
        public readonly Color Header;
        public readonly Color Body;
        public readonly Color Ink;

        public NoteColor(string name, string header, string body, string ink)
        {
            Name = name;
            Header = ColorTranslator.FromHtml(header);
            Body = ColorTranslator.FromHtml(body);
            Ink = ColorTranslator.FromHtml(ink);
        }
    }

    static class NoteColors
    {
        public static readonly NoteColor[] All =
        {
            new NoteColor("黃色", "#F9D65C", "#FFF6B8", "#2E2A1F"),
            new NoteColor("粉紅", "#F5A3C0", "#FFE1EC", "#3A2430"),
            new NoteColor("綠色", "#A5D98C", "#E4F6D9", "#22331B"),
            new NoteColor("藍色", "#8DC3F0", "#DDEEFC", "#1D2C3A"),
            new NoteColor("紫色", "#BFA8F0", "#EEE7FF", "#2C2340"),
            new NoteColor("橘色", "#F7B877", "#FFE9D2", "#3A2A18"),
            new NoteColor("灰色", "#C8C8C8", "#F2F2F2", "#2B2B2B"),
            new NoteColor("深色", "#3C3C3C", "#2A2A2A", "#E8E8E8"),
        };

        static Bitmap[] swatches;

        public static Image Swatch(int index)
        {
            if (swatches == null)
            {
                swatches = new Bitmap[All.Length];
                int size = Ui.S(16);
                for (int i = 0; i < All.Length; i++)
                {
                    var bmp = new Bitmap(size, size);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(All[i].Body);
                        using (var b = new SolidBrush(All[i].Header))
                            g.FillRectangle(b, 0, 0, size, size * 2 / 5);
                        using (var p = new Pen(Color.FromArgb(120, 0, 0, 0)))
                            g.DrawRectangle(p, 0, 0, size - 1, size - 1);
                    }
                    swatches[i] = bmp;
                }
            }
            return swatches[index];
        }
    }

    static class Native
    {
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int WM_MOUSEWHEEL = 0x020A;

        public const int HTTRANSPARENT = -1;
        public const int HTCLIENT = 1;
        public const int HTCAPTION = 2;
        public const int HTLEFT = 10;
        public const int HTRIGHT = 11;
        public const int HTTOP = 12;
        public const int HTTOPLEFT = 13;
        public const int HTTOPRIGHT = 14;
        public const int HTBOTTOM = 15;
        public const int HTBOTTOMLEFT = 16;
        public const int HTBOTTOMRIGHT = 17;

        public const int CS_DROPSHADOW = 0x00020000;

        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_ROUND = 2;

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static Point PointFromLParam(IntPtr lParam)
        {
            long v = lParam.ToInt64();
            return new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
        }

        // Windows 11 rounded corners for borderless windows; ignored on older systems.
        public static void TryRoundCorners(IntPtr hwnd)
        {
            try
            {
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
    }
}
