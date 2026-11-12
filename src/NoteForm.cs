using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace StickyNotes
{
    class NoteForm : Form
    {
        public const float DefaultFontSize = 12f;
        const float MinFontSize = 8f;
        const float MaxFontSize = 36f;

        readonly NoteManager manager;
        readonly string id;
        readonly EdgePanel header;
        readonly EdgePanel body;
        readonly NoteTextBox text;
        readonly HeaderButton btnAdd, btnColor, btnPin, btnDelete;
        readonly ContextMenuStrip colorMenu, headerMenu, textMenu;
        readonly ToolStripMenuItem menuPin, menuCollapse;
        readonly ToolTip tip = new ToolTip();
        readonly int grip;
        readonly int headerHeight;
        readonly Size expandedMinSize;

        int colorIndex;
        float fontSize;
        bool collapsed;
        int expandedHeight;
        bool closingForReal;
        int lastHeaderDownTick;
        Point lastHeaderDownPos;

        public NoteForm(NoteManager manager, NoteData d)
        {
            this.manager = manager;
            id = d.Id;
            colorIndex = d.ColorIndex >= 0 && d.ColorIndex < NoteColors.All.Length ? d.ColorIndex : 0;
            fontSize = Clamp(d.FontSize > 0 ? d.FontSize : DefaultFontSize);

            grip = Ui.S(6);
            headerHeight = Ui.S(32);
            expandedMinSize = new Size(Ui.S(180), Ui.S(120));

            SuspendLayout();
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            Text = "桌面便條紙";
            Icon = Program.AppIcon;
            Font = Ui.MenuFont;
            MinimumSize = expandedMinSize;
            Bounds = new Rectangle(d.X, d.Y, d.Width, d.Height);
            TopMost = d.TopMost;

            // ---- Text area ----
            text = new NoteTextBox();
            text.Dock = DockStyle.Fill;
            text.BorderStyle = BorderStyle.None;
            text.ScrollBars = RichTextBoxScrollBars.Vertical;
            text.DetectUrls = true;
            text.Font = new Font(Ui.TextFontName, fontSize);
            text.Text = d.Text ?? "";
            text.CtrlWheel = delegate(int delta) { SetFontSize(fontSize + (delta > 0 ? 1 : -1)); };
            text.LinkClicked += OnLinkClicked;
            text.TextChanged += delegate
            {
                if (collapsed) header.Invalidate();
                manager.ScheduleSave();
            };

            body = new EdgePanel();
            body.Dock = DockStyle.Fill;
            body.Padding = new Padding(Ui.S(12), Ui.S(4), Ui.S(8), Ui.S(12));
            body.Paint += PaintResizeGrip;
            body.Controls.Add(text);

            // ---- Header bar: drag to move, double-click to collapse ----
            header = new EdgePanel();
            header.Dock = DockStyle.Top;
            header.Height = headerHeight;
            header.Padding = new Padding(grip, 0, grip, 0);
            header.MouseDown += OnHeaderMouseDown;
            header.Paint += PaintCollapsedTitle;

            btnAdd = new HeaderButton(Ui.Glyph("", "+"), "新增便條紙 (Ctrl+N)", tip, DockStyle.Left);
            btnColor = new HeaderButton(Ui.Glyph("", "◐"), "變更顏色", tip, DockStyle.Right);
            btnPin = new HeaderButton(Ui.Glyph("", "△"), "置頂", tip, DockStyle.Right);
            btnDelete = new HeaderButton(Ui.Glyph("", "×"), "刪除便條紙 (Ctrl+D)", tip, DockStyle.Right);

            btnAdd.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) manager.NewNote(this); };
            btnColor.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) colorMenu.Show(btnColor, new Point(0, btnColor.Height)); };
            btnPin.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) SetPinned(!TopMost); };
            btnDelete.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) DeleteWithConfirm(); };

            // Docking runs from the last-added control inward, so btnDelete ends up rightmost.
            header.Controls.Add(btnAdd);
            header.Controls.Add(btnColor);
            header.Controls.Add(btnPin);
            header.Controls.Add(btnDelete);

            Controls.Add(body);
            Controls.Add(header);

            // ---- Menus ----
            colorMenu = NewMenu();
            AddColorItems(colorMenu.Items);
            colorMenu.Opening += delegate { RefreshColorChecks(colorMenu.Items); };

            var colorSub = new ToolStripMenuItem("顏色");
            AddColorItems(colorSub.DropDownItems);
            colorSub.DropDownOpening += delegate { RefreshColorChecks(colorSub.DropDownItems); };

            var fontSub = new ToolStripMenuItem("字體大小");
            fontSub.DropDownItems.Add(MenuItem("放大", "Ctrl++", delegate { SetFontSize(fontSize + 1); }));
            fontSub.DropDownItems.Add(MenuItem("縮小", "Ctrl+-", delegate { SetFontSize(fontSize - 1); }));
            fontSub.DropDownItems.Add(MenuItem("恢復預設", "Ctrl+0", delegate { SetFontSize(DefaultFontSize); }));

            menuPin = MenuItem("置頂", null, delegate { SetPinned(!TopMost); });
            menuCollapse = MenuItem("收合", "雙擊標題列", delegate { SetCollapsed(!collapsed); });

            headerMenu = NewMenu();
            headerMenu.Items.Add(MenuItem("新增便條紙", "Ctrl+N", delegate { manager.NewNote(this); }));
            headerMenu.Items.Add(new ToolStripSeparator());
            headerMenu.Items.Add(colorSub);
            headerMenu.Items.Add(fontSub);
            headerMenu.Items.Add(menuPin);
            headerMenu.Items.Add(menuCollapse);
            headerMenu.Items.Add(new ToolStripSeparator());
            headerMenu.Items.Add(MenuItem("刪除便條紙", "Ctrl+D", delegate { DeleteWithConfirm(); }));
            headerMenu.Opening += delegate
            {
                menuPin.Checked = TopMost;
                menuCollapse.Text = collapsed ? "展開" : "收合";
            };
            header.ContextMenuStrip = headerMenu;

            var miUndo = MenuItem("復原", "Ctrl+Z", delegate { text.Undo(); });
            var miCut = MenuItem("剪下", "Ctrl+X", delegate { text.Cut(); });
            var miCopy = MenuItem("複製", "Ctrl+C", delegate { text.Copy(); });
            var miPaste = MenuItem("貼上", "Ctrl+V", delegate { text.PastePlain(); });
            var miSelectAll = MenuItem("全選", "Ctrl+A", delegate { text.SelectAll(); });
            textMenu = NewMenu();
            textMenu.Items.AddRange(new ToolStripItem[]
            {
                miUndo, new ToolStripSeparator(), miCut, miCopy, miPaste, new ToolStripSeparator(), miSelectAll
            });
            textMenu.Opening += delegate
            {
                miUndo.Enabled = text.CanUndo;
                miCut.Enabled = miCopy.Enabled = text.SelectionLength > 0;
                miPaste.Enabled = Clipboard.ContainsText();
            };
            text.ContextMenuStrip = textMenu;

            ActiveControl = text;
            ApplyColor();
            UpdatePinButton();
            ResumeLayout();

            ResizeEnd += delegate { manager.ScheduleSave(); };

            expandedHeight = Height;
            if (d.Collapsed)
                SetCollapsed(true);
        }

        public int ColorIndex
        {
            get { return colorIndex; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= Native.CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.TryRoundCorners(Handle);
        }

        public NoteData ToData()
        {
            return new NoteData
            {
                Id = id,
                Text = text.Text,
                X = Left,
                Y = Top,
                Width = Width,
                Height = collapsed ? expandedHeight : Height,
                ColorIndex = colorIndex,
                TopMost = TopMost,
                FontSize = fontSize,
                Collapsed = collapsed,
            };
        }

        public void FocusText()
        {
            if (collapsed) return;
            text.Focus();
        }

        public void BringToFrontForce()
        {
            if (!Visible) Show();
            // Toggling TopMost raises the window above other apps without stealing their focus rules.
            bool pinned = TopMost;
            TopMost = true;
            if (!pinned) TopMost = false;
        }

        public void CloseForReal()
        {
            closingForReal = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!closingForReal)
            {
                // Alt+F4 shouldn't silently delete or hide a note.
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    return;
                }
                // Windows is shutting down or signing out: make sure everything is on disk.
                manager.SaveNow();
            }
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.N:
                    manager.NewNote(this);
                    return true;
                case Keys.Control | Keys.D:
                    DeleteWithConfirm();
                    return true;
                case Keys.Control | Keys.Oemplus:
                case Keys.Control | Keys.Shift | Keys.Oemplus:
                case Keys.Control | Keys.Add:
                    SetFontSize(fontSize + 1);
                    return true;
                case Keys.Control | Keys.OemMinus:
                case Keys.Control | Keys.Subtract:
                    SetFontSize(fontSize - 1);
                    return true;
                case Keys.Control | Keys.D0:
                case Keys.Control | Keys.NumPad0:
                    SetFontSize(DefaultFontSize);
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---- Moving and resizing a borderless window ----

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if (m.Result == (IntPtr)Native.HTCLIENT)
                {
                    int hit = HitTestEdges(Native.PointFromLParam(m.LParam));
                    if (hit != 0)
                        m.Result = (IntPtr)hit;
                }
                return;
            }
            base.WndProc(ref m);
        }

        // Returns the resize hit-test code for a screen point near the edges, or 0.
        public int HitTestEdges(Point screenPoint)
        {
            Point p = PointToClient(screenPoint);
            int w = ClientSize.Width, h = ClientSize.Height;
            bool left = p.X < grip, right = p.X >= w - grip;

            if (collapsed)
                return left ? Native.HTLEFT : right ? Native.HTRIGHT : 0;

            int corner = Ui.S(16);
            if (p.X >= w - corner && p.Y >= h - corner)
                return Native.HTBOTTOMRIGHT;

            bool top = p.Y < grip, bottom = p.Y >= h - grip;
            if (top && left) return Native.HTTOPLEFT;
            if (top && right) return Native.HTTOPRIGHT;
            if (bottom && left) return Native.HTBOTTOMLEFT;
            if (bottom && right) return Native.HTBOTTOMRIGHT;
            if (left) return Native.HTLEFT;
            if (right) return Native.HTRIGHT;
            if (top) return Native.HTTOP;
            if (bottom) return Native.HTBOTTOM;
            return 0;
        }

        void OnHeaderMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            // Starting a window drag eats the mouse-up, so Windows may not report the
            // second click as a double-click. Detect it from the click timing instead.
            Point pos = Cursor.Position;
            Size slop = SystemInformation.DoubleClickSize;
            bool isDoubleClick = e.Clicks == 2
                || (Environment.TickCount - lastHeaderDownTick <= SystemInformation.DoubleClickTime
                    && Math.Abs(pos.X - lastHeaderDownPos.X) <= slop.Width
                    && Math.Abs(pos.Y - lastHeaderDownPos.Y) <= slop.Height);
            if (isDoubleClick)
            {
                lastHeaderDownTick = Environment.TickCount - SystemInformation.DoubleClickTime - 1;
                SetCollapsed(!collapsed);
                return;
            }
            lastHeaderDownTick = Environment.TickCount;
            lastHeaderDownPos = pos;

            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
        }

        // ---- Note actions ----

        void SetColor(int index)
        {
            if (index == colorIndex) return;
            colorIndex = index;
            ApplyColor();
            manager.ScheduleSave();
        }

        void ApplyColor()
        {
            NoteColor c = NoteColors.All[colorIndex];
            BackColor = c.Body;
            body.BackColor = c.Body;
            text.BackColor = c.Body;
            text.ForeColor = c.Ink;
            header.BackColor = c.Header;
            Color hover = Ui.Blend(c.Header, c.Ink, 0.14f);
            foreach (HeaderButton b in new[] { btnAdd, btnColor, btnPin, btnDelete })
                b.SetColors(c.Header, hover, c.Ink);
            header.Invalidate();
            body.Invalidate();
        }

        void SetPinned(bool pinned)
        {
            TopMost = pinned;
            UpdatePinButton();
            manager.ScheduleSave();
        }

        void UpdatePinButton()
        {
            btnPin.Text = TopMost ? Ui.Glyph("", "▲") : Ui.Glyph("", "△");
            btnPin.Active = TopMost;
            tip.SetToolTip(btnPin, TopMost ? "取消置頂" : "置頂（永遠顯示在最上層）");
        }

        void SetFontSize(float size)
        {
            size = Clamp(size);
            if (size == fontSize) return;
            fontSize = size;
            text.Font = new Font(Ui.TextFontName, fontSize);
            manager.ScheduleSave();
        }

        void SetCollapsed(bool value)
        {
            if (collapsed == value) return;
            collapsed = value;
            SuspendLayout();
            if (value)
            {
                expandedHeight = Height;
                body.Visible = false;
                MinimumSize = new Size(expandedMinSize.Width, headerHeight);
                Height = headerHeight;
            }
            else
            {
                body.Visible = true;
                Height = Math.Max(expandedHeight, expandedMinSize.Height);
                MinimumSize = expandedMinSize;
            }
            ResumeLayout();
            header.Invalidate();
            if (!value) FocusText();
            manager.ScheduleSave();
        }

        void DeleteWithConfirm()
        {
            if (text.Text.Trim().Length > 0)
            {
                DialogResult answer = MessageBox.Show(this,
                    "確定要刪除這張便條紙嗎？刪除後無法復原。", "刪除便條紙",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
            }
            // Defer so we aren't disposing the form from inside one of its own event handlers.
            BeginInvoke((MethodInvoker)delegate { manager.DeleteNote(this); });
        }

        void OnLinkClicked(object sender, LinkClickedEventArgs e)
        {
            // Plain clicks keep editing the text; Ctrl+click opens the link.
            if ((ModifierKeys & Keys.Control) != Keys.Control) return;
            string url = e.LinkText;
            if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                url = "http://" + url;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                return;
            try { Process.Start(url); }
            catch (Exception) { }
        }

        // ---- Painting ----

        void PaintResizeGrip(object sender, PaintEventArgs e)
        {
            NoteColor c = NoteColors.All[colorIndex];
            int dot = Math.Max(2, Ui.S(2));
            int gap = Ui.S(4);
            int right = body.ClientSize.Width - Ui.S(3);
            int bottom = body.ClientSize.Height - Ui.S(3);
            using (var brush = new SolidBrush(Ui.Blend(c.Body, c.Ink, 0.3f)))
            {
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3 - i; j++)
                        e.Graphics.FillRectangle(brush, right - dot - i * gap, bottom - dot - j * gap, dot, dot);
            }
        }

        void PaintCollapsedTitle(object sender, PaintEventArgs e)
        {
            if (!collapsed) return;
            int left = btnAdd.Right + Ui.S(4);
            int right = btnColor.Left - Ui.S(4);
            if (right <= left) return;
            TextRenderer.DrawText(e.Graphics, CollapsedTitle(), Ui.TitleFont,
                new Rectangle(left, 0, right - left, header.Height), NoteColors.All[colorIndex].Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        string CollapsedTitle()
        {
            foreach (string line in text.Text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }
            return "（空白便條紙）";
        }

        // ---- Helpers ----

        static float Clamp(float size)
        {
            return Math.Max(MinFontSize, Math.Min(MaxFontSize, size));
        }

        static ContextMenuStrip NewMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Font = Ui.MenuFont;
            menu.ImageScalingSize = new Size(Ui.S(16), Ui.S(16));
            return menu;
        }

        static ToolStripMenuItem MenuItem(string label, string shortcut, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(label, null, onClick);
            if (shortcut != null)
                item.ShortcutKeyDisplayString = shortcut;
            return item;
        }

        void AddColorItems(ToolStripItemCollection items)
        {
            for (int i = 0; i < NoteColors.All.Length; i++)
            {
                int index = i;
                var item = new ToolStripMenuItem(NoteColors.All[i].Name, NoteColors.Swatch(i), delegate { SetColor(index); });
                item.Tag = index;
                items.Add(item);
            }
        }

        void RefreshColorChecks(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                var menuItem = item as ToolStripMenuItem;
                if (menuItem != null && menuItem.Tag is int)
                    menuItem.Checked = (int)menuItem.Tag == colorIndex;
            }
        }
    }
}
