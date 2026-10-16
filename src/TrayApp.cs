using System.Diagnostics;

namespace Dictee;

internal sealed class TrayApp : ApplicationContext
{
    const int MinSpeechMs = 150;

    readonly Settings settings = Settings.Load();
    readonly Bubble bubble = new();
    readonly GroqClient groq = new();
    readonly KeyboardHook hook;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem shortcutItem;
    readonly ToolStripMenuItem startupItem;
    readonly System.Windows.Forms.Timer trimTimer = new() { Interval = 3000 };
    readonly SemaphoreSlim pasteOrder = new(1, 1);

    Recording? recording;
    int transcribing;
    bool dialogOpen;

    public TrayApp()
    {
        Vocabulary.EnsureFile();

        hook = new KeyboardHook { Hotkey = settings.Hotkey };
        hook.Pressed += OnPressed;
        hook.Held += OnHeld;
        hook.Released += OnReleased;
        hook.Aborted += OnAborted;

        shortcutItem = new ToolStripMenuItem("", null, (_, _) => ShowShortcutDialog());
        startupItem = new ToolStripMenuItem("Lancer au démarrage", null, (_, _) => ToggleStartup());
        var menu = new ContextMenuStrip();
        menu.Items.Add(shortcutItem);
        menu.Items.Add("Modifier le vocabulaire…", null, (_, _) => OpenVocabulary());
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => ExitThread());

        tray = new NotifyIcon { Icon = AppIcon.Small, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowShortcutDialog();
        RefreshMenu();

        // Keep the startup entry pointing at the current executable location.
        if (Startup.Enabled) Startup.Enabled = true;

        trimTimer.Tick += (_, _) => TrimMemory();

        bool firstRun = !Settings.Exists;
        SynchronizationContext.Current!.Post(_ =>
        {
            if (firstRun) ShowShortcutDialog(firstRun: true);
            if (!GroqClient.HasApiKey)
                tray.ShowBalloonTip(5000, "Dictée", "Variable d'environnement GROQ_API_KEY introuvable.", ToolTipIcon.Warning);
            trimTimer.Start();
        }, null);
    }

    void OnPressed()
    {
        recording?.Cancel();
        recording = Recording.Start();
        groq.WarmUp();
    }

    void OnHeld()
    {
        var current = recording;
        if (current != null) bubble.ShowListening(() => current.Level);
    }

    void OnAborted()
    {
        recording?.Cancel();
        recording = null;
        if (transcribing > 0) bubble.ShowThinking();
        else if (!bubble.IsShowingError) bubble.HideBubble();
    }

    void OnReleased()
    {
        var finished = recording;
        recording = null;
        if (finished != null) _ = TranscribeAsync(finished);
    }

    async Task TranscribeAsync(Recording finished)
    {
        transcribing++;
        bubble.ShowThinking();
        string? error = null;
        bool pasted = false;
        try
        {
            var audio = await finished.StopAsync();
            if (audio.SpeechMs < MinSpeechMs)
            {
                error = "Aucune voix détectée";
                return;
            }

            // One dictation at a time from here: keeps paste order and clipboard restore consistent.
            await pasteOrder.WaitAsync();
            try
            {
                var text = await groq.TranscribeAsync(audio.Wav, settings.Language, Vocabulary.BuildPrompt());
                if (text.Length > 0)
                {
                    await Paster.PasteAsync(text + " ");
                    pasted = true;
                }
            }
            finally
            {
                pasteOrder.Release();
            }
        }
        catch (DictationException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            error = "Erreur inattendue";
        }
        finally
        {
            transcribing--;
            if (recording == null)
            {
                if (error != null) bubble.ShowError(error);
                else if (transcribing == 0 && pasted) bubble.ShowDone();
                else if (transcribing == 0) bubble.HideBubble();
            }
            trimTimer.Stop();
            trimTimer.Start();
        }
    }

    void ShowShortcutDialog(bool firstRun = false)
    {
        if (dialogOpen) return;
        dialogOpen = true;
        hook.Enabled = false;
        try
        {
            using var dialog = new ShortcutDialog(hook, settings.Hotkey, firstRun || Startup.Enabled);
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                settings.Hotkey = dialog.Hotkey;
                hook.Hotkey = dialog.Hotkey;
                Startup.Enabled = dialog.LaunchAtStartup;
                settings.Save();
            }
            else if (firstRun)
            {
                settings.Save();
            }
            RefreshMenu();
        }
        finally
        {
            hook.Enabled = true;
            dialogOpen = false;
        }
    }

    void ToggleStartup()
    {
        Startup.Enabled = !Startup.Enabled;
        RefreshMenu();
    }

    void RefreshMenu()
    {
        var keyName = settings.Hotkey.DisplayName;
        shortcutItem.Text = $"Raccourci : {keyName}…";
        startupItem.Checked = Startup.Enabled;
        var tip = $"Dictée — maintenir {keyName}";
        tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    static void OpenVocabulary()
    {
        Vocabulary.EnsureFile();
        Process.Start(new ProcessStartInfo(Vocabulary.FilePath) { UseShellExecute = true });
    }

    void TrimMemory()
    {
        trimTimer.Stop();
        if (recording != null || transcribing > 0) return;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), -1, -1); // let Windows page out idle memory
    }

    protected override void ExitThreadCore()
    {
        recording?.Cancel();
        hook.Dispose();
        tray.Visible = false;
        tray.Dispose();
        bubble.Dispose();
        trimTimer.Dispose();
        base.ExitThreadCore();
    }
}
