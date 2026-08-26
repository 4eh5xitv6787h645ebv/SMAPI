using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace LinuxMenuClickProbe;

/// <summary>Measures whether short physical X11 clicks survive the game update loop.</summary>
public sealed class ModEntry : Mod
{
    private readonly List<Process> PendingProcesses = new();
    private ProbeConfig Config = null!;
    private ProbeMenu? Menu;
    private long LastUpdateTimestamp;
    private double MaximumFrameGapMilliseconds;
    private int FrameGapCount;
    private int MeasurementGen0Collections;
    private int MeasurementGen1Collections;
    private int MeasurementGen2Collections;
    private int TicksSinceSaveLoaded = -1;
    private int TicksSinceLastClick;
    private int TicksSinceFinalClick;
    private int AttemptsStarted;
    private int ProcessesCompleted;
    private int ProcessFailures;
    private int PressEvents;
    private int ReleaseEvents;
    private bool IsComplete;

    /// <inheritdoc />
    public override void Entry(IModHelper helper)
    {
        this.Config = helper.ReadConfig<ProbeConfig>();
        this.Config.Validate();
        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.UpdateTicking += this.OnUpdateTicking;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.Input.ButtonReleased += this.OnButtonReleased;
        this.Monitor.Log(
            $"clickprobe-start runtime={Environment.Version} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} "
            + $"server_gc={System.Runtime.GCSettings.IsServerGC} clicks={this.Config.TotalClicks} hold_ms={this.Config.HoldMilliseconds} interval_ticks={this.Config.IntervalTicks}",
            LogLevel.Info
        );
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        if (!this.Config.StartAtTitle)
            return;

        this.TicksSinceSaveLoaded = 0;
        this.Monitor.Log("clickprobe-title-ready", LogLevel.Info);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.TicksSinceSaveLoaded = 0;
        this.Monitor.Log("clickprobe-save-loaded", LogLevel.Info);
    }

    private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        if (this.LastUpdateTimestamp != 0)
        {
            double gap = (now - this.LastUpdateTimestamp) * 1000d / Stopwatch.Frequency;
            this.MaximumFrameGapMilliseconds = Math.Max(this.MaximumFrameGapMilliseconds, gap);
            if (gap >= this.Config.FrameGapMilliseconds)
            {
                this.FrameGapCount++;
                this.Monitor.Log($"clickprobe-frame-gap milliseconds={gap:F3} attempts={this.AttemptsStarted}", LogLevel.Info);
            }
        }
        this.LastUpdateTimestamp = now;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        this.ReapProcesses();
        if (this.IsComplete || this.TicksSinceSaveLoaded < 0)
            return;

        this.TicksSinceSaveLoaded++;
        if (this.Menu is null)
        {
            if (this.TicksSinceSaveLoaded < this.Config.WarmupTicks)
                return;

            this.Menu = new ProbeMenu(this);
            Game1.activeClickableMenu = this.Menu;
            this.TicksSinceLastClick = this.Config.IntervalTicks;
            this.FrameGapCount = 0;
            this.MaximumFrameGapMilliseconds = 0;
            this.MeasurementGen0Collections = GC.CollectionCount(0);
            this.MeasurementGen1Collections = GC.CollectionCount(1);
            this.MeasurementGen2Collections = GC.CollectionCount(2);
            this.Monitor.Log($"clickprobe-menu-opened bounds={this.Menu.Target.bounds}", LogLevel.Info);
        }

        if (!ReferenceEquals(Game1.activeClickableMenu, this.Menu))
        {
            this.Monitor.Log($"clickprobe-fail menu-replaced by={Game1.activeClickableMenu?.GetType().FullName ?? "<none>"}", LogLevel.Error);
            this.Finish();
            return;
        }

        if (this.AttemptsStarted < this.Config.TotalClicks)
        {
            this.TicksSinceLastClick++;
            if (this.TicksSinceLastClick >= this.Config.IntervalTicks)
            {
                this.TicksSinceLastClick = 0;
                this.StartClick(this.Menu.Target.bounds.Center.X, this.Menu.Target.bounds.Center.Y);
            }
            return;
        }

