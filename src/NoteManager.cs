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
    // Owns every open note, the tray icon, saving and cloud sync. Runs the app until "結束".
    class NoteManager : ApplicationContext
    {
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "DesktopStickyNotes";
        const string AppName = "桌面便條紙";

        const string WelcomeText =
            "歡迎使用桌面便條紙！\n\n" +
            "• 拖曳上方色條可以移動\n" +
            "• 拖曳邊緣或右下角可以調整大小\n" +
            "• 上方按鈕：＋ 新增、調色盤 換顏色、圖釘 置頂、垃圾桶 刪除\n" +
            "• 雙擊上方色條可以收合／展開\n" +
            "• Ctrl + 滑鼠滾輪 調整字體大小\n" +
            "• 按住 Ctrl 點網址可以開啟連結\n" +
            "• 內容會自動儲存\n\n" +
            "工作列右下角的便條紙圖示可以顯示全部便條紙、設定開機自動啟動、連線 Google 雲端同步。";

        readonly List<NoteForm> notes = new List<NoteForm>();
        readonly NotifyIcon tray;
        readonly Timer saveTimer;
        readonly Control invoker;
        readonly RegisteredWaitHandle activateWait;
        readonly CloudSync sync;
        Dictionary<string, long> deleted = new Dictionary<string, long>();
        int changeCount;
        bool saveErrorShown;
        bool exiting;

        ToolStripMenuItem cloudAccountItem, cloudStatusItem, cloudConnectItem, cloudCancelItem, cloudSyncItem, cloudDisconnectItem;

        public NoteManager(EventWaitHandle activateEvent)
        {
            // Used to marshal "another copy was launched" signals onto the UI thread.
            invoker = new Control();
            invoker.CreateControl();

            saveTimer = new Timer();
            saveTimer.Interval = 600;
            saveTimer.Tick += delegate { SaveNow(); };

            sync = new CloudSync(this);
            sync.StatusChanged += delegate { UpdateCloudStatus(); };

            var startupItem = new ToolStripMenuItem("開機時自動啟動", null, delegate { ToggleStartup(); });
            var menu = new ContextMenuStrip();
            menu.Font = Ui.MenuFont;
            menu.Items.Add(new ToolStripMenuItem("新增便條紙", null, delegate { NewNote(null); }));
            menu.Items.Add(new ToolStripMenuItem("顯示全部便條紙", null, delegate { ShowAll(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(BuildCloudMenu());
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripMenuItem("開啟資料儲存位置", null, delegate { OpenDataFolder(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("結束", null, delegate { ExitApp(); }));
            menu.Opening += delegate { startupItem.Checked = IsStartupEnabled(); };

            tray = new NotifyIcon();
            tray.Icon = Program.TrayIcon;
            tray.Text = AppName;
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowAll(); };
            tray.Visible = true;

            LoadNotes();
            RefreshStartupPath();
            sync.Start();

            activateWait = ThreadPool.RegisterWaitForSingleObject(activateEvent,
                delegate { invoker.BeginInvoke((MethodInvoker)ShowAll); }, null, Timeout.Infinite, false);
        }

        int DefaultWidth { get { return Ui.S(280); } }
        int DefaultHeight { get { return Ui.S(280); } }

        void LoadNotes()
        {
            string warning;
            StoreData data = NoteStore.Load(out warning);
            if (warning != null)
                tray.ShowBalloonTip(8000, AppName, warning, ToolTipIcon.Warning);

            if (data == null)
            {
                NewNote(null, WelcomeText);
                SaveNow();
                return;
            }

            deleted = data.Deleted;
            SyncMerge.PruneDeleted(deleted, Clock.Now());

            foreach (NoteData d in data.Notes)
            {
                if (string.IsNullOrEmpty(d.Id))
                    d.Id = NewId();
                if (d.Modified <= 0)
                    d.Modified = Clock.Now();   // saved by a version without sync
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
            d.Modified = Clock.Now();

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
            NoteChanged();
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
            // Remember the deletion so other computers remove the note too instead of sending it back.
            deleted[form.Id] = Math.Max(Clock.Now(), form.Modified + 1);
            form.CloseForReal();
            changeCount++;
            SaveNow();
            sync.ScheduleSoon();
            if (notes.Count == 0)
                tray.ShowBalloonTip(4000, AppName, "便條紙都刪光了，程式仍在背景執行。點一下工作列右下角的圖示即可新增。", ToolTipIcon.Info);
        }

        public void ShowAll()
        {
            if (exiting) return;
            if (notes.Count == 0)
            {
                NewNote(null);
                return;
            }
            foreach (NoteForm form in notes)
                form.BringToFrontForce();
            notes[notes.Count - 1].Activate();
        }

        // Layout changes (move, resize, pin, collapse): saved here, not synced.
        public void ScheduleSave()
        {
            saveTimer.Stop();
            saveTimer.Start();
        }

        // Content changes (text, color, font size): saved here and sent to the cloud.
        public void NoteChanged()
        {
            changeCount++;
            ScheduleSave();
            sync.ScheduleSoon();
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
                NoteStore.Save(list, deleted);
                saveErrorShown = false;
            }
            catch (Exception ex)
            {
                if (!saveErrorShown)
                {
                    saveErrorShown = true;
                    tray.ShowBalloonTip(8000, AppName, "儲存失敗：" + ex.Message, ToolTipIcon.Error);
                }
            }
        }

        async void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            SaveNow();

            if (sync.IsConnected)
            {
                // Hide right away, then give the last edits a few seconds to reach the cloud.
                foreach (NoteForm form in notes)
                    form.Hide();
                tray.Text = AppName + "\n正在上傳最後的變更…";
                try { await sync.FlushAsync(8000); }
                catch (Exception) { }
                SaveNow();
            }

            sync.Dispose();
            activateWait.Unregister(null);
            tray.Visible = false;
            tray.Dispose();
            foreach (NoteForm form in notes.ToArray())
                form.CloseForReal();
            notes.Clear();
            ExitThread();
        }

        // ---- What CloudSync needs from the notes ----

        // Bumped on every synced edit and deletion, so CloudSync can tell whether anything is pending.
        public int ChangeCount
        {
            get { return changeCount; }
        }

        public Dictionary<string, long> DeletedNotes
        {
            get { return new Dictionary<string, long>(deleted); }
        }

        // Null while Windows is closing the notes, so a sync never sees a partial list.
        public List<NoteData> CaptureNotes()
        {
            var list = new List<NoteData>();
            foreach (NoteForm form in notes)
            {
                if (form.IsDisposed) return null;
                list.Add(form.ToData());
            }
            return list;
        }

        // Makes the open notes match the merge: updates changed ones, closes deleted ones, opens new ones.
        public void ApplyMerge(MergeResult result)
        {
            var byId = new Dictionary<string, NoteForm>();
            foreach (NoteForm form in notes)
                byId[form.Id] = form;

            var wanted = new HashSet<string>();
            var added = new List<NoteData>();
            foreach (NoteData d in result.Notes)
            {
                wanted.Add(d.Id);
                NoteForm form;
                if (byId.TryGetValue(d.Id, out form))
                    form.ApplySync(d);
                else
                    added.Add(d);
            }

            foreach (NoteForm form in notes.ToArray())
            {
                if (wanted.Contains(form.Id)) continue;
                notes.Remove(form);
                form.CloseForReal();
            }

            foreach (NoteData d in added)
                AddSyncedNote(d);

            deleted = new Dictionary<string, long>(result.Deleted);
            SaveNow();
        }

        void AddSyncedNote(NoteData d)
        {
            // Notes from another computer have no position here; conflict copies start next to their original.
            bool hasSpot = d.Width >= Ui.S(120) && d.Height >= Ui.S(80);
            if (!hasSpot)
            {
                d.Width = DefaultWidth;
                d.Height = DefaultHeight;
            }
            Point p = hasSpot
                ? FreeSpot(new Point(d.X, d.Y), new Size(Ui.S(32), Ui.S(32)))
                : FreeSpot(DefaultSpot(), new Size(-Ui.S(32), Ui.S(32)));
            if (!IsReachable(new Rectangle(p.X, p.Y, d.Width, d.Height)))
                p = FreeSpot(DefaultSpot(), new Size(-Ui.S(32), Ui.S(32)));
            d.X = p.X;
            d.Y = p.Y;

            var form = new NoteForm(this, d);
            form.OpenQuietly = true;
            notes.Add(form);
            if (!exiting)
                form.Show();
        }

        public void MarkUploaded(SyncDoc doc)
        {
            foreach (NoteForm form in notes)
            {
                SyncNote n;
                if (doc.Notes.TryGetValue(form.Id, out n))
                    form.Synced = n.Modified;
            }
            ScheduleSave();
        }

        // On a computer that just signed in, drop the untouched welcome note and empty notes
        // instead of adding them to the notes already in the cloud.
        public void DiscardPristineNotes()
        {
            foreach (NoteForm form in notes.ToArray())
            {
                if (form.Synced != 0) continue;
                string text = form.ToData().Text ?? "";
                if (text.Trim().Length > 0 && text != WelcomeText) continue;
                notes.Remove(form);
                form.CloseForReal();
            }
        }

        public void NotifyCloud(string message, ToolTipIcon icon)
        {
            if (!exiting)
                tray.ShowBalloonTip(8000, AppName, message, icon);
        }

        // ---- Cloud sync menu ----

        ToolStripMenuItem BuildCloudMenu()
        {
            cloudAccountItem = new ToolStripMenuItem();
            cloudAccountItem.Enabled = false;
            cloudStatusItem = new ToolStripMenuItem();
            cloudStatusItem.Enabled = false;
            cloudConnectItem = new ToolStripMenuItem("連線 Google 雲端硬碟…", null, delegate { ConnectCloud(); });
            cloudCancelItem = new ToolStripMenuItem("取消登入", null, delegate { sync.CancelSignIn(); });
            cloudSyncItem = new ToolStripMenuItem("立即同步", null, delegate { sync.SyncNow(); });
            cloudDisconnectItem = new ToolStripMenuItem("中斷連線…", null, delegate { DisconnectCloud(); });

            var cloudMenu = new ToolStripMenuItem("Google 雲端同步");
            cloudMenu.DropDown.Font = Ui.MenuFont;
            cloudMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                cloudAccountItem, cloudStatusItem, new ToolStripSeparator(),
                cloudConnectItem, cloudCancelItem, cloudSyncItem, cloudDisconnectItem,
            });
            cloudMenu.DropDownOpening += delegate { RefreshCloudMenu(); };
            RefreshCloudMenu();
            return cloudMenu;
        }

        void RefreshCloudMenu()
        {
            bool connected = sync.IsConnected;
            bool signingIn = sync.State == SyncState.SigningIn;

            cloudAccountItem.Text = connected
                ? "帳號：" + (sync.Email ?? "已連線")
                : signingIn ? "請在瀏覽器完成 Google 登入" : "尚未連線，便條紙只存在這台電腦";
            cloudStatusItem.Text = sync.StatusText ?? "";
            cloudStatusItem.Available = connected;
            cloudConnectItem.Available = !connected && !signingIn;
            cloudCancelItem.Available = signingIn;
            cloudSyncItem.Available = connected;
            cloudDisconnectItem.Available = connected;
        }

        void UpdateCloudStatus()
        {
            if (exiting) return;
            string status = sync.StatusText;
            string text = status == null ? AppName : AppName + "\n" + status;
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;   // NotifyIcon's limit
            RefreshCloudMenu();
        }

        async void ConnectCloud()
        {
            GoogleClient client = GoogleAuth.LoadClient() ?? AskForClientFile();
            if (client == null) return;

            tray.ShowBalloonTip(8000, AppName, "已開啟瀏覽器，請登入 Google 帳號並允許存取。", ToolTipIcon.Info);
            try
            {
                GoogleAccount account = await sync.SignInAsync(client);
                tray.ShowBalloonTip(5000, AppName,
                    "已連線 " + (account.Email ?? "Google 雲端硬碟") + "，便條紙會自動同步。", ToolTipIcon.Info);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                MessageBox.Show("無法連線 Google 雲端硬碟：" + ex.Message, AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        static GoogleClient AskForClientFile()
        {
            DialogResult go = MessageBox.Show(
                "第一次連線前，需要先選擇 Google OAuth 用戶端設定檔。\n\n" +
                "這是在 Google Cloud Console 建立「電腦版應用程式」用戶端後下載的 JSON 檔（檔名通常是 client_secret_….json）。每台電腦只需要選一次。",
                "連線 Google 雲端硬碟", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (go != DialogResult.OK) return null;

            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "選擇 Google OAuth 用戶端設定檔";
                dialog.Filter = "JSON 檔案 (*.json)|*.json|所有檔案 (*.*)|*.*";
                if (dialog.ShowDialog() != DialogResult.OK) return null;

                string error;
                GoogleClient client = GoogleAuth.ImportClient(dialog.FileName, out error);
                if (client == null)
                    MessageBox.Show(error, "連線 Google 雲端硬碟", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return client;
            }
        }

        void DisconnectCloud()
        {
            DialogResult answer = MessageBox.Show(
                "要中斷 Google 雲端同步嗎？\n\n便條紙會保留在這台電腦，雲端上的資料也不會刪除，之後可以隨時重新連線。",
                "中斷雲端同步", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes)
                sync.Disconnect();
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
                MessageBox.Show("無法變更開機自動啟動設定：" + ex.Message, AppName,
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
