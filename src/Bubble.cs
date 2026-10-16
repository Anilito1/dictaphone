using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace Dictee;

/// <summary>
/// Siri-like black orb at the bottom of the active screen: liquid white ribbons that follow the voice while
/// listening, a light running around the edge while transcribing, a check mark once the text is pasted.
/// Click-through, never takes focus, and only animates (and costs CPU) while visible.
/// </summary>
internal sealed class Bubble : Form
{
    enum Mode { Listening, Thinking, Done, Error }

    struct Spring
    {
        public float Value, Velocity, Target;

        public void Snap(float value) => (Value, Target, Velocity) = (value, value, 0);

        public void Step(float dt, float stiffness, float damping)
        {
            Velocity += ((Target - Value) * stiffness - Velocity * damping) * dt;
            Value += Velocity * dt;
        }
    }

    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    const uint MONITOR_DEFAULTTONEAREST = 2;

    // Logical (96 dpi) dimensions
    const int CanvasWidth = 420, CanvasHeight = 140, BottomMargin = 8;
    const float PillHeight = 58;
    const float DoneSeconds = 0.65f, ErrorSeconds = 2.2f;
    const int WavePoints = 60;

    static readonly (float Frequency, float Speed, float Phase, int Alpha, float Response)[] Ribbons =
    [
        (2.2f, 2.4f, 0.0f, 60, 5f),
        (3.4f, -3.1f, 1.8f, 85, 9f),
        (4.7f, 3.9f, 3.6f, 115, 15f),
    ];

    static readonly Color Clear = Color.FromArgb(0, 255, 255, 255);
    static readonly SolidBrush ShadowBrush = new(Color.FromArgb(6, 0, 0, 0));
    static readonly SolidBrush TextBrush = new(Color.FromArgb(240, 255, 255, 255));
    static readonly Font MeasureFont = new("Segoe UI Semibold", 13, GraphicsUnit.Pixel);

    readonly System.Windows.Forms.Timer frameTimer = new() { Interval = 15 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly float[] ribbonLevels = new float[Ribbons.Length];
    readonly PointF[] outline = new PointF[WavePoints * 2];

    Mode mode;
    Func<float> levelSource = () => 0;
    string message = "";
    float messageWidth;
    bool showing;
    float opacity, level, modeTime, scale = 1;
    Spring width, zoom;
    double lastFrame;
    LayeredCanvas? canvas;
    Font? textFont;
    Point location;

    public Bubble()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        frameTimer.Tick += (_, _) => Frame();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnPaintBackground(PaintEventArgs e) { }

    public bool IsShowingError => showing && mode == Mode.Error;

    public void ShowListening(Func<float> source)
    {
        levelSource = source;
        SetMode(Mode.Listening);
    }

    public void ShowThinking() => SetMode(Mode.Thinking);

    public void ShowDone() => SetMode(Mode.Done);

    public void ShowError(string text)
    {
        message = text;
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) messageWidth = g.MeasureString(text, MeasureFont).Width;
        SetMode(Mode.Error);
    }

    public void HideBubble() => showing = false;

    void SetMode(Mode next)
    {
        if (next != mode || !showing) modeTime = 0;
        if (next == Mode.Done && showing) zoom.Velocity -= 2.5f; // little squash-and-bounce on success
        mode = next;
        showing = true;
        if (!frameTimer.Enabled) Appear();
    }

    void Appear()
    {
        var foreground = Native.GetForegroundWindow();
        var screen = foreground != IntPtr.Zero ? Screen.FromHandle(foreground) : Screen.PrimaryScreen!;
        var monitor = Native.MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        scale = Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96f : 1f;

        int w = (int)(CanvasWidth * scale), h = (int)(CanvasHeight * scale);
        var area = screen.WorkingArea;
        location = new Point(area.Left + (area.Width - w) / 2, area.Bottom - h - (int)(BottomMargin * scale));

        canvas?.Dispose();
        canvas = new LayeredCanvas(w, h);
        textFont?.Dispose();
        textFont = new Font("Segoe UI Semibold", 13 * scale, GraphicsUnit.Pixel);

        // Grow from a small dot into the capsule, Dynamic Island style.
        width.Snap(PillHeight);
        zoom.Snap(0.45f);
        opacity = 0;
        level = 0;
        Array.Clear(ribbonLevels);

        Bounds = new Rectangle(location, new Size(w, h));
        lastFrame = clock.Elapsed.TotalSeconds;
        if (!Visible) Show();
        frameTimer.Start();
        Frame();
    }

    /// <summary>A round orb, except when a message needs room.</summary>
    float TargetWidth() => mode == Mode.Error ? Math.Min(messageWidth + 50, CanvasWidth - 60) : PillHeight;

