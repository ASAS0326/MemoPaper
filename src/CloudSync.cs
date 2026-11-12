using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace StickyNotes
{
    enum SyncState { Off, SigningIn, Syncing, Synced, Offline, Failed }

    // Keeps the notes on this computer and the copy in Google Drive in step.
    // Everything here runs on the UI thread; only the network calls go to the thread pool.
    class CloudSync : IDisposable
    {
        const int PollInterval = 30 * 1000;   // how often to look for edits made on other computers
        const int ChangeDelay = 2 * 1000;     // wait for typing to pause before uploading

        readonly NoteManager host;
        readonly SynchronizationContext ui;
        readonly Timer pollTimer = new Timer();
        readonly Timer soonTimer = new Timer();

        GoogleAccount account;
        GoogleDrive drive;
        CancellationTokenSource signInCancel;
        Task running;
        bool again;
        bool firstSync;
        bool stopped;
        long knownVersion = -1;   // Drive version we last merged with: 0 = no file yet, -1 = unknown
        int syncedChange = -1;    // host.ChangeCount at the last successful sync
        string lastNotice;

        public event EventHandler StatusChanged;

        public CloudSync(NoteManager host)
        {
            this.host = host;
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            pollTimer.Interval = PollInterval;
            pollTimer.Tick += delegate { Run(); };
            soonTimer.Interval = ChangeDelay;
            soonTimer.Tick += delegate { soonTimer.Stop(); Run(); };

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }

        public SyncState State { get; private set; }
        public DateTime LastSynced { get; private set; }
        public string LastError { get; private set; }

        public bool IsConnected
        {
            get { return account != null; }
        }

        public string Email
        {
            get { return account != null ? account.Email : null; }
        }

        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case SyncState.SigningIn: return "等待瀏覽器登入…";
                    case SyncState.Syncing: return "同步中…";
                    case SyncState.Synced: return "已同步 " + LastSynced.ToString("HH:mm");
                    case SyncState.Offline: return "無法連線，稍後自動重試";
                    case SyncState.Failed: return "同步失敗：" + LastError;
                    default: return null;
                }
            }
        }

        // ---- Connecting ----

        // Picks up the sign-in saved on this computer, if any.
        public void Start()
        {
            try
            {
                GoogleAccount saved = GoogleAuth.LoadSavedAccount();
                if (saved != null)
                    Connect(saved, false);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        public async Task<GoogleAccount> SignInAsync(GoogleClient client)
        {
            CancelSignIn();
            var cancel = new CancellationTokenSource();
            signInCancel = cancel;
            SetState(SyncState.SigningIn, null);

            GoogleAccount signedIn;
            try
            {
                signedIn = await Task.Run(() => GoogleAuth.SignIn(client, cancel.Token));
            }
            catch (Exception)
            {
                if (signInCancel == cancel)
                {
                    signInCancel = null;
                    SetState(SyncState.Off, null);
                }
                throw;
            }
            if (signInCancel != cancel)
                throw new OperationCanceledException();
            signInCancel = null;

            GoogleAuth.SaveAccount(signedIn);
            Connect(signedIn, true);
            return signedIn;
        }

        public void CancelSignIn()
        {
            if (signInCancel == null) return;
            signInCancel.Cancel();
            signInCancel = null;
            SetState(SyncState.Off, null);
        }

        // Stops syncing on this computer. The notes here and in the cloud are left alone.
        public void Disconnect()
        {
            CancelSignIn();
            if (account == null) return;
            account = null;
            drive = null;
            pollTimer.Stop();
            soonTimer.Stop();
            lastNotice = null;
            GoogleAuth.ForgetSavedAccount();
            SetState(SyncState.Off, null);
        }

        void Connect(GoogleAccount signedIn, bool freshSignIn)
        {
            account = signedIn;
            drive = new GoogleDrive(signedIn);
            knownVersion = -1;
            syncedChange = -1;
            firstSync = freshSignIn;
            lastNotice = null;
            pollTimer.Start();
            SetState(SyncState.Syncing, null);
            Run();
        }

        // ---- Triggers ----

        // Called after every edit; uploads once typing pauses.
        public void ScheduleSoon()
        {
            if (account == null || stopped) return;
            soonTimer.Stop();
            soonTimer.Start();
        }

        public void SyncNow()
        {
            soonTimer.Stop();
            Run();
        }

        // On exit: push pending edits, but don't hold the exit up for long.
        public Task FlushAsync(int timeoutMs)
        {
            soonTimer.Stop();
            if (account == null || stopped || (running == null && host.ChangeCount == syncedChange))
                return Task.FromResult(0);
            return Task.WhenAny(Run(), Task.Delay(timeoutMs));
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
                ui.Post(delegate { ScheduleSoon(); }, null);
        }

        void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            if (e.IsAvailable)
                ui.Post(delegate { ScheduleSoon(); }, null);
        }

        // ---- Sync rounds ----

        // Starts a round, or asks the one in progress to go again when it finishes.
        Task Run()
        {
            if (account == null || stopped)
                return Task.FromResult(0);
            if (running != null)
            {
                again = true;
                return running;
            }
            Task t = RunRounds();
            running = t.IsCompleted ? null : t;
            return t;
        }

        async Task RunRounds()
        {
            try
            {
                do
                {
                    again = false;
                    await SyncOnce();
                }
                while (again && account != null && !stopped);
            }
            finally
            {
                running = null;
            }
        }

        async Task SyncOnce()
        {
            GoogleAccount acc = account;
            GoogleDrive d = drive;
            if (acc == null || stopped) return;
            if (State != SyncState.Synced)
                SetState(SyncState.Syncing, null);

            Exception error = null;
            try
            {
                bool changedHere = host.ChangeCount != syncedChange;
                long known = knownVersion;
                FetchResult fetch = await Task.Run(() => Fetch(d, changedHere, known));
                if (account != acc || stopped) return;

                if (!fetch.Unchanged)
                {
                    if (firstSync && fetch.Doc.Notes.Count > 0)
                        host.DiscardPristineNotes();
                    firstSync = false;

                    List<NoteData> local = host.CaptureNotes();
                    if (local == null) return;   // Windows is closing the notes
                    int change = host.ChangeCount;

                    MergeResult merged = SyncMerge.Merge(local, host.DeletedNotes, fetch.Doc, Clock.Now());
                    host.ApplyMerge(merged);

                    string json = merged.Doc.ToJson();
                    bool upload = fetch.Files.Count == 0 ? !merged.Doc.IsEmpty : fetch.Files.Count > 1 || json != fetch.Json;
                    if (upload)
                    {
                        List<DriveFile> files = fetch.Files;
                        long version = await Task.Run(() => Upload(d, files, json));
                        if (account != acc || stopped) return;
                        if (version < 0)
                        {
                            // Another computer wrote while we were merging. Merge again with its version.
                            again = true;
                            return;
                        }
                        knownVersion = version;
                        host.MarkUploaded(merged.Doc);
                    }
                    else
                    {
                        knownVersion = fetch.Files.Count == 1 ? fetch.Files[0].Version : 0;
                    }
                    syncedChange = change;

                    if (merged.Conflicts > 0)
                        host.NotifyCloud("有 " + merged.Conflicts + " 張便條紙在不同電腦同時被修改，兩個版本都保留下來了，請確認後刪除不要的那張。", ToolTipIcon.Info);
                }
                LastSynced = DateTime.Now;
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (account != acc) return;
            if (error == null)
            {
                lastNotice = null;
                SetState(SyncState.Synced, null);
            }
            else
            {
                HandleError(error);
            }
        }

        class FetchResult
        {
            public List<DriveFile> Files;
            public bool Unchanged;
            public SyncDoc Doc;
            public string Json;   // Doc as the cloud has it now; null forces an upload
        }

        static FetchResult Fetch(GoogleDrive d, bool changedHere, long knownVersion)
        {
            var r = new FetchResult();
            r.Files = d.ListNoteFiles();

            long current = r.Files.Count == 0 ? 0 : r.Files.Count == 1 ? r.Files[0].Version : -2;
            if (!changedHere && current >= 0 && current == knownVersion)
            {
                r.Unchanged = true;
                return r;
            }

            r.Doc = new SyncDoc();
            bool unreadable = false;
            foreach (DriveFile f in r.Files)
            {
                string text = d.Download(f.Id);
                SyncDoc part;
                try
                {
                    part = SyncDoc.Parse(text);
                }
                catch (NewerFormatException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Damaged file: every computer keeps its own full copy, so rewriting it is safe.
                    unreadable = true;
                    continue;
                }
                r.Doc.Absorb(part);
            }
            r.Json = unreadable ? null : r.Doc.ToJson();
            return r;
        }

        // Returns the file's new version, or -1 if it changed since we downloaded it.
        static long Upload(GoogleDrive d, List<DriveFile> files, string json)
        {
            List<DriveFile> now = d.ListNoteFiles();
            if (now.Count != files.Count) return -1;
            for (int i = 0; i < now.Count; i++)
                if (now[i].Id != files[i].Id || now[i].Version != files[i].Version)
                    return -1;

            DriveFile saved = files.Count > 0 ? d.Update(files[0].Id, json) : d.Create(json);
            for (int i = 1; i < files.Count; i++)
            {
                try { d.Delete(files[i].Id); }
                catch (HttpError) { }
            }
            return saved.Version;
        }

        void HandleError(Exception ex)
        {
            if (ex is AuthRevokedException)
            {
                Disconnect();
                host.NotifyCloud("Google 授權已失效，雲端同步已暫停。請從工作列圖示的「Google 雲端同步」重新連線。", ToolTipIcon.Warning);
                return;
            }

            var http = ex as HttpError;
            if (ex is WebException || ex is IOException
                || (http != null && (http.Status == 429 || http.Status >= 500 || IsRateLimit(http))))
            {
                SetState(SyncState.Offline, null);
                return;
            }

            string message;
            if (http != null && http.Status == 403 && (http.Body.Contains("SERVICE_DISABLED") || http.Body.Contains("accessNotConfigured")))
                message = "Google Cloud 專案還沒啟用 Google Drive API，請到 Google Cloud Console 啟用。";
            else if (http != null && http.Status == 403 && (http.Body.Contains("insufficientPermissions") || http.Body.Contains("SCOPE_INSUFFICIENT")))
                message = "權限不足，請中斷連線後重新連線，並勾選雲端硬碟權限。";
            else
            {
                message = ex.Message;
                if (!(ex is NewerFormatException) && http == null)
                    Program.LogError(ex);
            }

            SetState(SyncState.Failed, message);
            if (message != lastNotice)
            {
                lastNotice = message;
                host.NotifyCloud("雲端同步失敗：" + message, ToolTipIcon.Warning);
            }
        }

        static bool IsRateLimit(HttpError http)
        {
            return http.Status == 403 && (http.Body.Contains("rateLimitExceeded") || http.Body.Contains("userRateLimitExceeded"));
        }

        void SetState(SyncState state, string error)
        {
            State = state;
            LastError = error;
            EventHandler handler = StatusChanged;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            stopped = true;
            StatusChanged = null;
            if (signInCancel != null)
                signInCancel.Cancel();
            pollTimer.Dispose();
            soonTimer.Dispose();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        }
    }
}
