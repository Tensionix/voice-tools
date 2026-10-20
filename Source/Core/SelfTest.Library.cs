using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AudionVoiceTools.Core;

/// <summary>
/// The library checked on a copy of its own, beside the built program (._runtime\library-check, cleared by the build):
/// what 2.3 kept comes in - the texts of config\dictation_history.json, a recording of input\ with its transcript, the
/// jobs of workspace\jobs.sqlite3 - and its file of input\ stays as it was; the search finds by the beginnings of words,
/// whatever the case; a second start brings nothing twice; what is deleted does not come back. The Recycle Bin is not
/// touched here - a check must not fill the Bin of the person.
/// </summary>
public static partial class SelfTest
{
    /// <summary>
    /// The stream of xAI: a cumulative final adds only what the locked chunks did not say (cloud_realtime.py,
    /// _novel_suffix) - compared by words, whatever the case; a new chunk that is no cumulative final is taken whole.
    /// </summary>
    static void CloudChecks(Report report)
    {
        var cases = new (string Previous, string Cumulative, string Expected)[]
        {
            ("", "Первая фраза", "Первая фраза"),
            ("привет мир", "Привет мир, как дела", "как дела"),
            ("раз два три", "два три", ""),
            ("раз", "Раз.", ""),
            ("alpha beta", "gamma", "gamma"),
        };
        var wrong = cases.Where(one => XaiStream.NovelSuffix(one.Previous, one.Cumulative) != one.Expected)
            .Select(one => $"<{one.Previous}> + <{one.Cumulative}> gave <{XaiStream.NovelSuffix(one.Previous, one.Cumulative)}>").ToList();
        report.Check("cloud: a cumulative final of xAI adds only what is new", wrong.Count == 0, string.Join("; ", wrong));
        var config = new DictationConfig();
        report.Check("cloud: dictation is local until a cloud is chosen, OpenAI Realtime on gpt-realtime-whisper",
            config.Engine == "local" && config.RealtimeModel == "gpt-realtime-whisper" && DictationConfig.Engines.Length == 5
            && DictationConfig.ProviderOf("openai_batch") == "openai" && DictationConfig.ProviderOf("local") == "");
    }