    void Frame()
    {
        double now = clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(now - lastFrame, 0.05);
        lastFrame = now;
        modeTime += dt;

        if (showing && ((mode == Mode.Done && modeTime > DoneSeconds) || (mode == Mode.Error && modeTime > ErrorSeconds)))
            showing = false;

        opacity = showing ? Math.Min(1, opacity + dt / 0.14f) : Math.Max(0, opacity - dt / 0.22f);
        if (!showing && opacity <= 0)
        {
            frameTimer.Stop();
            Hide();
            canvas?.Dispose();
            canvas = null;
            return;
        }

        width.Target = showing ? TargetWidth() : PillHeight;
        zoom.Target = showing ? 1f + 0.08f * level : 0.7f; // the orb breathes with the voice
        for (float remaining = dt; remaining > 0; remaining -= 1 / 240f)
        {
            float step = Math.Min(remaining, 1 / 240f);
            width.Step(step, 320, 22);
            zoom.Step(step, 380, 24);
        }

        float targetLevel = mode == Mode.Listening ? Math.Clamp(levelSource(), 0, 1) : 0;
        level += (targetLevel - level) * (1 - MathF.Exp(-dt * (targetLevel > level ? 28 : 8)));
        for (int i = 0; i < Ribbons.Length; i++)
            ribbonLevels[i] += (level - ribbonLevels[i]) * (1 - MathF.Exp(-dt * Ribbons[i].Response));

        Render((float)now);
    }

    void Render(float time)
    {
        var c = canvas!;
        var g = c.Graphics;
        g.ResetTransform();
        g.ResetClip();
        g.Clear(Color.Transparent);

        float cx = c.Width / 2f, cy = c.Height / 2f, z = Math.Max(zoom.Value, 0.05f);
        g.TranslateTransform(cx, cy);
        g.ScaleTransform(z, z);
        g.TranslateTransform(-cx, -cy);

        float w = Math.Max(width.Value, PillHeight * 0.8f) * scale, h = PillHeight * scale;
        var pill = new RectangleF(cx - w / 2, cy - h / 2, w, h);
        using var pillPath = Shapes.Capsule(pill);

        DrawShadow(g, pill);
        if (mode == Mode.Listening) DrawHalo(g, pill);
        using (var fill = new LinearGradientBrush(pill, Color.FromArgb(250, 40, 40, 44), Color.FromArgb(250, 6, 6, 8), LinearGradientMode.Vertical))
            g.FillPath(fill, pillPath);

        g.SetClip(pillPath);
        DrawGloss(g, pill);
        switch (mode)
        {
            case Mode.Listening: DrawVoice(g, pill, time); break;
            case Mode.Thinking: DrawThinking(g, pill, time); break;
            case Mode.Done: DrawCheck(g, pill); break;
            case Mode.Error: DrawMessage(g, pill); break;
        }
        g.ResetClip();

        if (mode == Mode.Thinking) DrawEdgeLight(g, pillPath, pill, time);
        using (var border = new Pen(Color.FromArgb(42 + (int)(80 * level), 255, 255, 255), Math.Max(1, scale)))
            g.DrawPath(border, pillPath);

        float fade = opacity * opacity * (3 - 2 * opacity);
        c.Present(Handle, location, (byte)(255 * fade));
    }

    void DrawShadow(Graphics g, RectangleF pill)
    {
        for (int i = 12; i >= 1; i--)
        {
            var r = RectangleF.Inflate(pill, i * scale, i * scale);
            r.Offset(0, 4 * scale);
            using var path = Shapes.Capsule(r);
            g.FillPath(ShadowBrush, path);
        }
    }

    void DrawHalo(Graphics g, RectangleF pill)
    {
        int alpha = (int)(11 * level);
        if (alpha == 0) return;
        using var brush = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
        for (int i = 6; i >= 1; i--)
        {
            using var path = Shapes.Capsule(RectangleF.Inflate(pill, i * 1.6f * scale, i * 1.6f * scale));
            g.FillPath(brush, path);
        }
    }

    static void DrawGloss(Graphics g, RectangleF pill)
    {
        var top = new RectangleF(pill.Left, pill.Top, pill.Width, pill.Height * 0.55f);
        using var gloss = new LinearGradientBrush(RectangleF.Inflate(top, 0, 1), Color.FromArgb(34, 255, 255, 255), Clear, LinearGradientMode.Vertical);
        g.FillRectangle(gloss, top);
    }

