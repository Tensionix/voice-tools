#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AudionVoiceTools.Views;

namespace AudionVoiceTools;

/// <summary>
/// A check of the manager without a mouse (a Debug build): AUDION_SHOT=FOLDER opens it off every screen, takes its
/// picture section by section and sheet by sheet, and ends the program. Nothing is shown to the person at the screen.
/// </summary>
public partial class MainWindow
{
    public static string ShotFolder { get; } = Environment.GetEnvironmentVariable("AUDION_SHOT") ?? "";

    /// <summary>The dictionary SETTINGS shows in a picture: made up - the one of the person is not read for a picture.</summary>
    const string ShotDictionary = "ФГИС ТП\nKubernetes\nгитхаб экшнс -> GitHub Actions\nкилометров в час -> км/ч\n# единицы\nградусов цельсия -> °C";

    async Task TakeShots(string folder)
    {
        Directory.CreateDirectory(folder);
        async Task Settle() { for (int i = 0; i < 6; i++) await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); await Task.Delay(150); }
        // The library of a stand (AUDION_SHOT_DATA) opens on the thread of the disk: the pictures wait for it.
        if (Environment.GetEnvironmentVariable("AUDION_SHOT_DATA") is { Length: > 0 })
        {
            for (int i = 0; i < 100 && Core.Library.Current is null; i++) await Task.Delay(100);
            LoadDictations();
            LoadRecordings();
            await Task.Delay(300);
        }
        foreach (var section in new[] { "dictations", "recordings", "files", "dictionary", "journal" })
        {
            _section = section;
            ShowSection();
            await Settle();
            Save(Path.Combine(folder, $"10-{section}.png"));
            // The strip of tools comes with the pointer; a picture has none - the first dictation shows it as the pointer would.
            // The search field asks the list again a quarter of a second after it is made: the strip is shown on the list that stays.
            if (section == "dictations") { await Task.Delay(700); await Settle(); }
            if (section == "dictations" && Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this).OfType<Border>().FirstOrDefault(one => one.Classes.Contains("pill")) is { } pill)
            {
                pill.IsVisible = true;
                await Settle();
                Save(Path.Combine(folder, "11-dictations-tools.png"));
                // One tool as the pointer over it shows it: its frame and its colour.
                if (Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(pill).OfType<Button>().FirstOrDefault() is { } first)
                {
                    ((Avalonia.Controls.IPseudoClasses)first.Classes).Set(":pointerover", true);
                    await Settle();
                    Save(Path.Combine(folder, "12-dictations-tool-over.png"));
                }
            }
        }
        foreach (var sheet in new[] { "settings", "about", "setup" })
        {
            ToggleSheet(sheet);
            await Settle();
            Save(Path.Combine(folder, $"20-{sheet}.png"));
            ToggleSheet(sheet);
        }
        // The question of the start over the window, and its INSTALL LATER: asked here, since a check never asks by itself.
        if (Needed().Count > 0)
        {
            SetupDialog.DevShown = async dialog =>
            {
                await Settle();
                Save(Path.Combine(folder, "40-setup-question.png"), dialog);
                dialog.DevTick();
                await Settle();
                Save(Path.Combine(folder, "41-setup-later.png"), dialog);
                // OPEN SETUP pressed: the question goes, the sheet SETUP comes with its button pressed.
                if (!dialog.DevOpen()) dialog.DevClose();
            };
            await AskSetup(later: true);
            if (_sheet == "setup")
            {
                await Settle();
                Save(Path.Combine(folder, "42-setup-open.png"));
                File.WriteAllText(Path.Combine(folder, "42-setup-open.txt"), $"setup_open: sheet setup, SETUP pressed {SetupButton.IsChecked == true}");
                ToggleSheet("setup");
            }
            else File.WriteAllText(Path.Combine(folder, "42-setup-open.txt"), $"setup_open: sheet {(_sheet.Length > 0 ? _sheet : "none")}");
        }
        foreach (var look in new[] { "light", "codedark" })
        {
            SetTheme(look);
            _section = "files";
            ShowSection();
            await Settle();
            Save(Path.Combine(folder, $"30-files-{look}.png"));
        }
        // The capsule, beyond the screens: open at rest, dictating with its words and level, recording.
        var capsule = new Capsule(0.7, offScreen: true);
        capsule.Show();
        await Task.Delay(400);
        await Settle();
        capsule.Post(new Core.Snapshot(Core.State.Ready, "", "gigaam-v3-e2e-rnnt-Q8_0 · Vulkan0", 0, ""));
        await Settle();
        capsule.DevReady();
        await Settle();
        Save(Path.Combine(folder, "50-capsule-ready.png"), capsule);
        capsule.Post(new Core.Snapshot(Core.State.Listening, "двигаемся дальше и проверяем, как выглядит капсула", "gigaam-v3-e2e-rnnt-Q8_0 · Vulkan0",
            System.Diagnostics.Stopwatch.GetTimestamp() - 12 * System.Diagnostics.Stopwatch.Frequency, ""));
        await Settle();
        capsule.Level(0.55f);
        await Settle();
        Save(Path.Combine(folder, "51-capsule-listening.png"), capsule);
        capsule.Post(new Core.Snapshot(Core.State.Ready, "", "gigaam-v3-e2e-rnnt-Q8_0 · Vulkan0", 0, ""));
        capsule.PostRecording(new Core.RecSnapshot(Core.RecState.Recording, "", TimeSpan.FromSeconds(75), ""));
        await Settle();
        capsule.RecordLevel(0.4f);
        await Settle();
        Save(Path.Combine(folder, "52-capsule-recording.png"), capsule);
        capsule.PostRecording(new Core.RecSnapshot(Core.RecState.Idle, "", TimeSpan.Zero, ""));
        capsule.Post(new Core.Snapshot(Core.State.Finalizing, "ну короче завтра в десять созвон", "gigaam-v3-e2e-rnnt-Q8_0 · Vulkan0", 0, "", Mode: "Слегка"));
        await Settle();
        Save(Path.Combine(folder, "53-capsule-mode.png"), capsule);
        capsule.Close();

        // The menu of the tray with its microphones out, beyond the screens; the devices are made up for the picture.
        void Nothing() { }
        var microphones = new System.Collections.Generic.List<CapsuleMenu.Item?>
        {
            new("Автоопределение", true, true, Nothing), new("Стерео микшер (Realtek(R) Audio)", false, true, Nothing),
            new("Набор микрофонов (Технология Intel® Smart Sound для цифровых микрофонов)", false, true, Nothing),
        };
        var trayItems = new System.Collections.Generic.List<CapsuleMenu.Item?>
        {
            new("Вставить последнюю диктовку", false, true, Nothing, "clipboard-paste"), new("История", false, true, Nothing, "scroll-text"), null,
            new("Начать запись (Right Alt + F11)", false, true, Nothing, "file-audio"),
            new("Движок диктовки", false, true, Nothing, "audio-lines", Sub: new System.Collections.Generic.List<CapsuleMenu.Item?> { new("GigaAM · transcribe.cpp", true, true, Nothing) }),
            new("Микрофон", false, true, Nothing, "sm7b", Sub: microphones),
            new("Положение панели", false, true, Nothing, "app-window", Sub: new System.Collections.Generic.List<CapsuleMenu.Item?> { new("Вверху экрана", true, true, Nothing), new("Внизу экрана", false, true, Nothing) }),
            null, new("Показывать плавающую панель", true, true, Nothing), null,
            new("Открыть Audion Voice Tools", false, true, Nothing, "maximize-2"), new("Настройки", false, true, Nothing, "settings"), null,
            new("Выход", false, true, Nothing, "power"),
        };
        var trayMenu = CapsuleMenu.DevAt(new Avalonia.PixelPoint(-30000, -30000), trayItems);
        await Task.Delay(300);
        await Settle();
        trayMenu.DevOpen("Микрофон");
        await Task.Delay(300);
        await Settle();
        Save(Path.Combine(folder, "60-tray-menu.png"), trayMenu);
        trayMenu.Close();

        // The menu of ⌄ of the capsule: the modes of the text, the engines in a menu of their own (Capsule.MenuItems).
        var modeItems = new System.Collections.Generic.List<CapsuleMenu.Item?>();
        foreach (var mode in Core.TextModes.Modes) modeItems.Add(new(Core.TextModes.TitleOf(mode), mode == "light", true, Nothing));
        modeItems.Add(null);
        modeItems.Add(new("Движок диктовки", false, true, Nothing, Sub: new System.Collections.Generic.List<CapsuleMenu.Item?>
        {
            new("GigaAM · transcribe.cpp", true, true, Nothing), new("xAI", false, true, Nothing), new("OpenAI Realtime", false, true, Nothing),
            new("ElevenLabs - нет ключа", false, false, Nothing), new("OpenAI · после остановки", false, true, Nothing),
        }));
        modeItems.Add(new("Положение панели", false, true, Nothing, Sub: new System.Collections.Generic.List<CapsuleMenu.Item?> { new("Вверху экрана", true, true, Nothing), new("Внизу экрана", false, true, Nothing) }));
        modeItems.Add(new("Настроить…", false, true, Nothing));
        modeItems.Add(new("Открыть Audion Voice Tools", false, true, Nothing));
        var modeMenu = CapsuleMenu.DevAt(new Avalonia.PixelPoint(-30000, -30000), modeItems);
        await Task.Delay(300);
        await Settle();
        modeMenu.DevOpen("Движок диктовки");
        await Task.Delay(300);
        await Settle();
        Save(Path.Combine(folder, "61-capsule-menu.png"), modeMenu);
        modeMenu.Close();
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            App.Quit(desktop);
    }

    /// <summary>The keys the pictures show: made up - the keys of the program, even their last characters, are not read for a picture.</summary>
    static System.Text.Json.JsonElement ShotKeys() => System.Text.Json.JsonDocument.Parse("""
                {"keys": [
                 {"id": "huggingface", "name": "Hugging Face", "why": {"ru": "Разделение по говорящим на видеокарте (Community-1) и быстрая докачка моделей.", "en": "Speaker separation on the video card (Community-1) and fast downloads of models."}, "url": "https://huggingface.co/settings/tokens", "present": true, "fingerprint": "…demo", "source": "file", "env": "HF_TOKEN", "checkable": true},
                 {"id": "openai", "name": "OpenAI", "why": {"ru": "Облачная расшифровка файлов и очистка текста.", "en": "Cloud transcription of files and the cleanup of a text."}, "url": "https://platform.openai.com/api-keys", "present": false, "fingerprint": "", "source": "", "env": "OPENAI_API_KEY", "checkable": true},
                 {"id": "xai", "name": "xAI", "why": {"ru": "Облачная расшифровка файлов.", "en": "Cloud transcription of files."}, "url": "https://console.x.ai", "present": true, "fingerprint": "…test", "source": "env", "env": "XAI_API_KEY", "checkable": true}]}
                """).RootElement.Clone();

    void Save(string file, Window? of = null)
    {
        var window = of ?? this;
        double scale = window.RenderScaling;
        var size = new Avalonia.PixelSize((int)(window.Bounds.Width * scale), (int)(window.Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Avalonia.Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        using var picture = File.Create(file);
        bitmap.Save(picture, new PngBitmapEncoderOptions());
    }
}
#endif
