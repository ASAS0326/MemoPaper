using System;
using System.Collections.Generic;
using System.Linq;

namespace StickyNotes
{
    static class Clock
    {
        static readonly long EpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        // Milliseconds since 1970 (UTC). Every computer stamps edits with this so they can be ordered.
        public static long Now()
        {
            return (DateTime.UtcNow.Ticks - EpochTicks) / TimeSpan.TicksPerMillisecond;
        }
    }

    class NewerFormatException : Exception
    {
        public NewerFormatException()
            : base("雲端上的資料是較新版本的便條紙程式建立的，請先更新這台電腦的程式。") { }
    }

    // The part of a note that follows you between computers. Position, size, pin and
    // collapsed state stay on each computer, since screens differ.
    class SyncNote
    {
        public string Id;
        public string Text = "";
        public int Color;
        public float FontSize;
        public long Modified;
    }

    // The contents of notes.json in Google Drive: every note plus markers for deleted ones.
    class SyncDoc
    {
        const int Format = 1;

        public readonly Dictionary<string, SyncNote> Notes = new Dictionary<string, SyncNote>();
        public readonly Dictionary<string, long> Deleted = new Dictionary<string, long>();

        public bool IsEmpty
        {
            get { return Notes.Count == 0 && Deleted.Count == 0; }
        }

        public static string NormalizeText(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        }

        public static SyncDoc Parse(string json)
        {
            Dictionary<string, object> root = Json.Parse(json);
            if (root == null)
                throw new FormatException("雲端資料不是有效的便條紙檔案。");
            if (Json.Long(root, "format") > Format)
                throw new NewerFormatException();

            var doc = new SyncDoc();
            foreach (Dictionary<string, object> n in Json.Objects(root, "notes"))
            {
                string id = Json.Str(n, "id");
                if (string.IsNullOrEmpty(id)) continue;
                doc.AddNote(new SyncNote
                {
                    Id = id,
                    Text = NormalizeText(Json.Str(n, "text")),
                    Color = (int)Json.Long(n, "color"),
                    FontSize = Json.Float(n, "fontSize"),
                    Modified = Json.Long(n, "modified"),
                });
            }
            foreach (Dictionary<string, object> d in Json.Objects(root, "deleted"))
            {
                string id = Json.Str(d, "id");
                if (!string.IsNullOrEmpty(id))
                    doc.AddDeleted(id, Json.Long(d, "time"));
            }
            doc.Resolve();
            return doc;
        }

        // Sorted by id so two equal documents always produce the same text.
        public string ToJson()
        {
            var notes = new List<object>();
            foreach (SyncNote n in Notes.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                notes.Add(new Dictionary<string, object>
                {
                    { "id", n.Id },
                    { "text", n.Text ?? "" },
                    { "color", n.Color },
                    { "fontSize", n.FontSize },
                    { "modified", n.Modified },
                });
            }
            var deleted = new List<object>();
            foreach (KeyValuePair<string, long> d in Deleted.OrderBy(x => x.Key, StringComparer.Ordinal))
                deleted.Add(new Dictionary<string, object> { { "id", d.Key }, { "time", d.Value } });

            return Json.Write(new Dictionary<string, object>
            {
                { "app", "DesktopStickyNotes" },
                { "format", Format },
                { "notes", notes },
                { "deleted", deleted },
            });
        }

        // Folds in another copy, keeping the newest entry for every note.
        public void Absorb(SyncDoc other)
        {
            foreach (SyncNote n in other.Notes.Values)
                AddNote(n);
            foreach (KeyValuePair<string, long> d in other.Deleted)
                AddDeleted(d.Key, d.Value);
            Resolve();
        }

        void AddNote(SyncNote n)
        {
            SyncNote current;
            if (!Notes.TryGetValue(n.Id, out current) || current.Modified < n.Modified)
                Notes[n.Id] = n;
        }

        void AddDeleted(string id, long time)
        {
            long current;
            if (!Deleted.TryGetValue(id, out current) || current < time)
                Deleted[id] = time;
        }

        // A note and its deletion marker can't both stand: the later one wins, deletion on a tie.
        void Resolve()
        {
            foreach (string id in Deleted.Keys.ToList())
            {
                SyncNote n;
                if (!Notes.TryGetValue(id, out n)) continue;
                if (Deleted[id] >= n.Modified)
                    Notes.Remove(id);
                else
                    Deleted.Remove(id);
            }
        }
    }

    class MergeResult
    {
        public readonly List<NoteData> Notes = new List<NoteData>();                        // what this computer should show
        public readonly Dictionary<string, long> Deleted = new Dictionary<string, long>();  // deletion markers to keep
        public SyncDoc Doc;                                                                  // what Google Drive should hold
        public int Conflicts;
    }

    // Merges this computer's notes with the copy in Google Drive.
    //
    // Every note carries the time of its last edit (Modified) and the edit time of the version
    // this computer last saw in the cloud (Synced); Modified != Synced means "edited here since".
    // The newest edit wins, and deleted notes leave a marker so the deletion spreads instead of
    // the note coming back from another computer. When the same note was changed on two computers
    // before either one synced, both versions are kept so no text is ever thrown away.
    //
    // The merge only depends on the two inputs, and this computer keeps its full state, so if
    // two computers upload at the same moment and one overwrites the other, the next round
    // simply merges again and nothing is lost.
    static class SyncMerge
    {
        const long DeletedMarkerLifetime = 365L * 24 * 60 * 60 * 1000;

