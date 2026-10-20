using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace AudionVoiceTools.Core;

/// <summary>
/// The library of the program: one SQLite file, library\library.sqlite3, with a full-text search (FTS5) - every dictation,
/// every recording, every file transcribed from the disk. A dictation keeps its text, its sound, where it went and how
/// long each stage took; a recording and a file keep their source, state and the transcripts beside them - the library
/// knows of the transcripts, it does not take them away from their source.
///
/// What 2.3 kept comes in at every start, and nothing of it is deleted or changed: the 200 texts of
/// config\dictation_history.json, the recordings Audion_Recording_* of input\ and output\ - given a second name of NTFS in
/// library\recordings\YEAR\MONTH\ with their transcripts, so the cleanup of the project no longer reaches them, - and the
/// jobs of workspace\jobs.sqlite3. What a person deleted here does not come back with the next start (the table gone).
///
/// The writes are made on the thread of the disk (Journal); a query opens a connection of its own and runs off the
/// window thread.
/// </summary>
public sealed class Library : IDisposable
{
    /// <summary>The library of the program; null until the thread of the disk has opened it.</summary>
    public static Library? Current { get; private set; }

    public string Root { get; }
    public string Folder => Path.Combine(Root, "library");
    public string FilePath => Path.Combine(Folder, "library.sqlite3");

    readonly SqliteConnection _write;

    /// <summary>The transcripts the engine writes beside a source (system_core\pipeline\orchestrator.py, sidecar_paths).</summary>
    public static readonly string[] Sidecars = { ".transcript.json", ".md", ".txt", ".srt", ".vtt" };

