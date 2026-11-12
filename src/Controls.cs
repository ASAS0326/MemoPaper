using System;
using System.Drawing;
using System.Windows.Forms;

namespace StickyNotes
{
    // Panel that lets mouse hits near the note's edges fall through to the form, so the
    // borderless form can still be resized from those edges.
    class EdgePanel : Panel
    {
        public EdgePanel()
        {
            ResizeRedraw = true;
            DoubleBuffered = true;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST)
            {
                NoteForm form = FindForm() as NoteForm;
                if (form != null && form.HitTestEdges(Native.PointFromLParam(m.LParam)) != 0)
                {
                    m.Result = (IntPtr)Native.HTTRANSPARENT;
                    return;
                }
            }
            base.WndProc(ref m);
        }
    }

    class HeaderButton : Label
    {
        Color normalBack;
        Color hoverBack;
        bool hovering;
        bool active;

        public HeaderButton(string glyph, string tipText, ToolTip tip, DockStyle dock)
        {
            // Every click counts; rapid clicks should not be swallowed as a double-click.
            SetStyle(ControlStyles.StandardDoubleClick, false);
            AutoSize = false;
            UseMnemonic = false;
            Text = glyph;
            Font = Ui.IconFont;
            TextAlign = ContentAlignment.MiddleCenter;
            Dock = dock;
            Width = Ui.S(32);
            Cursor = Cursors.Hand;
            tip.SetToolTip(this, tipText);
        }

        public bool Active
        {
            get { return active; }
            set { active = value; UpdateBack(); }
        }

        public void SetColors(Color back, Color hover, Color fore)
        {
            normalBack = back;
            hoverBack = hover;
            ForeColor = fore;
            UpdateBack();
        }

        void UpdateBack()
        {
            if (active && hovering)
                BackColor = Ui.Blend(hoverBack, ForeColor, 0.1f);
            else if (active || hovering)
                BackColor = hoverBack;
            else
                BackColor = normalBack;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovering = true;
            UpdateBack();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovering = false;
            UpdateBack();
            base.OnMouseLeave(e);
        }
    }

    // Plain-text RichTextBox: pastes text only, ignores RichEdit's formatting shortcuts
    // (the note is saved as plain text), and reports Ctrl+wheel instead of zooming.
    class NoteTextBox : RichTextBox
    {
        public Action<int> CtrlWheel;

        protected override void OnHandleCreated(EventArgs e)
        {
            // Keep our font for both CJK and Latin text instead of letting RichEdit swap fonts.
            LanguageOption &= ~(RichTextBoxLanguageOptions.AutoFont | RichTextBoxLanguageOptions.DualFont);
            base.OnHandleCreated(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEWHEEL && (ModifierKeys & Keys.Control) == Keys.Control)
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (CtrlWheel != null)
                    CtrlWheel(delta);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.V:
                case Keys.Shift | Keys.Insert:
                    PastePlain();
                    return true;

                case Keys.Control | Keys.B:
                case Keys.Control | Keys.I:
                case Keys.Control | Keys.U:
                case Keys.Control | Keys.L:
                case Keys.Control | Keys.E:
                case Keys.Control | Keys.R:
                case Keys.Control | Keys.J:
                case Keys.Control | Keys.D1:
                case Keys.Control | Keys.D2:
                case Keys.Control | Keys.D5:
                case Keys.Control | Keys.Shift | Keys.A:
                case Keys.Control | Keys.Shift | Keys.L:
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        public void PastePlain()
        {
            try
            {
                if (!ReadOnly && Clipboard.ContainsText())
                    SelectedText = Clipboard.GetText();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Clipboard is busy in another app; ignore this paste.
            }
        }
    }
}