    /// <summary>Three translucent mirrored ribbons whose thickness follows the voice; overlaps read as a liquid glow.</summary>
    void DrawVoice(Graphics g, RectangleF pill, float time)
    {
        float inset = pill.Height * 0.13f, left = pill.Left + inset, span = pill.Width - inset * 2;
        if (span < 4) return;
        float cy = pill.Top + pill.Height / 2, maxHeight = pill.Height * 0.3f;

        for (int i = 0; i < Ribbons.Length; i++)
        {
            var ribbon = Ribbons[i];
            float energy = 0.05f + 0.95f * MathF.Sqrt(ribbonLevels[i]);
            for (int p = 0; p < WavePoints; p++)
            {
                float u = p / (WavePoints - 1f), x = u * 4 - 2;
                float envelope = MathF.Pow(4 / (4 + x * x * x * x), 4);
                float wave = 0.55f + 0.45f * MathF.Sin(x * ribbon.Frequency + time * ribbon.Speed + ribbon.Phase);
                float breathe = 0.8f + 0.2f * MathF.Sin(time * 1.9f + i * 2.3f + x * 0.8f);
                float half = maxHeight * envelope * energy * wave * breathe;
                float px = left + u * span;
                outline[p] = new PointF(px, cy - half);
                outline[WavePoints * 2 - 1 - p] = new PointF(px, cy + half);
            }
            using var path = new GraphicsPath();
            path.AddLines(outline);
            path.CloseFigure();
            using var brush = new SolidBrush(Color.FromArgb(ribbon.Alpha, 255, 255, 255));
            g.FillPath(brush, path);
        }

        DrawGlowBar(g, new RectangleF(left, cy - 0.6f * scale, span, 1.2f * scale), 160);
    }

    void DrawThinking(Graphics g, RectangleF pill, float time)
    {
        DrawVoice(g, pill, time); // ribbons settle into a thin breathing line

        float inset = pill.Height * 0.13f, left = pill.Left + inset, span = pill.Width - inset * 2;
        float u = time * 0.9f % 1.3f - 0.15f;
        float glow = 22 * scale, cy = pill.Top + pill.Height / 2;
        DrawGlowBar(g, new RectangleF(left + u * span - glow / 2, cy - 1.6f * scale, glow, 3.2f * scale), 235);
    }

    static void DrawGlowBar(Graphics g, RectangleF bar, int alpha)
    {
        if (bar.Width < 1) return;
        using var brush = new LinearGradientBrush(RectangleF.Inflate(bar, 1, 1), Clear, Clear, LinearGradientMode.Horizontal)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = [Clear, Color.FromArgb(alpha, 255, 255, 255), Clear],
                Positions = [0, 0.5f, 1],
            },
        };
        using var path = Shapes.Capsule(bar);
        g.FillPath(brush, path);
    }

    /// <summary>A light band rotating across the capsule, lighting up its edge as it passes.</summary>
    void DrawEdgeLight(Graphics g, GraphicsPath pillPath, RectangleF pill, float time)
    {
        using var brush = new LinearGradientBrush(RectangleF.Inflate(pill, 2, 2), Clear, Clear, time * 170 % 360)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = [Clear, Clear, Color.FromArgb(220, 255, 255, 255), Clear, Clear],
                Positions = [0, 0.36f, 0.5f, 0.64f, 1],
            },
        };
        using var pen = new Pen(brush, 1.8f * scale);
        g.DrawPath(pen, pillPath);
    }

    void DrawCheck(Graphics g, RectangleF pill)
    {
        float progress = Math.Clamp((modeTime - 0.08f) / 0.24f, 0, 1);
        if (progress <= 0) return;
        progress = 1 - MathF.Pow(1 - progress, 3);

        float s = scale, cx = pill.Left + pill.Width / 2, cy = pill.Top + pill.Height / 2;
        var a = new PointF(cx - 7.5f * s, cy + 0.5f * s);
        var b = new PointF(cx - 2.5f * s, cy + 5.5f * s);
        var c = new PointF(cx + 8f * s, cy - 6f * s);
        float first = Distance(a, b), second = Distance(b, c), drawn = (first + second) * progress;

        using var pen = new Pen(Color.White, 2.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (drawn <= first) g.DrawLine(pen, a, Lerp(a, b, drawn / first));
        else g.DrawLines(pen, [a, b, Lerp(b, c, (drawn - first) / second)]);
    }

    void DrawMessage(Graphics g, RectangleF pill)
    {
        float s = scale, dot = 6 * s;
        using (var dotBrush = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
            g.FillEllipse(dotBrush, pill.Left + 17 * s, pill.Top + (pill.Height - dot) / 2, dot, dot);
        var textArea = new RectangleF(pill.Left + 31 * s, pill.Top, pill.Width - 38 * s, pill.Height);
        using var format = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.None };
        g.DrawString(message, textFont!, TextBrush, textArea, format);
    }

    static float Distance(PointF a, PointF b) => MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    static PointF Lerp(PointF a, PointF b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            frameTimer.Dispose();
            canvas?.Dispose();
            textFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
