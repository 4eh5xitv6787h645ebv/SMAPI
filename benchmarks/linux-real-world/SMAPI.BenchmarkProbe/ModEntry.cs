using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace SMAPI.BenchmarkProbe;

/// <summary>Captures identical, bounded benchmark telemetry on the official and fork builds.</summary>
public sealed class ModEntry : Mod
{
    private const string OutputVariable = "SMAPI_BENCHMARK_OUTPUT";
    private const string SaveVariable = "SMAPI_BENCHMARK_SAVE_SHA256";

    private static ModEntry? Instance;
    private static ProbePhase CurrentPhase;
    [ThreadStatic]
    private static long BaseGameTicksThisUpdate;
    [ThreadStatic]
    private static long UpdatesSinceDrawTicks;
    [ThreadStatic]
    private static int UpdatesSinceDrawCount;

    private ProbeConfig Config = new();
    private UpdateSample[] Updates = Array.Empty<UpdateSample>();
    private DrawSample[] Draws = Array.Empty<DrawSample>();
    private readonly Marker[] Markers = new Marker[16];
    private int MarkerCount;
    private int UpdateCount;
    private int DrawCount;
    private bool BufferOverflow;
    private long EntryTimestamp;
    private long PhaseTimestamp;
    private int SettleTicksRemaining;
    private TransitionState Transition;
    private string? OutputPath;
    private string? ExpectedSaveId;
    private bool ExpectedSaveLoaded;
    private GameLocation? SteadyLocation;
    private Vector2 SteadyPosition;
    private int InvalidWorldStateTicks;
    private int LocationChangedTicks;
    private int PositionChangedTicks;
    private int GameTimeAtSteadyStart;
    private int GameTimeAtSteadyEnd;
    private PhaseTotals TotalsAtEntry;
    private PhaseTotals TotalsAtSteadyStart;
    private PhaseTotals TotalsAtSteadyEnd;
    private PhaseTotals TotalsAtExit;