        this.TicksSinceFinalClick++;
        if (this.TicksSinceFinalClick >= Math.Max(120, this.Config.IntervalTicks * 2) && this.PendingProcesses.Count == 0)
            this.Finish();
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button == SButton.MouseLeft)
            this.PressEvents++;
    }

    private void OnButtonReleased(object? sender, ButtonReleasedEventArgs e)
    {
        if (e.Button == SButton.MouseLeft)
            this.ReleaseEvents++;
    }

    private void StartClick(int x, int y)
    {
        ProcessStartInfo start = new("/usr/bin/xdotool")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            "mousemove", x.ToString(), y.ToString(),
            "sleep", "0.050",
            "mousedown", "1",
            "sleep", (this.Config.HoldMilliseconds / 1000d).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            "mouseup", "1"
        })
        {
            start.ArgumentList.Add(argument);
        }

        Process process = Process.Start(start) ?? throw new InvalidOperationException("xdotool did not start.");
        this.PendingProcesses.Add(process);
        this.AttemptsStarted++;
    }

    private void ReapProcesses()
    {
        for (int i = this.PendingProcesses.Count - 1; i >= 0; i--)
        {
            Process process = this.PendingProcesses[i];
            if (!process.HasExited)
                continue;

            this.ProcessesCompleted++;
            if (process.ExitCode != 0)
            {
                this.ProcessFailures++;
                this.Monitor.Log($"clickprobe-xdotool-fail exit={process.ExitCode} stderr={process.StandardError.ReadToEnd().Trim()}", LogLevel.Error);
            }
            process.Dispose();
            this.PendingProcesses.RemoveAt(i);
        }
    }

    internal void RecordMenuActivation()
    {
        if (this.Menu is not null)
            this.Menu.Activations++;
    }

    private void Finish()
    {
        if (this.IsComplete)
            return;

        this.IsComplete = true;
        int activations = this.Menu?.Activations ?? 0;
        bool passed = this.AttemptsStarted == this.Config.TotalClicks
            && this.ProcessesCompleted == this.Config.TotalClicks
            && this.ProcessFailures == 0
            && this.PressEvents == this.Config.TotalClicks
            && this.ReleaseEvents == this.Config.TotalClicks
            && activations == this.Config.TotalClicks;
        this.Monitor.Log(
            $"clickprobe-complete result={(passed ? "pass" : "fail")} attempts={this.AttemptsStarted} processes={this.ProcessesCompleted} "
            + $"process_failures={this.ProcessFailures} press_events={this.PressEvents} release_events={this.ReleaseEvents} activations={activations} "
            + $"frame_gaps={this.FrameGapCount} max_frame_gap_ms={this.MaximumFrameGapMilliseconds:F3} "
            + $"gc={GC.CollectionCount(0) - this.MeasurementGen0Collections}/{GC.CollectionCount(1) - this.MeasurementGen1Collections}/{GC.CollectionCount(2) - this.MeasurementGen2Collections}",
            passed ? LogLevel.Info : LogLevel.Error
        );
    }

    private sealed class ProbeMenu : IClickableMenu
    {
        private readonly ModEntry Owner;

        public ClickableComponent Target { get; }

        public int Activations { get; set; }

        public ProbeMenu(ModEntry owner)
            : base(
                x: (Game1.uiViewport.Width - 480) / 2,
                y: (Game1.uiViewport.Height - 240) / 2,
                width: 480,
                height: 240,
                showUpperRightCloseButton: false
            )
        {
            this.Owner = owner;
            this.Target = new ClickableComponent(
                new Rectangle(this.xPositionOnScreen + 90, this.yPositionOnScreen + 110, 300, 72),
                "probe-target"
            );
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (this.Target.containsPoint(x, y))
                this.Owner.RecordMenuActivation();
        }

        public override void draw(SpriteBatch b)
        {
            IClickableMenu.drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);
            IClickableMenu.drawTextureBox(b, this.Target.bounds.X, this.Target.bounds.Y, this.Target.bounds.Width, this.Target.bounds.Height, Color.White);
            Utility.drawTextWithShadow(b, $"Click probe: {this.Activations}", Game1.dialogueFont, new Vector2(this.Target.bounds.X + 24, this.Target.bounds.Y + 18), Game1.textColor);
            this.drawMouse(b);
        }
    }

    private sealed class ProbeConfig
    {
        public int TotalClicks { get; set; } = 120;
        public int HoldMilliseconds { get; set; } = 40;
        public int IntervalTicks { get; set; } = 30;
        public int WarmupTicks { get; set; } = 180;
        public int FrameGapMilliseconds { get; set; } = 50;
        public bool StartAtTitle { get; set; }

        public void Validate()
        {
            if (this.TotalClicks is < 1 or > 10000)
                throw new InvalidOperationException("TotalClicks must be between 1 and 10000.");
            if (this.HoldMilliseconds is < 1 or > 1000)
                throw new InvalidOperationException("HoldMilliseconds must be between 1 and 1000.");
            if (this.IntervalTicks is < 1 or > 3600)
                throw new InvalidOperationException("IntervalTicks must be between 1 and 3600.");
            if (this.WarmupTicks is < 0 or > 36000)
                throw new InvalidOperationException("WarmupTicks must be between 0 and 36000.");
            if (this.FrameGapMilliseconds is < 1 or > 60000)
                throw new InvalidOperationException("FrameGapMilliseconds must be between 1 and 60000.");
        }
    }
}
