using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace StickyNotes
{
    // The OAuth client created in Google Cloud Console ("Desktop app" type).
    class GoogleClient
    {
        public string Id;
        public string Secret;
    }

    // Thrown when Google no longer accepts the saved sign-in (revoked, expired or client deleted).
    class AuthRevokedException : Exception
    {
        public AuthRevokedException(string message) : base(message) { }
    }

    // A signed-in Google account. Hands out short-lived access tokens, refreshing them as needed.
    class GoogleAccount
    {
        public readonly GoogleClient Client;
        public readonly string RefreshToken;
        public readonly string Email;

        readonly object gate = new object();
        string accessToken;
        DateTime accessExpiresUtc;

        public GoogleAccount(GoogleClient client, string refreshToken, string email)
        {
            Client = client;
            RefreshToken = refreshToken;
            Email = email;
        }

        public string AccessToken(bool forceRefresh)
        {
            lock (gate)
            {
                if (!forceRefresh && accessToken != null && DateTime.UtcNow < accessExpiresUtc)
                    return accessToken;

                Dictionary<string, object> r;
                try
                {
                    r = GoogleAuth.PostForm(GoogleAuth.TokenUrl,
                        "client_id=" + Http.Escape(Client.Id) +
                        "&client_secret=" + Http.Escape(Client.Secret) +
                        "&refresh_token=" + Http.Escape(RefreshToken) +
                        "&grant_type=refresh_token");
                }
                catch (HttpError ex)
                {
                    string code = ex.OAuthError;
                    if (code == "invalid_grant" || code == "invalid_client" || code == "unauthorized_client" || code == "deleted_client")
                        throw new AuthRevokedException("Google 授權已失效或被撤銷。");
                    throw;
                }
                SetAccessToken(Json.Str(r, "access_token"), Json.Long(r, "expires_in"));
                return accessToken;
            }
        }

        internal void SetAccessToken(string token, long expiresInSeconds)
        {
            if (string.IsNullOrEmpty(token))
                throw new InvalidDataException("Google 沒有回傳存取權杖。");
            lock (gate)
            {
                accessToken = token;
                accessExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(120, expiresInSeconds) - 60);
            }
        }
    }

    static class GoogleAuth
    {
        public const string TokenUrl = "https://oauth2.googleapis.com/token";
        const string AuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth";

        // drive.appdata only reaches this app's own hidden folder, never the user's other files.
        const string DriveScope = "https://www.googleapis.com/auth/drive.appdata";
        const string Scopes = "openid email " + DriveScope;

        const string ClientResource = "StickyNotes.google-client.json";
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DesktopStickyNotes.GoogleToken");

        static string ClientPath { get { return Path.Combine(NoteStore.Folder, "google-client.json"); } }
        static string TokenPath { get { return Path.Combine(NoteStore.Folder, "google-token.dat"); } }

        // ---- OAuth client settings ----

        // Looks for an imported client file first, then one built into the exe. Null if neither exists.
        public static GoogleClient LoadClient()
        {
            string error;
            try
            {
                if (File.Exists(ClientPath))
                {
                    GoogleClient imported = ParseClient(File.ReadAllText(ClientPath, Encoding.UTF8), out error);
                    if (imported != null) return imported;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ClientResource))
            {
                if (s == null) return null;
                using (var reader = new StreamReader(s, Encoding.UTF8))
                    return ParseClient(reader.ReadToEnd(), out error);
            }
        }

        // Validates the JSON downloaded from Google Cloud Console and keeps a copy for next time.
        public static GoogleClient ImportClient(string path, out string error)
        {
            string json;
            try
            {
                json = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                error = "無法讀取檔案：" + ex.Message;
                return null;
            }

            GoogleClient client = ParseClient(json, out error);
            if (client == null) return null;

            Directory.CreateDirectory(NoteStore.Folder);
            File.WriteAllText(ClientPath, json, new UTF8Encoding(false));
            return client;
        }

        static GoogleClient ParseClient(string json, out string error)
        {
            error = null;
            Dictionary<string, object> root;
            try { root = Json.Parse(json); }
            catch (Exception) { root = null; }
            if (root == null)
            {
                error = "這不是有效的 JSON 檔案。";
                return null;
            }

            Dictionary<string, object> installed = Json.Obj(root, "installed");
            if (installed == null)
            {
                error = Json.Obj(root, "web") != null
                    ? "這是「網頁應用程式」類型的用戶端，請在 Google Cloud Console 改建立「電腦版應用程式」類型。"
                    : "檔案裡找不到 OAuth 用戶端資訊，請確認下載的是 OAuth 用戶端 ID 的 JSON 檔。";
                return null;
            }

            string id = Json.Str(installed, "client_id");
            string secret = Json.Str(installed, "client_secret");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret))
            {
                error = "JSON 檔缺少 client_id 或 client_secret。";
                return null;
            }
            return new GoogleClient { Id = id, Secret = secret };
        }

        // ---- Saved sign-in (encrypted with the Windows account's DPAPI key) ----

        public static GoogleAccount LoadSavedAccount()
        {
            if (!File.Exists(TokenPath)) return null;
            GoogleClient client = LoadClient();
            if (client == null) return null;

            Dictionary<string, object> saved;
            try
            {
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), Entropy, DataProtectionScope.CurrentUser);
                saved = Json.Parse(Encoding.UTF8.GetString(plain));
            }
            catch (Exception)
            {
                return null;
            }

            // A token only works with the client it was issued to.
            string refreshToken = Json.Str(saved, "refresh_token");
            if (string.IsNullOrEmpty(refreshToken) || Json.Str(saved, "client_id") != client.Id)
                return null;
            return new GoogleAccount(client, refreshToken, Json.Str(saved, "email"));
        }

        public static void SaveAccount(GoogleAccount account)
        {
            string json = Json.Write(new Dictionary<string, object>
            {
                { "client_id", account.Client.Id },
                { "refresh_token", account.RefreshToken },
                { "email", account.Email },
            });
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(NoteStore.Folder);
            File.WriteAllBytes(TokenPath, data);
        }

        // Only forgets the sign-in on this computer. Revoking at Google would also
        // sign out every other computer, so that is left to the Google account settings.
        public static void ForgetSavedAccount()
        {
            try { File.Delete(TokenPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---- Browser sign-in (OAuth for installed apps: loopback redirect + PKCE) ----

        // Blocks until the user finishes in the browser. Run it off the UI thread.
        public static GoogleAccount SignIn(GoogleClient client, CancellationToken cancel)
        {
            string verifier = RandomToken(32);
            string challenge = Base64Url(Sha256(Encoding.ASCII.GetBytes(verifier)));
            string state = RandomToken(16);

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                string redirectUri = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
                string url = AuthorizeUrl +
                    "?client_id=" + Http.Escape(client.Id) +
                    "&redirect_uri=" + Http.Escape(redirectUri) +
                    "&response_type=code" +
                    "&scope=" + Http.Escape(Scopes) +
                    "&code_challenge=" + challenge +
                    "&code_challenge_method=S256" +
                    "&access_type=offline" +
                    "&prompt=consent" +
                    "&state=" + state;
                Process.Start(url);

                string code = WaitForRedirect(listener, state, cancel);
                cancel.ThrowIfCancellationRequested();

                Dictionary<string, object> r = PostForm(TokenUrl,
                    "code=" + Http.Escape(code) +
                    "&client_id=" + Http.Escape(client.Id) +
                    "&client_secret=" + Http.Escape(client.Secret) +
                    "&redirect_uri=" + Http.Escape(redirectUri) +
                    "&grant_type=authorization_code" +
                    "&code_verifier=" + verifier);

                // Google lets people untick individual permissions on the consent screen.
                string granted = Json.Str(r, "scope") ?? "";
                if (granted.IndexOf(DriveScope, StringComparison.Ordinal) < 0)
                    throw new InvalidOperationException("登入時沒有允許存取 Google 雲端硬碟，請重新連線並勾選雲端硬碟的權限。");

                string refreshToken = Json.Str(r, "refresh_token");
                if (string.IsNullOrEmpty(refreshToken))
                    throw new InvalidOperationException("Google 沒有回傳長期授權，請再試一次。");

                var account = new GoogleAccount(client, refreshToken, EmailFromIdToken(Json.Str(r, "id_token")));
                account.SetAccessToken(Json.Str(r, "access_token"), Json.Long(r, "expires_in"));
                return account;
            }
            finally
            {
                listener.Stop();
            }
        }

        static string WaitForRedirect(TcpListener listener, string state, CancellationToken cancel)
        {
            DateTime deadline = DateTime.UtcNow.AddMinutes(5);
            // Browsers open spare connections ahead of time, and the real request may arrive on any
            // of them, so keep them all and serve whichever one actually sends something.
            var open = new List<TcpClient>();
            try
            {
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("等候 Google 登入逾時，請再試一次。");

                    while (listener.Pending())
                    {
                        open.Add(listener.AcceptTcpClient());
                        if (open.Count > 16)
                        {
                            open[0].Close();
                            open.RemoveAt(0);
                        }
                    }

                    TcpClient ready = open.FirstOrDefault(c => c.Available > 0 || c.Client.Poll(0, SelectMode.SelectRead));
                    if (ready == null)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    open.Remove(ready);

                    string code = HandleRedirect(ready, state);
                    if (code != null)
                        return code;
                }
            }
            finally
            {
                foreach (TcpClient c in open)
                    c.Close();
            }
        }

        // Answers one request to the loopback address. Returns the authorization code, or null
        // if this wasn't the redirect we're waiting for.
        static string HandleRedirect(TcpClient tcp, string state)
        {
            using (tcp)
            {
                tcp.ReceiveTimeout = 5000;
                tcp.SendTimeout = 5000;
                NetworkStream stream = tcp.GetStream();

                string target;
                try { target = ReadRequestTarget(stream); }
                catch (IOException) { return null; }

                Dictionary<string, string> query = ParseQuery(target);
                string code, error, gotState;
                query.TryGetValue("code", out code);
                query.TryGetValue("error", out error);
                query.TryGetValue("state", out gotState);

                if (code == null && error == null)
                {
                    WriteResponse(stream, "404 Not Found", "");   // e.g. /favicon.ico
                    return null;
                }
                if (gotState != state)
                {
                    WriteResponse(stream, "400 Bad Request", Page("登入資訊不符", "請回到桌面便條紙重新連線。"));
                    return null;
                }
                if (error != null)
                {
                    WriteResponse(stream, "200 OK", Page("沒有完成授權", "可以關閉這個分頁。"));
                    if (error == "access_denied")
                        throw new OperationCanceledException("你取消了 Google 授權。");
                    throw new InvalidOperationException("Google 授權失敗：" + error);
                }

                WriteResponse(stream, "200 OK", Page("已連線到 Google 雲端硬碟", "可以關閉這個分頁，回到桌面便條紙。"));
                return code;
            }
        }

        static string ReadRequestTarget(Stream stream)
        {
            var buffer = new byte[4096];
            var data = new MemoryStream();
            while (data.Length < 65536)
            {
                int n = stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                data.Write(buffer, 0, n);
                if (Encoding.ASCII.GetString(data.GetBuffer(), 0, (int)data.Length).Contains("\r\n\r\n"))
                    break;
            }
            string text = Encoding.ASCII.GetString(data.ToArray());
            int eol = text.IndexOf("\r\n", StringComparison.Ordinal);
            string[] parts = (eol >= 0 ? text.Substring(0, eol) : text).Split(' ');
            return parts.Length >= 2 ? parts[1] : "";
        }

        static Dictionary<string, string> ParseQuery(string target)
        {
            var result = new Dictionary<string, string>();
            int q = target.IndexOf('?');
            if (q < 0) return result;
            foreach (string pair in target.Substring(q + 1).Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key = Unescape(eq < 0 ? pair : pair.Substring(0, eq));
                result[key] = eq < 0 ? "" : Unescape(pair.Substring(eq + 1));
            }
            return result;
        }

        static string Unescape(string s)
        {
            return Uri.UnescapeDataString(s.Replace('+', ' '));
        }

        static void WriteResponse(Stream stream, string status, string html)
        {
            byte[] body = Encoding.UTF8.GetBytes(html);
            byte[] head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + status + "\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n");
            try
            {
                stream.Write(head, 0, head.Length);
                stream.Write(body, 0, body.Length);
                stream.Flush();
            }
            catch (IOException) { }
        }

        static string Page(string title, string message)
        {
            return "<!doctype html><html lang=\"zh-Hant\"><head><meta charset=\"utf-8\"><title>" + title + "</title>" +
                "<style>body{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;" +
                "background:#FFF6B8;color:#2E2A1F;font-family:'Microsoft JhengHei UI','Segoe UI',sans-serif}" +
                "div{background:#fff;border-top:10px solid #F9D65C;border-radius:8px;padding:28px 40px;" +
                "box-shadow:0 4px 18px rgba(0,0,0,.12)}h1{font-size:20px;margin:0 0 8px}p{margin:0}</style></head>" +
                "<body><div><h1>" + title + "</h1><p>" + message + "</p></div></body></html>";
        }

        // ---- Helpers ----

        internal static Dictionary<string, object> PostForm(string url, string form)
        {
            string text = Http.Request("POST", url, null, "application/x-www-form-urlencoded", Encoding.ASCII.GetBytes(form));
            Dictionary<string, object> r = Json.Parse(text);
            if (r == null) throw new InvalidDataException("Google 回傳了無法辨識的資料。");
            return r;
        }

        // The id_token comes straight from Google's token endpoint over HTTPS, so reading
        // the email out of it without verifying the signature is fine.
        static string EmailFromIdToken(string idToken)
        {
            if (string.IsNullOrEmpty(idToken)) return null;
            string[] parts = idToken.Split('.');
            if (parts.Length < 2) return null;
            try
            {
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                return Json.Str(Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))), "email");
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string RandomToken(int byteCount)
        {
            var bytes = new byte[byteCount];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(bytes);
            return Base64Url(bytes);
        }

        static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        static byte[] Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
                return sha.ComputeHash(data);
        }
    }
}