    /// <inheritdoc />
    public override void Entry(IModHelper helper)
    {
        Instance = this;
        this.EntryTimestamp = Stopwatch.GetTimestamp();
        this.OutputPath = Environment.GetEnvironmentVariable(OutputVariable);
        this.ExpectedSaveId = Environment.GetEnvironmentVariable(SaveVariable);
        this.TotalsAtEntry = CapturePhaseTotals();
        this.Config = helper.ReadConfig<ProbeConfig>();
        this.Config.Validate();
        this.Updates = new UpdateSample[this.Config.MaximumUpdates];
        this.Draws = new DrawSample[this.Config.MaximumDraws];
        this.AddMarker("probe_entry");

        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Player.Warped += this.OnWarped;

        Harmony harmony = new(this.ModManifest.UniqueID);
        Type runnerType = AccessTools.TypeByName("StardewModdingAPI.Framework.SGameRunner")
            ?? throw new InvalidOperationException("SMAPI runner type not found.");
        Type coreType = AccessTools.TypeByName("StardewModdingAPI.Framework.SCore")
            ?? throw new InvalidOperationException("SMAPI core type not found.");
        HarmonyMethod outerPrefix = new(typeof(ModEntry), nameof(BeforeOuterUpdate)) { priority = Priority.First };
        HarmonyMethod outerPostfix = new(typeof(ModEntry), nameof(AfterOuterUpdate)) { priority = Priority.Last };
        harmony.Patch(
            AccessTools.DeclaredMethod(runnerType, "Update", new[] { typeof(GameTime) })
                ?? throw new InvalidOperationException("SMAPI outer update method not found."),
            prefix: outerPrefix,
            postfix: outerPostfix
        );
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(Game1), "Update", new[] { typeof(GameTime) })
                ?? throw new InvalidOperationException("Base game update method not found."),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforeBaseGameUpdate)),
            postfix: new HarmonyMethod(typeof(ModEntry), nameof(AfterBaseGameUpdate))
        );
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(GameRunner), "Draw", new[] { typeof(GameTime) })
                ?? throw new InvalidOperationException("Outer draw method not found."),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforeOuterDraw)),
            postfix: new HarmonyMethod(typeof(ModEntry), nameof(AfterOuterDraw))
        );
        harmony.Patch(
            AccessTools.DeclaredMethod(coreType, "OnGameExiting")
                ?? throw new InvalidOperationException("SMAPI game-exit method not found."),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforeGameExit))
        );
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        this.AddMarker("game_launched");
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.AddMarker("save_loaded");
        string saveFolderName = Constants.SaveFolderName ?? string.Empty;
        string loadedDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(saveFolderName))).ToLowerInvariant();
        this.ExpectedSaveLoaded = !string.IsNullOrEmpty(this.ExpectedSaveId)
            && string.Equals(loadedDigest, this.ExpectedSaveId, StringComparison.Ordinal);
        this.PhaseTimestamp = Stopwatch.GetTimestamp();
        CurrentPhase = ProbePhase.Warmup;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        switch (CurrentPhase)
        {
            case ProbePhase.Warmup when ElapsedSeconds(this.PhaseTimestamp, now) >= this.Config.WarmupSeconds:
                if (!this.IsValidSteadyState())
                    break;
                this.SteadyLocation = Game1.currentLocation;
                this.SteadyPosition = Game1.player.Position;
                this.GameTimeAtSteadyStart = Game1.timeOfDay;
                this.TotalsAtSteadyStart = CapturePhaseTotals();
                this.AddMarker("steady_state_start", now);
                this.PhaseTimestamp = now;
                CurrentPhase = ProbePhase.Measurement;
                break;

            case ProbePhase.Measurement when ElapsedSeconds(this.PhaseTimestamp, now) >= this.Config.MeasurementSeconds:
                this.GameTimeAtSteadyEnd = Game1.timeOfDay;
                this.TotalsAtSteadyEnd = CapturePhaseTotals();
                this.AddMarker("steady_state_end", now);
                this.Transition = TransitionState.WarpToTown;
                CurrentPhase = ProbePhase.Transition;
                break;

            case ProbePhase.Measurement:
                if (!this.IsValidSteadyState())
                    this.InvalidWorldStateTicks++;
                if (!ReferenceEquals(Game1.currentLocation, this.SteadyLocation))
                    this.LocationChangedTicks++;
                if (Game1.player.Position != this.SteadyPosition)
                    this.PositionChangedTicks++;
                break;

            case ProbePhase.Transition:
                this.AdvanceTransition(now);
                break;
        }
    }

    private void AdvanceTransition(long now)
    {
        if (this.SettleTicksRemaining > 0)
        {
            this.SettleTicksRemaining--;
            return;
        }

        switch (this.Transition)
        {
            case TransitionState.WarpToTown:
                this.AddMarker("warp_town_start", now);
                this.PhaseTimestamp = now;
                this.Transition = TransitionState.WaitForTown;
                Game1.warpFarmer("Town", 48, 68, false);
                break;

            case TransitionState.WarpToFarm:
                this.AddMarker("warp_town_settled", now);
                this.AddMarker("warp_farm_start", now);
                this.PhaseTimestamp = now;
                this.Transition = TransitionState.WaitForFarm;
                Game1.warpFarmer("Farm", 64, 15, false);
                break;

            case TransitionState.Exit:
                this.AddMarker("warp_farm_settled", now);
                this.AddMarker("normal_exit_requested", now);
                this.Transition = TransitionState.Done;
                CurrentPhase = ProbePhase.Done;
                Game1.game1.Exit();
                break;
        }
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        if (!ReferenceEquals(e.Player, Game1.player))
            return;

        if (this.Transition == TransitionState.WaitForTown && string.Equals(e.NewLocation.NameOrUniqueName, "Town", StringComparison.Ordinal))
        {
            this.AddMarker("warp_town_complete", now);
            this.Transition = TransitionState.WarpToFarm;
            this.SettleTicksRemaining = this.Config.TransitionSettleTicks;
        }
        else if (this.Transition == TransitionState.WaitForFarm && string.Equals(e.NewLocation.NameOrUniqueName, "Farm", StringComparison.Ordinal))
        {
            this.AddMarker("warp_farm_complete", now);
            this.Transition = TransitionState.Exit;
            this.SettleTicksRemaining = this.Config.TransitionSettleTicks;
        }
    }

    private static void BeforeGameExit()
    {
        ModEntry? instance = Instance;
        if (instance is null)
            return;
        instance.TotalsAtExit = CapturePhaseTotals();
        instance.AddMarker("game_exiting");
        instance.WriteResults();
    }

    private static void BeforeOuterUpdate(ref OuterUpdateState __state)
    {
        BaseGameTicksThisUpdate = 0;
        __state = new OuterUpdateState(
            Stopwatch.GetTimestamp(),
            GC.GetAllocatedBytesForCurrentThread(),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            CurrentPhase
        );
    }

    private static void AfterOuterUpdate(OuterUpdateState __state)
    {
        long elapsed = Stopwatch.GetTimestamp() - __state.Timestamp;
        UpdatesSinceDrawTicks += elapsed;
        UpdatesSinceDrawCount++;
        if (__state.Phase is not (ProbePhase.Measurement or ProbePhase.Transition))
            return;

        long allocated = GC.GetAllocatedBytesForCurrentThread() - __state.AllocatedBytes;
        Instance?.RecordUpdate(new UpdateSample(
            __state.Phase,
            elapsed,
            BaseGameTicksThisUpdate,
            allocated,
            GC.CollectionCount(0) - __state.Gen0,
            GC.CollectionCount(1) - __state.Gen1,
            GC.CollectionCount(2) - __state.Gen2
        ));
    }

    private static void BeforeBaseGameUpdate(ref long __state)
    {
        __state = Stopwatch.GetTimestamp();
    }

    private static void AfterBaseGameUpdate(long __state)
    {
        BaseGameTicksThisUpdate += Stopwatch.GetTimestamp() - __state;
    }

    private static void BeforeOuterDraw(ref DrawState __state)
    {
        __state = new DrawState(Stopwatch.GetTimestamp(), CurrentPhase);
    }

    private static void AfterOuterDraw(DrawState __state)
    {
        long finished = Stopwatch.GetTimestamp();
        long drawTicks = finished - __state.Timestamp;
        long updateTicks = UpdatesSinceDrawTicks;
        int updateCount = UpdatesSinceDrawCount;
        UpdatesSinceDrawTicks = 0;
        UpdatesSinceDrawCount = 0;
        if (__state.Phase is ProbePhase.Measurement or ProbePhase.Transition)
        {
            ModEntry? instance = Instance;
            if (instance is not null)
                instance.RecordDraw(new DrawSample(__state.Phase, finished - instance.EntryTimestamp, drawTicks, updateTicks, updateCount));
        }
    }

    private void RecordUpdate(UpdateSample sample)
    {
        int index = this.UpdateCount;
        if (index < this.Updates.Length)
            this.Updates[index] = sample;
        else
            this.BufferOverflow = true;
        this.UpdateCount++;
    }

    private void RecordDraw(DrawSample sample)
    {
        int index = this.DrawCount;
        if (index < this.Draws.Length)
            this.Draws[index] = sample;
        else
            this.BufferOverflow = true;
        this.DrawCount++;
    }

    private void AddMarker(string name, long? timestamp = null)
    {
        if (this.MarkerCount >= this.Markers.Length)
        {
            this.BufferOverflow = true;
            return;
        }
        this.Markers[this.MarkerCount++] = new Marker(name, timestamp ?? Stopwatch.GetTimestamp());
    }

    private void WriteResults()
    {
        if (string.IsNullOrWhiteSpace(this.OutputPath))
        {
            this.Monitor.Log($"{OutputVariable} is unset; benchmark results were not written.", LogLevel.Error);
            return;
        }

        string? parent = Path.GetDirectoryName(this.OutputPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        using StreamWriter writer = new(this.OutputPath, false, new UTF8Encoding(false));
        writer.WriteLine($"{{\"type\":\"header\",\"schema\":1,\"probeVersion\":\"1.1.0\",\"stopwatchFrequency\":{Stopwatch.Frequency},\"warmupSeconds\":{this.Config.WarmupSeconds.ToString(CultureInfo.InvariantCulture)},\"measurementSeconds\":{this.Config.MeasurementSeconds.ToString(CultureInfo.InvariantCulture)},\"transitionSettleTicks\":{this.Config.TransitionSettleTicks},\"updateCapacity\":{this.Updates.Length},\"drawCapacity\":{this.Draws.Length},\"recordedUpdates\":{Math.Min(this.UpdateCount, this.Updates.Length)},\"recordedDraws\":{Math.Min(this.DrawCount, this.Draws.Length)},\"bufferOverflow\":{this.BufferOverflow.ToString().ToLowerInvariant()},\"expectedSaveLoaded\":{this.ExpectedSaveLoaded.ToString().ToLowerInvariant()},\"invalidWorldStateTicks\":{this.InvalidWorldStateTicks},\"locationChangedTicks\":{this.LocationChangedTicks},\"positionChangedTicks\":{this.PositionChangedTicks},\"gameTimeAtSteadyStart\":{this.GameTimeAtSteadyStart},\"gameTimeAtSteadyEnd\":{this.GameTimeAtSteadyEnd}}}");

        for (int index = 0; index < this.MarkerCount; index++)
        {
            Marker marker = this.Markers[index];
            writer.WriteLine($"{{\"type\":\"marker\",\"name\":\"{marker.Name}\",\"elapsedTicks\":{marker.Timestamp - this.EntryTimestamp}}}");
        }

        writer.WriteLine($"{{\"type\":\"phaseTotals\",\"entryAllocatedBytes\":{this.TotalsAtEntry.AllocatedBytes},\"entryGc0\":{this.TotalsAtEntry.Gen0},\"entryGc1\":{this.TotalsAtEntry.Gen1},\"entryGc2\":{this.TotalsAtEntry.Gen2},\"steadyStartAllocatedBytes\":{this.TotalsAtSteadyStart.AllocatedBytes},\"steadyStartGc0\":{this.TotalsAtSteadyStart.Gen0},\"steadyStartGc1\":{this.TotalsAtSteadyStart.Gen1},\"steadyStartGc2\":{this.TotalsAtSteadyStart.Gen2},\"steadyEndAllocatedBytes\":{this.TotalsAtSteadyEnd.AllocatedBytes},\"steadyEndGc0\":{this.TotalsAtSteadyEnd.Gen0},\"steadyEndGc1\":{this.TotalsAtSteadyEnd.Gen1},\"steadyEndGc2\":{this.TotalsAtSteadyEnd.Gen2},\"exitAllocatedBytes\":{this.TotalsAtExit.AllocatedBytes},\"exitGc0\":{this.TotalsAtExit.Gen0},\"exitGc1\":{this.TotalsAtExit.Gen1},\"exitGc2\":{this.TotalsAtExit.Gen2}}}");

        int updateLimit = Math.Min(this.UpdateCount, this.Updates.Length);
        for (int index = 0; index < updateLimit; index++)
        {
            UpdateSample sample = this.Updates[index];
            writer.WriteLine($"{{\"type\":\"update\",\"phase\":\"{PhaseName(sample.Phase)}\",\"elapsedTicks\":{sample.ElapsedTicks},\"baseGameTicks\":{sample.BaseGameTicks},\"allocatedBytes\":{sample.AllocatedBytes},\"gc0\":{sample.Gen0},\"gc1\":{sample.Gen1},\"gc2\":{sample.Gen2}}}");
        }

        int drawLimit = Math.Min(this.DrawCount, this.Draws.Length);
        for (int index = 0; index < drawLimit; index++)
        {
            DrawSample sample = this.Draws[index];
            writer.WriteLine($"{{\"type\":\"draw\",\"phase\":\"{PhaseName(sample.Phase)}\",\"capturedAtTicks\":{sample.CapturedAtTicks},\"drawTicks\":{sample.DrawTicks},\"updateTicks\":{sample.UpdateTicks},\"updateCount\":{sample.UpdateCount}}}");
        }
    }

    private static double ElapsedSeconds(long start, long end)
    {
        return (end - start) / (double)Stopwatch.Frequency;
    }

    private bool IsValidSteadyState()
    {
        return this.ExpectedSaveLoaded
            && Context.IsWorldReady
            && Game1.currentLocation is not null
            && Game1.player is not null
            && Game1.activeClickableMenu is null
            && Game1.currentMinigame is null
            && !Game1.paused;
    }

    private static PhaseTotals CapturePhaseTotals()
    {
        return new PhaseTotals(GC.GetTotalAllocatedBytes(false), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    private static string PhaseName(ProbePhase phase)
    {
        return phase == ProbePhase.Measurement ? "steady" : "transition";
    }

    private readonly record struct OuterUpdateState(long Timestamp, long AllocatedBytes, int Gen0, int Gen1, int Gen2, ProbePhase Phase);
    private readonly record struct DrawState(long Timestamp, ProbePhase Phase);
    private readonly record struct UpdateSample(ProbePhase Phase, long ElapsedTicks, long BaseGameTicks, long AllocatedBytes, int Gen0, int Gen1, int Gen2);
    private readonly record struct DrawSample(ProbePhase Phase, long CapturedAtTicks, long DrawTicks, long UpdateTicks, int UpdateCount);
    private readonly record struct Marker(string Name, long Timestamp);
    private readonly record struct PhaseTotals(long AllocatedBytes, int Gen0, int Gen1, int Gen2);

    private enum ProbePhase : byte
    {
        Startup,
        Warmup,
        Measurement,
        Transition,
        Done
    }

    private enum TransitionState : byte
    {
        None,
        WarpToTown,
        WaitForTown,
        WarpToFarm,
        WaitForFarm,
        Exit,
        Done
    }
}

public sealed class ProbeConfig
{
    public double WarmupSeconds { get; set; } = 60;
    public double MeasurementSeconds { get; set; } = 180;
    public int TransitionSettleTicks { get; set; } = 300;
    public int MaximumUpdates { get; set; } = 30000;
    public int MaximumDraws { get; set; } = 30000;

    public void Validate()
    {
        if (this.WarmupSeconds < 10 || this.WarmupSeconds > 600)
            throw new InvalidOperationException("WarmupSeconds must be between 10 and 600.");
        if (this.MeasurementSeconds < 180 || this.MeasurementSeconds > 1800)
            throw new InvalidOperationException("MeasurementSeconds must be between 180 and 1800.");
        if (this.TransitionSettleTicks < 60 || this.TransitionSettleTicks > 3600)
            throw new InvalidOperationException("TransitionSettleTicks must be between 60 and 3600.");
        if (this.MaximumUpdates < 15000 || this.MaximumUpdates > 120000)
            throw new InvalidOperationException("MaximumUpdates must be between 15000 and 120000.");
        if (this.MaximumDraws < 15000 || this.MaximumDraws > 120000)
            throw new InvalidOperationException("MaximumDraws must be between 15000 and 120000.");
    }
}