    public Library(string root)
    {
        Root = root;
        Directory.CreateDirectory(Folder);
        _write = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, DefaultTimeout = 10 }.ToString());
        _write.Open();
        Execute(Schema);
        _audioDays = int.TryParse(Meta("audio_days"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) ? days : 7;
        _transcribeRecordings = Meta("record_transcribe") != "0";
    }

    /// <summary>Opened on the thread of the disk, then 2.3 is brought in and the old sound is swept.</summary>
    public static void Start(string root)
    {
        try
        {
            var library = new Library(root);
            var said = library.Migrate();
            if (said.Length > 0) Journal.Log("[LIBRARY] " + said);
            library.Sweep();
            Current = library;
            Opened?.Invoke();
        }
        catch (Exception ex) { Journal.Log("[ERR] the library did not open: " + ex.Message); }
    }

    /// <summary>The library is open and 2.3 is in it: the manager reads it. Called on the thread of the disk.</summary>
    public static event Action? Opened;

    public void Dispose() => _write.Dispose();

    const string Schema = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS gone (id TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS items (
            id TEXT NOT NULL UNIQUE,
            kind TEXT NOT NULL,
            created TEXT NOT NULL,
            outcome TEXT NOT NULL DEFAULT '',
            title TEXT NOT NULL DEFAULT '',
            text TEXT NOT NULL DEFAULT '',
            clean_text TEXT NOT NULL DEFAULT '',
            error TEXT NOT NULL DEFAULT '',
            engine TEXT NOT NULL DEFAULT '',
            model TEXT NOT NULL DEFAULT '',
            language TEXT NOT NULL DEFAULT '',
            target_process TEXT NOT NULL DEFAULT '',
            target_title TEXT NOT NULL DEFAULT '',
            seconds REAL NOT NULL DEFAULT 0,
            audio TEXT NOT NULL DEFAULT '',
            source TEXT NOT NULL DEFAULT '',
            outputs TEXT NOT NULL DEFAULT '',
            speakers INTEGER NOT NULL DEFAULT 0,
            pinned INTEGER NOT NULL DEFAULT 0,
            detail TEXT NOT NULL DEFAULT '',
            origin TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS items_kind_created ON items(kind, created);
        CREATE UNIQUE INDEX IF NOT EXISTS items_source ON items(kind, source) WHERE source <> '';
        CREATE VIRTUAL TABLE IF NOT EXISTS items_text USING fts5(
            text, clean_text, title, target_title, content = 'items', content_rowid = 'rowid', tokenize = 'unicode61 remove_diacritics 2');
        CREATE TRIGGER IF NOT EXISTS items_ai AFTER INSERT ON items BEGIN
            INSERT INTO items_text(rowid, text, clean_text, title, target_title) VALUES (new.rowid, new.text, new.clean_text, new.title, new.target_title);
        END;
        CREATE TRIGGER IF NOT EXISTS items_ad AFTER DELETE ON items BEGIN
            INSERT INTO items_text(items_text, rowid, text, clean_text, title, target_title) VALUES ('delete', old.rowid, old.text, old.clean_text, old.title, old.target_title);
        END;
        CREATE TRIGGER IF NOT EXISTS items_au AFTER UPDATE ON items BEGIN
            INSERT INTO items_text(items_text, rowid, text, clean_text, title, target_title) VALUES ('delete', old.rowid, old.text, old.clean_text, old.title, old.target_title);
            INSERT INTO items_text(rowid, text, clean_text, title, target_title) VALUES (new.rowid, new.text, new.clean_text, new.title, new.target_title);
        END;
        PRAGMA user_version = 1;
        """;

    int Execute(string sql, params (string Name, object? Value)[] values)
    {
        using var command = _write.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    /// <summary>A time as the library keeps it: UTC, sortable as text.</summary>
    static string Stamp(DateTime time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    static DateTime Local(string stamp) =>
        DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            ? utc.ToLocalTime() : DateTime.MinValue;

    /// <summary>A path as the library keeps it: relative to the root when it lies in the program - the folder may move.</summary>
    string Keep(string path)
    {
        if (path.Length == 0) return "";
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(Root, full);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative;
    }

    public string Full(string kept) => kept.Length == 0 ? "" : Path.IsPathRooted(kept) ? kept : Path.Combine(Root, kept);

    bool Gone(string id)
    {
        using var command = _write.CreateCommand();
        command.CommandText = "SELECT 1 FROM gone WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is not null;
    }

    string? Meta(string key)
    {
        using var command = _write.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    void SetMeta(string key, string value) =>
        Execute("INSERT INTO meta(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$key", key), ("$value", value));

    // ------------------------------------------------------------ dictations

    static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>What a dictation took, stage by stage: kept as it was measured.</summary>
    static string Timings(DictationRecord r) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["device"] = r.Device, ["mic_open_ms"] = r.MicOpenMs, ["listening_shown_ms"] = r.ListeningShownMs, ["audio_ready_ms"] = r.AudioReadyMs,
        ["recognize_ms"] = r.RecognizeMs, ["mode"] = r.Mode, ["mode_ms"] = r.ModeMs, ["paste_ms"] = r.PasteMs, ["stop_to_paste_ms"] = r.StopToPasteMs, ["ui_worst_lag_ms"] = r.UiWorstLagMs,
    }, Compact);

    /// <summary>A dictation of this program into the library; <paramref name="replace"/> false - one already there stays as it is.</summary>
    public void Put(DictationRecord r, string origin = "3.0", bool replace = true)
    {
        if (Gone(r.Id)) return;
        Execute($"""
            INSERT INTO items (id, kind, created, outcome, text, clean_text, error, engine, target_title, seconds, audio, detail, origin)
            VALUES ($id, 'dictation', $created, $outcome, $text, $clean, $error, $engine, $target, $seconds, $audio, $detail, $origin)
            {(replace ? "ON CONFLICT(id) DO UPDATE SET outcome = excluded.outcome, text = excluded.text, clean_text = excluded.clean_text, error = excluded.error, engine = excluded.engine, target_title = excluded.target_title, seconds = excluded.seconds, audio = excluded.audio, detail = excluded.detail" : "ON CONFLICT(id) DO NOTHING")}
            """,
            ("$id", r.Id), ("$created", Stamp(r.Created)), ("$outcome", r.Outcome), ("$text", r.Text), ("$clean", r.CleanText), ("$error", r.Error), ("$engine", r.Engine),
            ("$target", r.TargetTitle), ("$seconds", r.AudioSeconds), ("$audio", r.Audio), ("$detail", Timings(r)), ("$origin", origin));
    }

    /// <summary>
    /// The words of a search as FTS5 reads them: every word the beginning of a word of the text, all of them in it. A
    /// quote of the person is a letter, not a sign of the query.
    /// </summary>
    static string Query(string words) => string.Join(" ",
        words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(word => "\"" + word.Replace("\"", "\"\"") + "\"*"));

    /// <summary>The dictations, the pinned first, then the newest; with words - only those whose text holds them.</summary>
    public List<DictationRecord> Dictations(string words = "", int most = 500)
    {
        using var read = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 10 }.ToString());
        read.Open();
        using var command = read.CreateCommand();
        var query = Query(words.Trim());
        command.CommandText = $"""
            SELECT id, created, outcome, text, error, engine, target_title, seconds, audio, pinned, origin, detail, clean_text FROM items
            WHERE kind = 'dictation' {(query.Length > 0 ? "AND rowid IN (SELECT rowid FROM items_text WHERE items_text MATCH $query)" : "")}
            ORDER BY pinned DESC, created DESC LIMIT $most
            """;
        if (query.Length > 0) command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$most", most);
        var found = new List<DictationRecord>();
        using var row = command.ExecuteReader();
        while (row.Read())
        {
            var record = new DictationRecord
            {
                Id = row.GetString(0), Created = Local(row.GetString(1)), Outcome = row.GetString(2), Text = row.GetString(3), Error = row.GetString(4),
                Engine = row.GetString(5), TargetTitle = row.GetString(6), AudioSeconds = row.GetDouble(7), Audio = row.GetString(8),
                Pinned = row.GetInt64(9) != 0, Origin = row.GetString(10), CleanText = row.GetString(12),
            };
            try
            {
                using var detail = JsonDocument.Parse(row.GetString(11) is { Length: > 0 } text ? text : "{}");
                if (detail.RootElement.TryGetProperty("stop_to_paste_ms", out var wait) && wait.ValueKind == JsonValueKind.Number) record.StopToPasteMs = wait.GetInt64();
                if (detail.RootElement.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String) record.Mode = mode.GetString() ?? "";
            }
            catch (JsonException) { }
            found.Add(record);
        }
        return found;
    }

    public Task<List<DictationRecord>> DictationsAsync(string words, int most = 500) => Task.Run(() => Dictations(words, most));

    public void Pin(string id, bool pinned) => Execute("UPDATE items SET pinned = $pinned WHERE id = $id", ("$pinned", pinned ? 1 : 0), ("$id", id));

    /// <summary>A dictation taken off: its sound to the Recycle Bin, the record out, and it does not come back from 2.3.</summary>
    public string Delete(string id)
    {
        using var command = _write.CreateCommand();
        command.CommandText = "SELECT audio FROM items WHERE id = $id AND kind = 'dictation'";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteScalar() is not string audio) return "";
        var sound = Full(audio);
        if (audio.Length > 0 && File.Exists(sound))
        {
            if (!Bin.HasBin(sound)) return Lang.F("на этом диске нет Корзины, звук не удалён: {0}", sound);
            if (Bin.ToBin(new[] { sound }) is { Length: > 0 } code) return Lang.F("звук не ушёл в Корзину (код {0})", code);
        }
        Execute("INSERT OR IGNORE INTO gone(id) VALUES ($id)", ("$id", id));
        Execute("DELETE FROM items WHERE id = $id", ("$id", id));
        return "";
    }

    // ------------------------------------------------------------ the sound of dictations

    volatile int _audioDays;

    /// <summary>How long the sound of a dictation is kept, in days: 7 by default; -1 - always; 0 - not kept. Read anywhere.</summary>
    public int AudioDays => _audioDays;

    /// <summary>A new term for the sound, kept in the library itself; on the thread of the disk.</summary>
    public void SetAudioDays(int days)
    {
        _audioDays = days;
        SetMeta("audio_days", days.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The sound older than its term goes to the Recycle Bin - a pinned dictation keeps its own; the text stays. Nothing is
    /// taken off on a drive without a Bin. The number of the sounds taken off.
    /// </summary>
    public int Sweep()
    {
        int days = AudioDays;
        if (days < 0) return 0;
        var before = Stamp(DateTime.Now.AddDays(-days));
        var old = new List<(string Id, string File)>();
        using (var command = _write.CreateCommand())
        {
            command.CommandText = "SELECT id, audio FROM items WHERE kind = 'dictation' AND pinned = 0 AND audio <> '' AND created < $before";
            command.Parameters.AddWithValue("$before", before);
            using var row = command.ExecuteReader();
            while (row.Read()) old.Add((row.GetString(0), Full(row.GetString(1))));
        }
        if (old.Count == 0) return 0;
        if (!Bin.HasBin(Folder))
        {
            Journal.Log($"[LIBRARY] the sound of {old.Count} old dictations is kept: the drive of the library has no Recycle Bin");
            return 0;
        }
        var present = old.Where(one => File.Exists(one.File)).Select(one => one.File).ToList();
        if (Bin.ToBin(present) is { Length: > 0 } code)
        {
            Journal.Log($"[LIBRARY] the old sound did not go to the Recycle Bin (code {code})");
            return 0;
        }
        foreach (var (id, _) in old) Execute("UPDATE items SET audio = '' WHERE id = $id", ("$id", id));
        Journal.Log($"[LIBRARY] the sound of {present.Count} dictations older than {days} days went to the Recycle Bin");
        return present.Count;
    }

    // ------------------------------------------------------------ recordings and files

    volatile bool _transcribeRecordings;

    /// <summary>A recording, once saved, goes to the transcription by itself (true by default). Read anywhere.</summary>
    public bool TranscribeRecordings => _transcribeRecordings;

    /// <summary>On the thread of the disk.</summary>
    public void SetTranscribeRecordings(bool on)
    {
        _transcribeRecordings = on;
        SetMeta("record_transcribe", on ? "1" : "0");
    }

    /// <summary>The time a recording began, by its name (Audion_Recording_2026-09-04_14-06-00[_2]); the time of the file otherwise.</summary>
    static DateTime RecordingTime(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var stamp = stem.StartsWith("Audion_Recording_", StringComparison.OrdinalIgnoreCase)
            ? string.Join("_", stem["Audion_Recording_".Length..].Split('_').Take(2)) : "";
        return DateTime.TryParseExact(stamp, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at)
            ? at : File.GetLastWriteTime(file);
    }

    /// <summary>A recording of the recorder of this program into the library; recovered - after a fall of the program.</summary>
    public void PutRecording(string file, bool recovered = false)
    {
        var id = "rec-" + Path.GetFileNameWithoutExtension(file);
        if (Gone(id)) return;
        Execute("""
            INSERT INTO items (id, kind, created, outcome, title, seconds, source, outputs, detail, origin)
            VALUES ($id, 'recording', $created, 'pending', $title, $seconds, $source, $outputs, $detail, '3.0')
            ON CONFLICT(id) DO UPDATE SET seconds = excluded.seconds, outputs = excluded.outputs
            """,
            ("$id", id), ("$created", Stamp(RecordingTime(file))), ("$title", Path.GetFileNameWithoutExtension(file)), ("$seconds", WavSeconds(file)),
            ("$source", Keep(file)), ("$outputs", JsonSerializer.Serialize(OutputsOf(file).Select(Keep), Compact)),
            ("$detail", recovered ? JsonSerializer.Serialize(new { recovered = true }, Compact) : ""));
    }

    /// <summary>One recording or file of the library.</summary>
    public sealed record Item(string Id, string Kind, DateTime Created, string Outcome, string Title, string Error, double Seconds, string Source, string Origin);

    public List<Item> Items(string kind, int most = 500)
    {
        using var read = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 10 }.ToString());
        read.Open();
        using var command = read.CreateCommand();
        command.CommandText = "SELECT id, kind, created, outcome, title, error, seconds, source, origin FROM items WHERE kind = $kind ORDER BY created DESC LIMIT $most";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$most", most);
        var found = new List<Item>();
        using var row = command.ExecuteReader();
        while (row.Read())
            found.Add(new Item(row.GetString(0), row.GetString(1), Local(row.GetString(2)), row.GetString(3), row.GetString(4), row.GetString(5),
                row.GetDouble(6), Full(row.GetString(7)), row.GetString(8)));
        return found;
    }

    public Task<List<Item>> ItemsAsync(string kind, int most = 500) => Task.Run(() => Items(kind, most));

    /// <summary>The transcripts beside a source, those that are there.</summary>
    public static List<string> OutputsOf(string source)
    {
        var folder = Path.GetDirectoryName(source) ?? "";
        var stem = Path.GetFileNameWithoutExtension(source);
        return Sidecars.Select(extension => Path.Combine(folder, stem + extension)).Where(File.Exists).ToList();
    }

    /// <summary>
    /// A file of a run of the window, with its state as the run said it: a recording of the library takes it on itself,
    /// any other file is a file of the library.
    /// </summary>
    public void PutFile(string source, string outcome, string error = "")
    {
        var kept = Keep(source);
        var outputs = JsonSerializer.Serialize(OutputsOf(source).Select(Keep), Compact);
        if (Execute("UPDATE items SET outcome = $outcome, error = $error, outputs = $outputs WHERE kind = 'recording' AND source = $source",
                ("$outcome", outcome), ("$error", error), ("$outputs", outputs), ("$source", kept)) > 0) return;
        var id = "file-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(kept.ToLowerInvariant())))[..12].ToLowerInvariant();
        if (Gone(id)) return;
        Execute("""
            INSERT INTO items (id, kind, created, outcome, title, error, source, outputs, origin)
            VALUES ($id, 'file', $created, $outcome, $title, $error, $source, $outputs, '3.0')
            ON CONFLICT(id) DO UPDATE SET outcome = excluded.outcome, error = excluded.error, outputs = excluded.outputs
            """,
            ("$id", id), ("$created", Stamp(DateTime.Now)), ("$outcome", outcome), ("$title", Path.GetFileName(source)), ("$error", error),
            ("$source", kept), ("$outputs", outputs));
    }

    // ------------------------------------------------------------ what 2.3 kept

    /// <summary>Everything of 2.3 brought in, once a start: what came, in words for the journal ("" - nothing new).</summary>
    public string Migrate()
    {
        var said = new List<string>();
        foreach (var (name, step) in new (string, Func<int>)[]
                 {
                     ("dictations of the probe", FromJsonl), ("dictations of 2.3", FromHistory), ("recordings of 2.3", FromRecorder), ("jobs of 2.3", FromJobs),
                 })
        {
            try { if (step() is > 0 and var count) said.Add($"{count} {name}"); }
            catch (Exception ex) { said.Add($"{name}: {ex.Message}"); }
        }
        return said.Count > 0 ? "brought in: " + string.Join(", ", said) : "";
    }

    int Count(string kind)
    {
        using var command = _write.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM items WHERE kind = $kind";
        command.Parameters.AddWithValue("$kind", kind);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>library\dictations.jsonl of the probe of 3.0: its dictations, once; the file stays.</summary>
    int FromJsonl()
    {
        var file = Path.Combine(Folder, "dictations.jsonl");
        if (Meta("from.jsonl") is not null || !File.Exists(file)) return 0;
        int before = Count("dictation");
        using (var batch = _write.BeginTransaction())
        {
            foreach (var line in File.ReadLines(file, Encoding.UTF8))
                try { if (JsonSerializer.Deserialize<DictationRecord>(line) is { Id.Length: > 0 } record) Put(record, "3.0", replace: false); }
                catch (JsonException) { }
            batch.Commit();
        }
        SetMeta("from.jsonl", Stamp(DateTime.Now));
        return Count("dictation") - before;
    }

    /// <summary>config\dictation_history.json of 2.3 (system_core\live\history.py): its texts, at every start - 2.3 still writes it.</summary>
    int FromHistory()
    {
        var file = Path.Combine(Root, "config", "dictation_history.json");
        if (!File.Exists(file)) return 0;
        using var told = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
        var entries = told.RootElement.ValueKind == JsonValueKind.Array ? told.RootElement
            : told.RootElement.TryGetProperty("entries", out var list) ? list : default;
        if (entries.ValueKind != JsonValueKind.Array) return 0;
        var written = File.GetLastWriteTime(file);
        int before = Count("dictation");
        using (var batch = _write.BeginTransaction())
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                string Field(string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                var text = Field("text").Trim();
                if (text.Length == 0) continue;
                var key = Field("entry_id");
                if (key.Length == 0) key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Field("created_at") + "\n" + text)))[..16].ToLowerInvariant();
                var created = DateTimeOffset.TryParse(Field("created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.LocalDateTime : written;
                Put(new DictationRecord { Id = "2.3-" + key, Created = created, Outcome = "pasted", Text = text }, "history-2.3", replace: false);
            }
            batch.Commit();
        }
        return Count("dictation") - before;
    }

    /// <summary>
    /// The recordings of the recorder of 2.3, Audion_Recording_*.wav|.m4a in input\ and output\: a second name of NTFS in
    /// library\recordings\YEAR\MONTH\, their transcripts beside them the same way, the files of input\ left as they are.
    /// </summary>
    int FromRecorder()
    {
        int before = Count("recording");
        foreach (var folder in new[] { Path.Combine(Root, "input"), Path.Combine(Root, "output") })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var original in Directory.EnumerateFiles(folder, "Audion_Recording_*.*")
                         .Where(path => path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)))
            {
                var stem = Path.GetFileNameWithoutExtension(original);
                var id = "rec-" + stem;
                if (Gone(id)) continue;
                var created = RecordingTime(original);
                var target = Path.Combine(Folder, "recordings", created.ToString("yyyy", CultureInfo.InvariantCulture), created.ToString("MM", CultureInfo.InvariantCulture), Path.GetFileName(original));
                if (!File.Exists(target)) Bin.LinkOrCopy(original, target);
                // The transcripts too - and those 2.3 makes later, at the next start.
                foreach (var extension in Sidecars)
                {
                    var beside = Path.Combine(folder, stem + extension);
                    var mine = Path.Combine(Path.GetDirectoryName(target)!, stem + extension);
                    if (File.Exists(beside) && !File.Exists(mine)) Bin.LinkOrCopy(beside, mine);
                }
                var outputs = OutputsOf(target);
                Execute("""
                    INSERT INTO items (id, kind, created, outcome, title, seconds, source, outputs, detail, origin)
                    VALUES ($id, 'recording', $created, $outcome, $title, $seconds, $source, $outputs, $detail, 'recorder-2.3')
                    ON CONFLICT(id) DO UPDATE SET outputs = excluded.outputs,
                        outcome = CASE WHEN excluded.outcome = 'completed' THEN 'completed' ELSE items.outcome END
                    """,
                    ("$id", id), ("$created", Stamp(created)), ("$outcome", outputs.Any(path => path.EndsWith(".transcript.json", StringComparison.OrdinalIgnoreCase)) ? "completed" : "pending"),
                    ("$title", stem), ("$seconds", WavSeconds(target)), ("$source", Keep(target)),
                    ("$outputs", JsonSerializer.Serialize(outputs.Select(Keep), Compact)), ("$detail", JsonSerializer.Serialize(new { original = Keep(original) }, Compact)));
            }
        }
        return Count("recording") - before;
    }

    /// <summary>The length of a WAV by its header; 0 when it does not read as one.</summary>
    public static double WavSeconds(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 44 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") return 0;
            reader.ReadInt32();
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") return 0;
            int byteRate = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                var chunk = Encoding.ASCII.GetString(reader.ReadBytes(4));
                long size = reader.ReadUInt32();
                if (chunk == "fmt ")
                {
                    var format = reader.ReadBytes((int)size);
                    byteRate = BitConverter.ToInt32(format, 8);
                }
                else if (chunk == "data")
                {
                    // A recording cut off by a fall keeps a header that says less than the file holds.
                    long data = Math.Max(size, stream.Length - stream.Position);
                    return byteRate > 0 ? data / (double)byteRate : 0;
                }
                else stream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }
        catch (IOException) { }
        return 0;
    }

    /// <summary>The jobs of the engine of 2.3, workspace\jobs.sqlite3, read only: a recording takes the state of its job, any other source is a file.</summary>
    int FromJobs()
    {
        var store = Path.Combine(Root, "workspace", "jobs.sqlite3");
        if (!File.Exists(store)) return 0;
        int before = Count("file");
        var jobs = new List<(string Source, string Status, string Created, string Error)>();
        using (var read = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 10 }.ToString()))
        {
            read.Open();
            using var command = read.CreateCommand();
            command.CommandText = "SELECT source_path, status, COALESCE(completed_at, created_at, ''), COALESCE(error, '') FROM jobs";
            using var row = command.ExecuteReader();
            while (row.Read()) jobs.Add((row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3)));
        }
        using var batch = _write.BeginTransaction();
        foreach (var (source, status, created, error) in jobs)
        {
            var name = Path.GetFileNameWithoutExtension(source);
            if (name.StartsWith("Audion_Recording_", StringComparison.OrdinalIgnoreCase))
            {
                // The recording is in the library already: a failed job is its state when no transcript came.
                Execute("UPDATE items SET outcome = $status, error = $error WHERE id = $id AND outcome <> 'completed'",
                    ("$status", status), ("$error", error), ("$id", "rec-" + name));
                continue;
            }
            var kept = Keep(source);
            var id = "file-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(kept.ToLowerInvariant())))[..12].ToLowerInvariant();
            if (Gone(id)) continue;
            var when = DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at) ? at.LocalDateTime : File.GetLastWriteTime(store);
            Execute("""
                INSERT INTO items (id, kind, created, outcome, title, error, source, outputs, origin)
                VALUES ($id, 'file', $created, $outcome, $title, $error, $source, $outputs, 'jobs-2.3')
                ON CONFLICT(id) DO NOTHING
                """,
                ("$id", id), ("$created", Stamp(when)), ("$outcome", status), ("$title", Path.GetFileName(source)), ("$error", error),
                ("$source", kept), ("$outputs", JsonSerializer.Serialize(OutputsOf(source).Select(Keep), Compact)));
        }
        batch.Commit();
        return Count("file") - before;
    }
}