        public static MergeResult Merge(List<NoteData> local, Dictionary<string, long> localDeleted, SyncDoc remote, long now)
        {
            var result = new MergeResult();

            var localById = new Dictionary<string, NoteData>();
            var order = new List<string>();
            foreach (NoteData n in local)
            {
                if (localById.ContainsKey(n.Id)) continue;
                localById[n.Id] = n;
                order.Add(n.Id);
            }
            order.AddRange(localDeleted.Keys);
            order.AddRange(remote.Notes.Keys.OrderBy(k => k, StringComparer.Ordinal));
            order.AddRange(remote.Deleted.Keys);

            var seen = new HashSet<string>();
            foreach (string id in order)
            {
                if (!seen.Add(id)) continue;

                NoteData mine;
                localById.TryGetValue(id, out mine);
                long mineDeleted;
                bool deletedHere = localDeleted.TryGetValue(id, out mineDeleted);
                SyncNote theirs;
                remote.Notes.TryGetValue(id, out theirs);
                long theirsDeleted;
                bool deletedThere = remote.Deleted.TryGetValue(id, out theirsDeleted);

                if (mine != null)
                {
                    if (theirs != null)
                    {
                        MergeNote(result, mine, theirs, now);
                    }
                    else if (deletedThere && theirsDeleted >= mine.Modified)
                    {
                        if (mine.Modified == mine.Synced)
                        {
                            result.Deleted[id] = theirsDeleted;
                        }
                        else
                        {
                            // Deleted elsewhere while it was being edited here: the edit wins.
                            NoteData kept = mine.Clone();
                            kept.Modified = Math.Max(now, theirsDeleted + 1);
                            result.Notes.Add(kept);
                        }
                    }
                    else
                    {
                        result.Notes.Add(mine);
                    }
                }
                else if (theirs != null)
                {
                    if (deletedHere && mineDeleted >= theirs.Modified)
                        result.Deleted[id] = mineDeleted;
                    else
                        result.Notes.Add(FromCloud(theirs, null));
                }
                else
                {
                    result.Deleted[id] = Math.Max(deletedHere ? mineDeleted : 0, deletedThere ? theirsDeleted : 0);
                }
            }

            PruneDeleted(result.Deleted, now);

            var doc = new SyncDoc();
            foreach (NoteData n in result.Notes)
                doc.Notes[n.Id] = ToCloud(n);
            foreach (KeyValuePair<string, long> d in result.Deleted)
                doc.Deleted[d.Key] = d.Value;
            result.Doc = doc;
            return result;
        }

        static void MergeNote(MergeResult result, NoteData mine, SyncNote theirs, long now)
        {
            if (theirs.Modified == mine.Modified)
            {
                // Same version. Take the cloud's copy so both sides settle on identical data.
                result.Notes.Add(FromCloud(theirs, mine));
                return;
            }

            string myText = SyncDoc.NormalizeText(mine.Text);
            bool changedHere = mine.Modified != mine.Synced;
            bool changedThere = theirs.Modified != mine.Synced;
            bool clash = changedHere && changedThere && myText != theirs.Text;

            if (theirs.Modified > mine.Modified)
            {
                result.Notes.Add(FromCloud(theirs, mine));
                if (clash)
                    AddCopy(result, mine, myText, mine.ColorIndex, mine.FontSize, now);
            }
            else
            {
                NoteData kept = mine.Clone();
                kept.Synced = theirs.Modified;
                result.Notes.Add(kept);
                if (clash)
                    AddCopy(result, mine, theirs.Text, theirs.Color, theirs.FontSize, now);
            }
            if (clash)
                result.Conflicts++;
        }

        // Keeps the losing side of a conflict as a separate note placed next to the original.
        static void AddCopy(MergeResult result, NoteData near, string text, int color, float fontSize, long now)
        {
            NoteData copy = near.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Text = text;
            copy.ColorIndex = color;
            copy.FontSize = fontSize;
            copy.Modified = now;
            copy.Synced = 0;
            copy.TopMost = false;
            copy.Collapsed = false;
            result.Notes.Add(copy);
        }

        static NoteData FromCloud(SyncNote n, NoteData layoutFrom)
        {
            NoteData d = layoutFrom != null ? layoutFrom.Clone() : new NoteData();
            d.Id = n.Id;
            d.Text = n.Text;
            d.ColorIndex = n.Color;
            d.FontSize = n.FontSize;
            d.Modified = n.Modified;
            d.Synced = n.Modified;
            return d;
        }

        static SyncNote ToCloud(NoteData n)
        {
            return new SyncNote
            {
                Id = n.Id,
                Text = SyncDoc.NormalizeText(n.Text),
                Color = n.ColorIndex,
                FontSize = n.FontSize,
                Modified = n.Modified,
            };
        }

        public static void PruneDeleted(Dictionary<string, long> deleted, long now)
        {
            long cutoff = now - DeletedMarkerLifetime;
            foreach (string id in deleted.Keys.ToList())
                if (deleted[id] < cutoff)
                    deleted.Remove(id);
        }
    }
}