    /// <summary>
    /// The dictionary of SETTINGS (Core\Vocabulary.cs) on a folder of its own in TEMP: before its file it is the term_dictionary of
    /// 2.3; a line is a term, «сказано -> написано» a replacement made by whole words whatever the case - the longest first, the
    /// rest of the text as it was; the clouds are told the terms and what the replacements write when it has letters.
    /// </summary>
    static void VocabularyChecks(Report report)
    {
        var root = Path.Combine(Path.GetTempPath(), "audion-vocabulary-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "app_settings.yaml"),
                "language: ru\nterm_dictionary:\n- ФГИС ТП\n- 'КИПРР'\n  - \"Kubernetes\"\npipeline:\n  force: false\n", new UTF8Encoding(false));
            var seed = Vocabulary.Text(root).Replace("\r\n", "\n");
            report.Check("dictionary: before its file it is the term_dictionary of 2.3", seed == "ФГИС ТП\nКИПРР\nKubernetes", seed);

            Vocabulary.Save(root, "# заметка\nФГИС ТП\nгитхаб экшнс -> GitHub Actions\nгитхаб → GitHub\nкилометров в час => км/ч\n -> пусто\n");
            var entries = Vocabulary.Entries(root);
            report.Check("dictionary: terms and replacements read, notes and halves passed by",
                entries.Count == 4 && entries[0] == new Vocabulary.Entry("", "ФГИС ТП") && entries[1] == new Vocabulary.Entry("гитхаб экшнс", "GitHub Actions")
                && entries[3] == new Vocabulary.Entry("километров в час", "км/ч"), string.Join("; ", entries));
            var replaced = Vocabulary.Replace(root, "Гитхаб  Экшнс упал, гитхаб жив, гитхабовский нет; 90 километров в час.");
            report.Check("dictionary: replacements by whole words, whatever the case, the longest first",
                replaced == "GitHub Actions упал, GitHub жив, гитхабовский нет; 90 км/ч.", replaced);
            var terms = Vocabulary.Terms(root);
            report.Check("dictionary: the clouds are told the terms and what the replacements write with letters",
                terms.SequenceEqual(new[] { "ФГИС ТП", "GitHub Actions", "GitHub", "км/ч" }), string.Join("; ", terms));
            var echoTerms = new[] { "ФГИС ТП", "Kubernetes", "GitHub Actions", "КИПРР" };
            report.Check("dictionary: the echo of the whole dictionary after a short take is taken off, a long take keeps it",
                Vocabulary.StripEcho("Привет. ФГИС ТП, Kubernetes, GitHub Actions, КИПРР.", echoTerms, 1.0) == "Привет"
                && Vocabulary.StripEcho("Привет. ФГИС ТП, Kubernetes, GitHub Actions, КИПРР.", echoTerms, 4.0) == "Привет. ФГИС ТП, Kubernetes, GitHub Actions, КИПРР."
                && Vocabulary.StripEcho("Привет, Kubernetes", new[] { "Kubernetes" }, 0.5) == "Привет, Kubernetes"
                && Vocabulary.StripEcho("ФГИС ТП и Kubernetes", echoTerms, 0.5) == "ФГИС ТП и Kubernetes");
            report.Check("dictionary: the file is UTF-8 without a mark, written whole",
                File.ReadAllBytes(Vocabulary.FilePath(root)) is [0x23, ..] && !File.Exists(Vocabulary.FilePath(root) + ".tmp"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>
    /// The modes of the text (Core\TextModes.cs) against a server of the check on this machine: «Исходный текст» asks nothing;
    /// a mode sends the model, its words and the text, and no key to a server that is no service of a key; «Слегка» refuses an
    /// answer that is no text corrected; a model late or failing is an error in words - the paste takes the text of the engine.
    /// </summary>
    static void ModeChecks(Report report)
    {
        report.Check("modes: a key only to its own service",
            TextModes.ProviderOf("https://api.openai.com/v1") == "openai" && TextModes.ProviderOf("https://api.x.ai/v1") == "xai"
            && TextModes.ProviderOf("http://localhost:11434/v1") == "" && TextModes.ProviderOf("https://openrouter.ai/api/v1") == "" && TextModes.ProviderOf("not an address") == "");
        var fresh = new DictationConfig();
        report.Check("modes: the text as the engine gives it until a mode is chosen; the model of 2.3",
            fresh.Mode == "raw" && fresh.ModeModel == "gpt-6-luna" && fresh.ModeUrl == "https://api.openai.com/v1" && TextModes.Modes[0] == "raw"
            && TextModes.Effort("gpt-6-luna", "minutes") == "max" && TextModes.Effort("gpt-6-luna", "light") == "low" && TextModes.Effort("check-model", "minutes") == "");

        // The list of a service is everything it has: each choice takes what does its work; a local server's list is all text.
        var listed = new[] { "gpt-4o-transcribe", "gpt-5.4-mini", "text-embedding-3-large", "gpt-realtime-whisper", "tts-1", "gpt-4o-transcribe-diarize", "gpt-image-1", "gpt-live-transcribe" };
        report.Check("models: a list of a service is picked for its use",
            ModelCatalog.Pick(listed, ModelCatalog.Kind.Chat, "https://api.openai.com/v1").SequenceEqual(new[] { "gpt-5.4-mini" })
            && ModelCatalog.Pick(listed, ModelCatalog.Kind.Transcribe, "https://api.openai.com/v1").SequenceEqual(new[] { "gpt-4o-transcribe", "gpt-4o-transcribe-diarize" })
            && ModelCatalog.Pick(listed, ModelCatalog.Kind.Realtime, "https://api.openai.com/v1").SequenceEqual(new[] { "gpt-4o-transcribe", "gpt-realtime-whisper", "gpt-live-transcribe" })
            && ModelCatalog.Pick(new[] { "llama3.2", "qwen3:8b" }, ModelCatalog.Kind.Chat, "http://localhost:11434/v1").Count == 2
            && ModelCatalog.Choices(null, "my-model", "a", "b").SequenceEqual(new[] { "my-model", "a", "b" }));

        // GET {address}/models of a server of its own: the list read, the newest first, no key to an address of no service.
        var lists = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        lists.Start();
        int listPort = ((System.Net.IPEndPoint)lists.LocalEndpoint).Port;
        string listAsked = "";
        var answering = System.Threading.Tasks.Task.Run(async () =>
        {
            using var client = await lists.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[8192];
            int read = await stream.ReadAsync(buffer);
            listAsked = Encoding.UTF8.GetString(buffer, 0, read);
            var body = Encoding.UTF8.GetBytes("{\"object\":\"list\",\"data\":[{\"id\":\"old-model\",\"created\":100},{\"id\":\"new-model\",\"created\":200}]}");
            var top = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(top);
            await stream.WriteAsync(body);
        });
        try
        {
            var (ids, error) = System.Threading.Tasks.Task.Run(() => ModelCatalog.Ask(Paths.Root, $"http://127.0.0.1:{listPort}/v1")).GetAwaiter().GetResult();
            answering.Wait(TimeSpan.FromSeconds(5));
            report.Check("models: the list of a server read, the newest first, no key sent",
                error.Length == 0 && ids.SequenceEqual(new[] { "new-model", "old-model" }) && listAsked.StartsWith("GET /v1/models ", StringComparison.Ordinal)
                && !listAsked.Contains("Authorization", StringComparison.OrdinalIgnoreCase), error.Length > 0 ? error : string.Join(", ", ids));
        }
        finally { lists.Stop(); }

        var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        int port = ((System.Net.IPEndPoint)server.LocalEndpoint).Port;
        string asked = "";
        int requests = 0;
        // One answer a connection: the head and the body of the request kept, the answer given after a pause.
        void Serve(int status, string content, int pause = 0) => System.Threading.Tasks.Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var got = new MemoryStream();
            var buffer = new byte[8192];
            int length = -1, head = -1;
            while (head < 0 || got.Length < head + length)
            {
                int read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                got.Write(buffer, 0, read);
                var text = Encoding.UTF8.GetString(got.ToArray());
                if (head < 0 && text.IndexOf("\r\n\r\n", StringComparison.Ordinal) is var end and >= 0)
                {
                    head = Encoding.UTF8.GetByteCount(text[..(end + 4)]);
                    var line = text[..end].Split("\r\n").FirstOrDefault(one => one.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    length = line is null ? 0 : int.Parse(line.Split(':')[1].Trim());
                }
            }
            asked = Encoding.UTF8.GetString(got.ToArray());
            requests++;
            if (pause > 0) await System.Threading.Tasks.Task.Delay(pause);
            var body = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } }));
            var top = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            try { await stream.WriteAsync(top); await stream.WriteAsync(body); } catch (IOException) { }
        });
        // A service of the cloud dictates: the working over is on whatever keys the machine holds (TextModes.Allowed).
        var config = new DictationConfig { Engine = "xai", ModeUrl = $"http://127.0.0.1:{port}/v1", ModeModel = "check-model" };
        string Try(string mode, string text)
        {
            // Off the window thread: the check runs on it, and the request comes back to the thread it was made on.
            try { return System.Threading.Tasks.Task.Run(() => TextModes.Apply(Paths.Root, config, mode, text)).GetAwaiter().GetResult(); }
            catch (InvalidOperationException error) { return "!" + error.Message; }
        }
        var wait = TextModes.Wait;
        try
        {
            const string spoken = "ну короче завтра в десять созвон по гитхаб экшнс";
            report.Check("modes: «Исходный текст» asks nothing", Try("raw", spoken) == spoken && requests == 0);

            Serve(200, "Завтра в десять - созвон по GitHub Actions.");
            var light = Try("light", spoken);
            report.Check("modes: «Слегка» gives the text of the model", light == "Завтра в десять - созвон по GitHub Actions.", light);
            report.Check("modes: the request - to /chat/completions, the model, the words of the mode and the text, no key to a server of no service",
                asked.StartsWith("POST /v1/chat/completions ", StringComparison.Ordinal) && asked.Contains("\"model\":\"check-model\"")
                && asked.Contains("\"role\":\"system\"") && asked.Contains(System.Text.Json.JsonSerializer.Serialize(spoken)[1..^1])
                && !asked.Contains("Authorization", StringComparison.OrdinalIgnoreCase), asked.Split("\r\n")[0]);

            Serve(200, "- Созвон завтра в десять");
            // Off the window thread, as Try asks.
            var chosen = System.Threading.Tasks.Task.Run(() => TextModes.Apply(Paths.Root, config, "magic", spoken, "Telegram (2)")).GetAwaiter().GetResult();
            report.Check("modes: «Магия» is told the window the text goes into", chosen == "- Созвон завтра в десять" && asked.Contains("Telegram (2)"), chosen);

            Serve(200, "Конечно! Вот подробный план созвона: " + new string('х', 300));
            var answered = Try("light", spoken);
            report.Check("modes: «Слегка» refuses an answer that is no text corrected", answered.StartsWith('!'), answered);

            Serve(500, "");
            var failed = Try("list", spoken);
            report.Check("modes: a model that fails is an error in words", failed.StartsWith("!HTTP 500"), failed);

            TextModes.Wait = TimeSpan.FromMilliseconds(300);
            Serve(200, "поздно", pause: 1500);
            var late = Try("strong", spoken);
            report.Check("modes: a model late is an error in words", late.StartsWith('!') && !late.StartsWith("!HTTP"), late);
        }
        finally
        {
            TextModes.Wait = wait;
            server.Stop();
        }
    }

    /// <summary>
    /// The measure of the engines (Core\Bench.cs): errors are counted by words and letters of what was read, whatever the
    /// case, ё or the signs; an English term written in Russian letters is an error; nothing heard is the whole phrase wrong.
    /// </summary>
    static void BenchChecks(Report report)
    {
        const string read = "Открой файл в Visual Studio Code и запусти pytest.";
        report.Check("bench: case, ё and the signs are no errors",
            Bench.Wer(read, "открой файл в visual studio code, и запусти pytest") == 0 && Bench.Cer("Ещё раз.", "еще раз") == 0);
        report.Check("bench: a term in Russian letters counts, nothing heard is all wrong",
            Math.Abs(Bench.Wer(read, "Открой файл в Вижуал Студио Код и запусти пайтест") - 4.0 / 9) < 1e-9 && Bench.Wer(read, "") == 1 && Bench.Cer(read, "") == 1);
        report.Check("bench: the segments of whisper-server join whole, a word cut between two of them too",
            Bench.WhisperText("Привет!\n Посмотри, почему окно не открывается после об\nновления.\n") == "Привет! Посмотри, почему окно не открывается после обновления.");
    }

    /// <summary>
    /// The file of the recorder: its header of 80 bytes - RIFF with a JUNK chunk kept for RF64 - and RF64 past 4 GB (counted,
    /// not written), a fall recovered by the length of the file and its marker taken off, the WAV read back by NAudio.
    /// </summary>
    static void RecorderChecks(Report report)
    {
        var small = Wav.Header(96000, Recorder.Rate);
        string Tag(byte[] header, int at) => Encoding.ASCII.GetString(header, at, 4);
        report.Check("recorder: the header is RIFF with a JUNK of 28 kept for RF64, mono PCM16 at 48 kHz",
            small.Length == 80 && Tag(small, 0) == "RIFF" && BitConverter.ToUInt32(small, 4) == 96000 + 72 && Tag(small, 12) == "JUNK"
            && BitConverter.ToInt32(small, 16) == 28 && Tag(small, 48) == "fmt " && BitConverter.ToInt16(small, 58) == 1 && BitConverter.ToInt32(small, 60) == 48000
            && Tag(small, 72) == "data" && BitConverter.ToUInt32(small, 76) == 96000);
        long huge = 5_000_000_000;
        var big = Wav.Header(huge, Recorder.Rate);
        report.Check("recorder: past 4 GB the same 80 bytes are RF64 with ds64",
            Tag(big, 0) == "RF64" && BitConverter.ToUInt32(big, 4) == uint.MaxValue && Tag(big, 12) == "ds64"
            && BitConverter.ToInt64(big, 20) == huge + 72 && BitConverter.ToInt64(big, 28) == huge && BitConverter.ToInt64(big, 36) == huge / 2
            && BitConverter.ToUInt32(big, 76) == uint.MaxValue);

        var root = Path.Combine(AppContext.BaseDirectory, "._runtime", "recorder-check");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        try
        {
            var folder = Path.Combine(root, "2026", "10");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "Audion_Recording_2026-10-18_12-00-00.wav");
            // What a fall leaves: the header of the start, a second of sound after it, the marker beside.
            using (var stream = File.Create(file))
            {
                stream.Write(Wav.Header(0, Recorder.Rate));
                stream.Write(new byte[96000]);
            }
            File.WriteAllText(file + ".recording", "fallen");
            var recovered = Recorder.Recover(root);
            var header = File.ReadAllBytes(file)[..80];
            report.Check("recorder: a fall is recovered by the length of the file, its marker taken off",
                recovered.Count == 1 && BitConverter.ToUInt32(header, 76) == 96000 && !File.Exists(file + ".recording"));
            using (var reader = new NAudio.Wave.WaveFileReader(file))
                report.Check("recorder: the recording reads as a WAV of one second, mono, 48 kHz",
                    Math.Abs(reader.TotalTime.TotalSeconds - 1) < 0.001 && reader.WaveFormat.Channels == 1 && reader.WaveFormat.SampleRate == 48000,
                    $"{reader.TotalTime.TotalSeconds} s, {reader.WaveFormat}");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    static void LibraryChecks(Report report)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "._runtime", "library-check");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        try
        {
            foreach (var folder in new[] { "config", "input", "workspace" }) Directory.CreateDirectory(Path.Combine(root, folder));
            File.WriteAllText(Path.Combine(root, "config", "dictation_history.json"), """
                {"version": 1, "limit": 200, "entries": [
                  {"entry_id": "aa11", "text": "Проверка библиотеки диктовок", "created_at": "2026-09-01T10:00:00+00:00"},
                  {"entry_id": "bb22", "text": "Second dictation", "created_at": "2026-09-02T11:30:00+00:00"}]}
                """, new UTF8Encoding(false));
            var original = Path.Combine(root, "input", "Audion_Recording_2026-09-04_14-06-00.wav");
            using (var wav = new BinaryWriter(File.Create(original)))
            {
                int bytes = 16000 * 2 * 2; // two seconds of 16 kHz mono PCM16
                wav.Write("RIFF"u8); wav.Write(36 + bytes); wav.Write("WAVE"u8);
                wav.Write("fmt "u8); wav.Write(16); wav.Write((short)1); wav.Write((short)1); wav.Write(16000); wav.Write(32000); wav.Write((short)2); wav.Write((short)16);
                wav.Write("data"u8); wav.Write(bytes); wav.Write(new byte[bytes]);
            }
            var originalBytes = File.ReadAllBytes(original);
            File.WriteAllText(Path.Combine(root, "input", "Audion_Recording_2026-09-04_14-06-00.md"), "# transcript\n");
            File.WriteAllText(Path.Combine(root, "input", "Audion_Recording_2026-09-04_14-06-00.transcript.json"), "{}");
            var meeting = Path.Combine(root, "input", "meeting.m4a");
            File.WriteAllBytes(meeting, new byte[] { 0 });
            using (var jobs = new SqliteConnection($"Data Source={Path.Combine(root, "workspace", "jobs.sqlite3")}"))
            {
                jobs.Open();
                using var command = jobs.CreateCommand();
                command.CommandText = """
                    CREATE TABLE jobs (id INTEGER PRIMARY KEY AUTOINCREMENT, source_path TEXT NOT NULL UNIQUE, status TEXT NOT NULL DEFAULT 'pending',
                      created_at TEXT, started_at TEXT, completed_at TEXT, error TEXT, options_json TEXT, outputs_json TEXT);
                    INSERT INTO jobs (source_path, status, created_at, completed_at) VALUES ($meeting, 'completed', '2026-09-05T09:00:00', '2026-09-05T09:20:00');
                    INSERT INTO jobs (source_path, status, created_at) VALUES ($recording, 'completed', '2026-09-04T14:30:00');
                    """;
                command.Parameters.AddWithValue("$meeting", meeting);
                command.Parameters.AddWithValue("$recording", original);
                command.ExecuteNonQuery();
            }

            using (var library = new Library(root))
            {
                var said = library.Migrate();
                var dictations = library.Dictations();
                report.Check("library: the texts of 2.3 come in, marked as theirs", dictations.Count == 2 && dictations.All(one => one.Origin == "history-2.3"), said);
                report.Check("library: the search finds by the beginning of a word, Russian, whatever the case",
                    library.Dictations("библ").Count == 1 && library.Dictations("ПРОВЕРКА").Count == 1 && library.Dictations("second").Count == 1 && library.Dictations("нет такого").Count == 0);
                var recordings = library.Items("recording");
                var target = Path.Combine(root, "library", "recordings", "2026", "09", "Audion_Recording_2026-09-04_14-06-00.wav");
                report.Check("library: a recording of 2.3 gets its name in library\\recordings, its transcript beside it",
                    recordings.Count == 1 && string.Equals(recordings[0].Source, target, StringComparison.OrdinalIgnoreCase) && File.Exists(target)
                    && Library.OutputsOf(target).Count == 2 && recordings[0].Outcome == "completed" && Math.Abs(recordings[0].Seconds - 2) < 0.01,
                    recordings.Count > 0 ? $"{recordings[0].Source} {recordings[0].Outcome} {recordings[0].Seconds}" : said);
                report.Check("library: the file of input\\ stays where and as it was", File.Exists(original) && File.ReadAllBytes(original).SequenceEqual(originalBytes));
                var files = library.Items("file");
                report.Check("library: a job of 2.3 that is no recording is a file of the library", files.Count == 1 && files[0].Title == "meeting.m4a" && files[0].Outcome == "completed",
                    string.Join("; ", files.Select(one => one.Title + " " + one.Outcome)));
                report.Check("library: a second start brings nothing twice", library.Migrate().Length == 0 && library.Dictations().Count == 2 && library.Items("recording").Count == 1);

                library.Put(new DictationRecord { Id = "20261018-120000-000", Created = DateTime.Now, Outcome = "pasted", Text = "новая диктовка ну", CleanText = "Новая диктовка.", Mode = "light" });
                library.Pin("20261018-120000-000", true);
                var first = library.Dictations().FirstOrDefault();
                report.Check("library: a dictation of 3.0 comes in, a pinned one stands first", first is { Id: "20261018-120000-000", Pinned: true, Origin: "3.0" });
                report.Check("library: a dictation keeps the text of the engine and the one its mode pasted; the search finds either",
                    first is { Text: "новая диктовка ну", CleanText: "Новая диктовка.", Mode: "light", Pasted: "Новая диктовка." }
                    && library.Dictations("диктовка").Any(one => one.Id == first.Id) && library.Dictations("ну").Any(one => one.Id == first.Id));
                library.Delete("2.3-aa11");
                library.Migrate();
                report.Check("library: what was deleted does not come back from 2.3", library.Dictations().All(one => one.Id != "2.3-aa11") && library.Dictations().Count == 2);
            }
        }
        finally
        {
            // The pool keeps the files of SQLite open: let them go before the copy is cleared.
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}
