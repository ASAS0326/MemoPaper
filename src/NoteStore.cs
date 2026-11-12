using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace StickyNotes
{
    class NoteData
    {
        public string Id;
        public string Text = "";
        public int X, Y, Width, Height;
        public int ColorIndex;
        public bool TopMost;
        public float FontSize;
        public bool Collapsed;
    }

    static class NoteStore
    {
        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopStickyNotes");

        static readonly string DataPath = Path.Combine(Folder, "notes.xml");
        static readonly string BackupPath = Path.Combine(Folder, "notes.bak.xml");
        static readonly string TempPath = Path.Combine(Folder, "notes.tmp.xml");

        // Returns null when nothing has been saved yet (first run).
        public static List<NoteData> Load(out string warning)
        {
            warning = null;
            if (!File.Exists(DataPath) && !File.Exists(BackupPath))
                return null;

            List<NoteData> notes = TryRead(DataPath);
            if (notes != null)
                return notes;

            string keptCopy = null;
            if (File.Exists(DataPath))
            {
                keptCopy = Path.Combine(Folder, "notes.corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".xml");
                try { File.Copy(DataPath, keptCopy, true); }
                catch (IOException) { keptCopy = null; }
            }

            notes = TryRead(BackupPath);
            if (notes != null)
            {
                warning = "便條紙資料檔讀取失敗，已從備份還原。";
                return notes;
            }

            warning = keptCopy != null
                ? "無法讀取便條紙資料，原檔已另存為 " + Path.GetFileName(keptCopy) + "。"
                : "無法讀取便條紙資料。";
            return new List<NoteData>();
        }

        static List<NoteData> TryRead(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                XDocument doc;
                var settings = new XmlReaderSettings { CheckCharacters = false };
                using (XmlReader reader = XmlReader.Create(path, settings))
                    doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);

                var notes = new List<NoteData>();
                foreach (XElement e in doc.Root.Elements("Note"))
                {
                    notes.Add(new NoteData
                    {
                        Id = (string)e.Attribute("id"),
                        Text = (string)e.Element("Text") ?? "",
                        X = (int?)e.Attribute("x") ?? 0,
                        Y = (int?)e.Attribute("y") ?? 0,
                        Width = (int?)e.Attribute("width") ?? 0,
                        Height = (int?)e.Attribute("height") ?? 0,
                        ColorIndex = (int?)e.Attribute("color") ?? 0,
                        TopMost = (bool?)e.Attribute("topmost") ?? false,
                        FontSize = (float?)e.Attribute("fontSize") ?? 0f,
                        Collapsed = (bool?)e.Attribute("collapsed") ?? false,
                    });
                }
                return notes;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Save(List<NoteData> notes)
        {
            Directory.CreateDirectory(Folder);

            var root = new XElement("StickyNotes", new XAttribute("version", 1));
            foreach (NoteData n in notes)
            {
                root.Add(new XElement("Note",
                    new XAttribute("id", n.Id),
                    new XAttribute("x", n.X),
                    new XAttribute("y", n.Y),
                    new XAttribute("width", n.Width),
                    new XAttribute("height", n.Height),
                    new XAttribute("color", n.ColorIndex),
                    new XAttribute("topmost", n.TopMost),
                    new XAttribute("fontSize", n.FontSize),
                    new XAttribute("collapsed", n.Collapsed),
                    new XElement("Text", n.Text ?? "")));
            }

            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = new UTF8Encoding(false),
                CheckCharacters = false,
            };
            using (XmlWriter writer = XmlWriter.Create(TempPath, settings))
                new XDocument(root).Save(writer);

            // Write to a temp file first, then swap it in, so a crash mid-save never
            // leaves a half-written notes.xml. The previous version becomes the backup.
            if (!File.Exists(DataPath))
            {
                File.Move(TempPath, DataPath);
                return;
            }
            try
            {
                File.Replace(TempPath, DataPath, BackupPath, true);
            }
            catch (IOException)
            {
                // File.Replace can fail while an indexer or antivirus briefly holds the file.
                File.Copy(TempPath, DataPath, true);
                File.Delete(TempPath);
            }
        }
    }
}
