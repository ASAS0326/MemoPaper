using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace StickyNotes
{
    // Owns every open note, the tray icon and saving. Runs the app until "結束".
    class NoteManager : ApplicationContext
    {
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "DesktopStickyNotes";

        const string WelcomeText =
            "歡迎使用桌面便條紙！\n\n" +
            "• 拖曳上方色條可以移動\n" +
            "• 拖曳邊緣或右下角可以調整大小\n" +
            "• 上方按鈕：＋ 新增、調色盤 換顏色、圖釘 置頂、垃圾桶 刪除\n" +
            "• 雙擊上方色條可以收合／展開\n" +
            "• Ctrl + 滑鼠滾輪 調整字體大小\n" +
            "• 按住 Ctrl 點網址可以開啟連結\n" +
            "• 內容會自動儲存\n\n" +
            "工作列右下角的便條紙圖示可以顯示全部便條紙、設定開機自動啟動。";

        readonly List<NoteForm> notes = new List<NoteForm>();
        readonly NotifyIcon tray;
        readonly Timer saveTimer;
        readonly Control invoker;
        readonly RegisteredWaitHandle activateWait;
        bool saveErrorShown;

        public NoteManager(EventWaitHandle activateEvent)
        {
            // Used to marshal "another copy was launched" signals onto the UI thread.
            invoker = new Control();
            invoker.CreateControl();

            saveTimer = new Timer();
            saveTimer.Interval = 600;
            saveTimer.Tick += delegate { SaveNow(); };

            var startupItem = new ToolStripMenuItem("開機時自動啟動", null, delegate { ToggleStartup(); });
            var menu = new ContextMenuStrip();
            menu.Font = Ui.MenuFont;
            menu.Items.Add(new ToolStripMenuItem("新增便條紙", null, delegate { NewNote(null); }));
            menu.Items.Add(new ToolStripMenuItem("顯示全部便條紙", null, delegate { ShowAll(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripMenuItem("開啟資料儲存位置", null, delegate { OpenDataFolder(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("結束", null, delegate { ExitApp(); }));
            menu.Opening += delegate { startupItem.Checked = IsStartupEnabled(); };

            tray = new NotifyIcon();
            tray.Icon = Program.TrayIcon;
            tray.Text = "桌面便條紙";
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowAll(); };
            tray.Visible = true;

            LoadNotes();
            RefreshStartupPath();

            activateWait = ThreadPool.RegisterWaitForSingleObject(activateEvent,
                delegate { invoker.BeginInvoke((MethodInvoker)ShowAll); }, null, Timeout.Infinite, false);
        }

        int DefaultWidth { get { return Ui.S(280); } }
        int DefaultHeight { get { return Ui.S(280); } }

        void LoadNotes()
        {
            string warning;
            List<NoteData> data = NoteStore.Load(out warning);
            if (warning != null)
                tray.ShowBalloonTip(8000, "桌面便條紙", warning, ToolTipIcon.Warning);

            if (data == null)
            {
                NewNote(null, WelcomeText);
                SaveNow();
                return;
            }

            foreach (NoteData d in data)
            {
                if (string.IsNullOrEmpty(d.Id))
                    d.Id = NewId();
                if (d.Width < Ui.S(120) || d.Height < Ui.S(80))
                {
                    d.Width = DefaultWidth;
                    d.Height = DefaultHeight;
                }
                if (!IsReachable(new Rectangle(d.X, d.Y, d.Width, d.Height)))
                {
                    // Saved on a monitor that is no longer connected.
                    Point p = FreeSpot(DefaultSpot(), new Size(-Ui.S(32), Ui.S(32)));
                    d.X = p.X;
                    d.Y = p.Y;
                }
                AddNote(d);
            }

            if (notes.Count == 0)
                NewNote(null);
        }

        public NoteForm NewNote(NoteForm near)
        {
            return NewNote(near, "");
        }

        NoteForm NewNote(NoteForm near, string initialText)
        {
            var d = new NoteData();
            d.Id = NewId();
            d.Text = initialText;
            d.Width = DefaultWidth;
            d.Height = DefaultHeight;
            d.FontSize = NoteForm.DefaultFontSize;

            Rectangle area;
            Point pos;
            if (near != null)
            {
                d.ColorIndex = near.ColorIndex;
                area = Screen.FromControl(near).WorkingArea;
                pos = FreeSpot(new Point(near.Left + Ui.S(32), near.Top + Ui.S(32)), new Size(Ui.S(32), Ui.S(32)));
            }
            else
            {
                area = Screen.PrimaryScreen.WorkingArea;
                pos = FreeSpot(DefaultSpot(), new Size(-Ui.S(32), Ui.S(32)));
            }
            d.X = Math.Max(area.Left, Math.Min(pos.X, area.Right - d.Width));
            d.Y = Math.Max(area.Top, Math.Min(pos.Y, area.Bottom - d.Height));

            NoteForm form = AddNote(d);
            form.Activate();
            form.FocusText();
            ScheduleSave();
            return form;
        }

        NoteForm AddNote(NoteData d)
        {
            var form = new NoteForm(this, d);
            notes.Add(form);
            form.Show();
            return form;
        }

        public void DeleteNote(NoteForm form)
        {
            notes.Remove(form);
            form.CloseForReal();
            SaveNow();
            if (notes.Count == 0)
                tray.ShowBalloonTip(4000, "桌面便條紙", "便條紙都刪光了，程式仍在背景執行。點一下工作列右下角的圖示即可新增。", ToolTipIcon.Info);
        }

        public void ShowAll()
        {
            if (notes.Count == 0)
            {
                NewNote(null);
                return;
            }
            foreach (NoteForm form in notes)
                form.BringToFrontForce();
            notes[notes.Count - 1].Activate();
        }

        public void ScheduleSave()
        {
            saveTimer.Stop();
            saveTimer.Start();
        }

        public void SaveNow()
        {
            saveTimer.Stop();
            var list = new List<NoteData>();
            foreach (NoteForm form in notes)
            {
                // During Windows shutdown the forms close one by one. Never write a partial list.
                if (form.IsDisposed) return;
                list.Add(form.ToData());
            }
            try
            {
                NoteStore.Save(list);
                saveErrorShown = false;
            }
            catch (Exception ex)
            {
                if (!saveErrorShown)
                {
                    saveErrorShown = true;
                    tray.ShowBalloonTip(8000, "桌面便條紙", "儲存失敗：" + ex.Message, ToolTipIcon.Error);
                }
            }
        }

        void ExitApp()
        {
            SaveNow();
            activateWait.Unregister(null);
            tray.Visible = false;
            tray.Dispose();
            foreach (NoteForm form in notes.ToArray())
                form.CloseForReal();
            notes.Clear();
            ExitThread();
        }

        // ---- Placement ----

        Point DefaultSpot()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            return new Point(area.Right - DefaultWidth - Ui.S(48), area.Top + Ui.S(48));
        }

        // Steps away from `start` until no other note sits at the same spot.
        Point FreeSpot(Point start, Size step)
        {
            Point p = start;
            for (int i = 0; i < 20 && IsOccupied(p); i++)
                p.Offset(step.Width, step.Height);
            return p;
        }

        bool IsOccupied(Point p)
        {
            int tolerance = Ui.S(8);
            foreach (NoteForm form in notes)
                if (Math.Abs(form.Left - p.X) < tolerance && Math.Abs(form.Top - p.Y) < tolerance)
                    return true;
            return false;
        }

        // True if enough of the header is on some screen to grab it.
        static bool IsReachable(Rectangle bounds)
        {
            var headerArea = new Rectangle(bounds.X, bounds.Y, bounds.Width, Ui.S(32));
            foreach (Screen screen in Screen.AllScreens)
            {
                Rectangle visible = Rectangle.Intersect(screen.WorkingArea, headerArea);
                if (visible.Width >= Ui.S(48) && visible.Height >= Ui.S(16))
                    return true;
            }
            return false;
        }

        static string NewId()
        {
            return Guid.NewGuid().ToString("N");
        }

        // ---- Tray menu actions ----

        static string StartupCommand
        {
            get { return "\"" + Application.ExecutablePath + "\""; }
        }

        static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return key != null && key.GetValue(RunValueName) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static void SetStartup(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (enabled)
                    key.SetValue(RunValueName, StartupCommand);
                else
                    key.DeleteValue(RunValueName, false);
            }
        }

        void ToggleStartup()
        {
            try
            {
                SetStartup(!IsStartupEnabled());
            }
            catch (Exception ex)
            {
                MessageBox.Show("無法變更開機自動啟動設定：" + ex.Message, "桌面便條紙",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // If the exe was moved, point the startup entry at its new location.
        static void RefreshStartupPath()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (key == null) return;
                    object value = key.GetValue(RunValueName);
                    if (value == null || (string)value == StartupCommand) return;
                }
                SetStartup(true);
            }
            catch (Exception) { }
        }

        static void OpenDataFolder()
        {
            Directory.CreateDirectory(NoteStore.Folder);
            Process.Start("explorer.exe", "\"" + NoteStore.Folder + "\"");
        }
    }
}
