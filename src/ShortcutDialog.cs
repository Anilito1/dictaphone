namespace Dictee;

internal sealed class ShortcutDialog : Form
{
    const int VK_ESCAPE = 0x1B;

    static readonly Color Accent = Color.FromArgb(0, 122, 255);
    static readonly Color KeyBorder = Color.FromArgb(218, 218, 224);

    readonly KeyboardHook hook;
    readonly Button keyButton;
    readonly CheckBox startupBox;
    bool capturing;

    public Hotkey Hotkey { get; private set; }
    public bool LaunchAtStartup => startupBox.Checked;

    public ShortcutDialog(KeyboardHook hook, Hotkey current, bool launchAtStartup)
    {
        this.hook = hook;
        Hotkey = current;

        Text = "Dictée";
        Icon = AppIcon.Small;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.75f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(380, 252);

        var title = new Label
        {
            Text = "Raccourci de dictée",
            Font = new Font("Segoe UI Semibold", 13f),
            AutoSize = true,
            Location = new Point(18, 16),
        };
        var hint = new Label
        {
            Text = "Maintiens cette touche et parle : le texte est collé là où\nse trouve ton curseur. Un appui court reste normal.",
            ForeColor = Color.FromArgb(100, 100, 106),
            AutoSize = true,
            Location = new Point(20, 50),
        };

        keyButton = new Button
        {
            Location = new Point(20, 100),
            Size = new Size(340, 56),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(246, 246, 248),
            Font = new Font("Segoe UI Semibold", 14f),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        keyButton.FlatAppearance.BorderColor = KeyBorder;
        keyButton.Click += (_, _) => StartCapture();

        startupBox = new CheckBox
        {
            Text = "Lancer au démarrage de Windows",
            Checked = launchAtStartup,
            AutoSize = true,
            Location = new Point(20, 170),
        };

        var save = new Button { Text = "Enregistrer", DialogResult = DialogResult.OK, Location = new Point(262, 208), Size = new Size(98, 30) };
        var cancel = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, Location = new Point(156, 208), Size = new Size(98, 30) };
        AcceptButton = save;
        CancelButton = cancel;

        Controls.AddRange([title, hint, keyButton, startupBox, cancel, save]);
        ShowCurrentKey();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    void StartCapture()
    {
        if (capturing) return;
        capturing = true;
        keyButton.Text = "Appuie sur une touche…";
        keyButton.ForeColor = Accent;
        keyButton.FlatAppearance.BorderColor = Accent;
        hook.BeginCapture(key =>
        {
            capturing = false;
            if (key.Vk != VK_ESCAPE) Hotkey = key;
            ShowCurrentKey();
        });
    }

    void ShowCurrentKey()
    {
        keyButton.Text = Hotkey.DisplayName;
        keyButton.ForeColor = Color.FromArgb(28, 28, 30);
        keyButton.FlatAppearance.BorderColor = KeyBorder;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (capturing) hook.CancelCapture();
        base.OnFormClosed(e);
    }
}
