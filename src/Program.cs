using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace StickyNotes
{
    static class Program
    {
        const string MutexName = "DesktopStickyNotes.SingleInstance";
        const string ActivateEventName = "DesktopStickyNotes.Activate";

        public static Icon AppIcon;
        public static Icon TrayIcon;

        [STAThread]
        static void Main()
        {
            bool isFirstInstance;
            using (var mutex = new Mutex(true, MutexName, out isFirstInstance))
            {
                if (!isFirstInstance)
                {
                    // Already running: ask that copy to bring its notes to the front.
                    try
                    {
                        using (EventWaitHandle activate = EventWaitHandle.OpenExisting(ActivateEventName))
                        {
                            Native.AllowSetForegroundWindow(-1);
                            activate.Set();
                        }
                    }
                    catch (WaitHandleCannotBeOpenedException) { }
                    return;
                }

                using (var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName))
                {
                    // Built without a target-framework attribute, .NET runs in 4.0 compatibility mode,
                    // which doesn't offer TLS 1.2 by default. Google requires it.
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    ServicePointManager.Expect100Continue = false;

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.ThreadException += (s, e) => ReportError(e.Exception);

                    Ui.Init();
                    AppIcon = LoadIcon(Size.Empty);
                    TrayIcon = LoadIcon(SystemInformation.SmallIconSize);

                    Application.Run(new NoteManager(activate));
                }
            }
        }

        static Icon LoadIcon(Size size)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("StickyNotes.app.ico"))
                {
                    if (s != null)
                        return size.IsEmpty ? new Icon(s) : new Icon(s, size);
                }
            }
            catch (Exception) { }
            return SystemIcons.Application;
        }

        // Appends to error.log in the data folder without bothering the user.
        public static void LogError(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(NoteStore.Folder);
                File.AppendAllText(Path.Combine(NoteStore.Folder, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch (Exception) { }
        }

        static void ReportError(Exception ex)
        {
            LogError(ex);
            MessageBox.Show("發生錯誤：" + ex.Message + "\n\n詳細資訊已記錄在 error.log。", "桌面便條紙",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
