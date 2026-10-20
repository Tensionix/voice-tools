using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;

namespace AudionVoiceTools;

/// <summary>
/// AudionVoiceTools.exe - the program in the tray: the capsule at the top of the screen, the hotkey, the microphone and
/// the paste of dictation, and the manager of dictations and files, opened from the tray.
///
///   AudionVoiceTools.exe [--self-test --to FILE] [--just-built]
///   AudionVoiceTools.exe --stand [--fake-mic WAV --cycles N --hold SECONDS --data DIR --summary FILE]
///   AudionVoiceTools.exe [--model GGUF] [--backend vulkan|cpu|auto] [--scale 0.7] [--mic keep|press]
///   AudionVoiceTools.exe --bench [--set DIR | --from-library DAYS] [--cloud] [--runs N] [--to FILE.json]
///
/// --stand: everything beyond the screens, a WAV for the microphone, no keys and no clipboard of the person
/// (Core\Stand.cs). --self-test: the window is put together and not shown, then checked (Core\SelfTest.cs).
/// </summary>
public static class Program
{
    /// <summary>How dictation runs: what the stand and the probe of 3.0 set from the command line.</summary>
    public sealed class Options
    {
        public bool Stand;
        public string? FakeMic;
        public int Cycles = 5;
        public double HoldSeconds = 6;
        public string DataRoot = "";
        public bool Record;
        /// <summary>A stand of the cloud: the engine, its address (the fake server of the stand), how long the stop waits.</summary>
        public string Cloud = "";
        public string CloudEndpoint = "";
        public double CloudWait;
        /// <summary>--cloud-stand text|mute: the stand serves xAI itself - one that answers, or one that keeps silent.</summary>
        public string CloudStand = "";
        public string Summary = "";
        public string Model = "";
        /// <summary>--model named a model outright: the one chosen on DICTATIONS is not taken.</summary>
        public bool ModelGiven;
        public string Backend = "vulkan";
        public double Scale = 0.7;
        public bool KeepMicOpen = true;
    }

    public static Options Run { get; } = new();

    /// <summary>
    /// AUDION_WALK=1: the built window is walked by UI Automation (Source\Tools\release-walk.ps1). The window and its
    /// questions stand off every screen and take no focus.
    /// </summary>
    public static bool Walk { get; } = Environment.GetEnvironmentVariable("AUDION_WALK") == "1";

    [STAThread]
    public static int Main(string[] args)
    {
        // --bench: the engines of dictation measured on the same takes, nothing shown (Core\Bench.cs).
        if (args.Any(arg => Is(arg, "--bench"))) return Core.Bench.Run(args);
        bool selfTest = false, migrate = false;
        var o = Run;
        o.DataRoot = Core.Paths.Root;
        o.Model = Path.Combine(Core.Paths.Root, "models", "transcribe", "gigaam-v3-e2e-rnnt-Q8_0.gguf");
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i].Trim().Trim('"') : "";
            if (Is(a, "--self-test")) selfTest = true;
            // --migrate: what 2.3 kept into the library of the folder, nothing shown; what came - into --to, or the console.
            else if (Is(a, "--migrate")) migrate = true;
            // --to "<file>": where --self-test writes what it checked - the build of App\ reads it.
            else if (Is(a, "--to")) Core.SelfTest.ReportFile = Next();
            else if (Is(a, "--just-built")) Core.SelfTest.JustBuilt = true;
            else if (Is(a, "--stand")) o.Stand = true;
            else if (Is(a, "--fake-mic")) o.FakeMic = Next();
            else if (Is(a, "--cycles")) o.Cycles = int.Parse(Next());
            else if (Is(a, "--hold")) o.HoldSeconds = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
            else if (Is(a, "--data")) o.DataRoot = Next();
            else if (Is(a, "--summary")) o.Summary = Next();
            // --record: the stand records too - a recording over its dictations, a pause, then a fall recovered.
            else if (Is(a, "--record")) o.Record = true;
            else if (Is(a, "--cloud-stand")) o.CloudStand = Next();
            else if (Is(a, "--cloud-wait")) o.CloudWait = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
            else if (Is(a, "--model")) { o.Model = Next(); o.ModelGiven = true; }
            else if (Is(a, "--backend")) o.Backend = Next();
            else if (Is(a, "--mic")) o.KeepMicOpen = Next() != "press";
            else if (Is(a, "--scale")) o.Scale = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (o.Summary.Length == 0) o.Summary = Path.Combine(o.DataRoot, "stand-summary.json");

        if (selfTest) return SelfTest();
        if (migrate) return Migrate(o.DataRoot);
#if !DEBUG
        // One program per folder: a second start brings the manager of the first forward and ends - two hotkeys and two
        // capsules on one chord were a trouble of 2.3.0 (the inventory, 5.3). A stand or a check is not held back.
        if (!o.Stand && !Core.OneWindow.Claim("AudionVoiceTools")) return 0;
#endif
        Core.Paths.DropDeadSessions();
        try { Core.Lang.Set(Core.Settings.Load().LanguageInUse); } catch { }
        try { SetCurrentProcessExplicitAppUserModelID("Audion.VoiceTools"); } catch { }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return Environment.ExitCode;
        }
        catch (Exception error)
        {
            // The program never came up: the reason goes into logs\gui-startup-TIME.log and is shown.
            Core.Guard.StartFailed(error);
            return 1;
        }
    }

    static bool Is(string arg, string word) => string.Equals(arg, word, StringComparison.OrdinalIgnoreCase);

    /// <summary>The window put together and not shown, then the parts that need no screen checked: 0 - every check
    /// passed, 2 - otherwise, the reason on the error stream.</summary>
    static int SelfTest()
    {
        try
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            _ = new MainWindow(null);
            return Core.SelfTest.Run() ? 0 : 2;
        }
        catch (Exception error)
        {
            try { Console.Error.WriteLine(error); } catch { }
            return 2;
        }
        finally
        {
            Core.Paths.DropSession();
        }
    }

    /// <summary>
    /// The library opened without the window and what 2.3 kept brought in - the start of the window does the same; 0 - done,
    /// 1 - the library did not open. Nothing of 2.3 is changed or deleted.
    /// </summary>
    static int Migrate(string root)
    {
        string said;
        int code = 0;
        try
        {
            using var library = new Core.Library(root);
            var came = library.Migrate();
            said = (came.Length > 0 ? came : "nothing new") + $"; in the library: {library.Dictations(most: 1_000_000).Count} dictations, "
                + $"{library.Items("recording", 1_000_000).Count} recordings, {library.Items("file", 1_000_000).Count} files ({library.FilePath})";
        }
        catch (Exception error)
        {
            said = "the library did not open: " + error.Message;
            code = 1;
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        try
        {
            if (Core.SelfTest.ReportFile.Length > 0) File.WriteAllText(Core.SelfTest.ReportFile, said + Environment.NewLine, new System.Text.UTF8Encoding(false));
            else Console.Out.WriteLine(said);
        }
        catch { }
        return code;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // JetBrains Mono (SIL OFL, Assets\Fonts\JetBrainsMono) ships inside the program like Inter.
            .ConfigureFonts(fonts => fonts.AddFontCollection(new Avalonia.Media.Fonts.EmbeddedFontCollection(
                new Uri("fonts:JetBrainsMono"), new Uri("avares://AudionVoiceTools/Assets/Fonts/JetBrainsMono"))))
            // The menus and tooltips are drawn inside the window, not as native windows of their own.
            .With(new Win32PlatformOptions { OverlayPopups = true })
            .LogToTrace();
}
