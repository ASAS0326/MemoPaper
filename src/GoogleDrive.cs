using System;
using System.Collections.Generic;
using System.Text;

namespace StickyNotes
{
    class DriveFile
    {
        public string Id;
        public long Version;   // increases on every change to the file
    }

    // Reads and writes notes.json in the app's hidden folder in Google Drive (appDataFolder).
    // The folder is invisible in the Drive UI and other apps can't read it.
    class GoogleDrive
    {
        const string FileName = "notes.json";
        const string FilesApi = "https://www.googleapis.com/drive/v3/files";
        const string UploadApi = "https://www.googleapis.com/upload/drive/v3/files";

        readonly GoogleAccount account;

        public GoogleDrive(GoogleAccount account)
        {
            this.account = account;
        }

        // Normally one file. Two computers syncing for the very first time at the same moment
        // can each create one; the caller merges them and removes the extras.
        public List<DriveFile> ListNoteFiles()
        {
            string url = FilesApi +
                "?spaces=appDataFolder&orderBy=createdTime&pageSize=100" +
                "&fields=" + Http.Escape("files(id,version)") +
                "&q=" + Http.Escape("name = '" + FileName + "' and trashed = false");
            Dictionary<string, object> r = Json.Parse(Call("GET", url, null, null));

            var files = new List<DriveFile>();
            foreach (Dictionary<string, object> f in Json.Objects(r, "files"))
            {
                string id = Json.Str(f, "id");
                if (!string.IsNullOrEmpty(id))
                    files.Add(new DriveFile { Id = id, Version = Json.Long(f, "version") });
            }
            return files;
        }

        public string Download(string fileId)
        {
            return Call("GET", FilesApi + "/" + Http.Escape(fileId) + "?alt=media", null, null);
        }

        public DriveFile Create(string json)
        {
            string boundary = "StickyNotes" + Guid.NewGuid().ToString("N");
            string metadata = Json.Write(new Dictionary<string, object>
            {
                { "name", FileName },
                { "parents", new[] { "appDataFolder" } },
                { "mimeType", "application/json" },
            });
            string body =
                "--" + boundary + "\r\n" +
                "Content-Type: application/json; charset=UTF-8\r\n\r\n" +
                metadata + "\r\n" +
                "--" + boundary + "\r\n" +
                "Content-Type: application/json; charset=UTF-8\r\n\r\n" +
                json + "\r\n" +
                "--" + boundary + "--\r\n";
            return ToFile(Call("POST", UploadApi + "?uploadType=multipart&fields=id,version",
                "multipart/related; boundary=" + boundary, Utf8(body)));
        }

        public DriveFile Update(string fileId, string json)
        {
            return ToFile(Call("PATCH", UploadApi + "/" + Http.Escape(fileId) + "?uploadType=media&fields=id,version",
                "application/json; charset=UTF-8", Utf8(json)));
        }

        public void Delete(string fileId)
        {
            Call("DELETE", FilesApi + "/" + Http.Escape(fileId), null, null);
        }

        string Call(string method, string url, string contentType, byte[] body)
        {
            try
            {
                return Http.Request(method, url, account.AccessToken(false), contentType, body);
            }
            catch (HttpError ex)
            {
                if (ex.Status != 401) throw;
            }
            // The access token was rejected early (e.g. the PC slept past its expiry); get a fresh one once.
            return Http.Request(method, url, account.AccessToken(true), contentType, body);
        }

        static DriveFile ToFile(string response)
        {
            Dictionary<string, object> r = Json.Parse(response);
            return new DriveFile { Id = Json.Str(r, "id"), Version = Json.Long(r, "version") };
        }

        static byte[] Utf8(string text)
        {
            return new UTF8Encoding(false).GetBytes(text);
        }
    }
}
