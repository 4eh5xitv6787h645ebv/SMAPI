using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using StardewModdingAPI.Framework.ModLoading;
using StardewModdingAPI.Toolkit.Framework.Clients.WebApi;

namespace StardewModdingAPI.Framework.Health;

/// <summary>Copies privacy-safe runtime observations into the bounded session health ledger.</summary>
/// <remarks>This class deliberately accepts structured runtime values only. It never retains paths, URLs, exception messages, or stack traces.</remarks>
internal sealed class ModHealthRuntimeObserver
{
    /*********
    ** Fields
    *********/
    private readonly ModHealthLedger Ledger;
    private readonly ConditionalWeakTable<IModMetadata, Registration> Registrations = new();


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="ledger">The bounded health ledger to update.</param>
    public ModHealthRuntimeObserver(ModHealthLedger ledger)
    {
        this.Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    /// <summary>Register a mod as soon as discovery produces its metadata.</summary>
    public void ObserveDiscovery(IModMetadata mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        this.Registrations.GetValue(mod, this.RegisterMod);
    }

    /// <summary>Get the current structured ledger status for a mod.</summary>
    public ModHealthLedgerModStatus GetStatus(IModMetadata mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        return GetStatusCore(mod);
    }

    /// <summary>Copy a mod's authoritative metadata after it changes.</summary>
    /// <param name="mod">The changed mod metadata.</param>
    /// <param name="previousStatus">The ledger status before the change.</param>
    public void ObserveMetadataChanged(IModMetadata mod, ModHealthLedgerModStatus previousStatus)
    {
        ArgumentNullException.ThrowIfNull(mod);
        Registration registration = this.Registrations.GetValue(mod, this.RegisterMod);
        lock (registration.SyncRoot)
            this.Ledger.UpdateMod(registration.Key, previousStatus, CreateObservation(mod, registration.UpdateStatus));
    }

    /// <summary>Record an explicit update-check state before or instead of an immutable result assignment.</summary>
    /// <remarks>An assigned <see cref="ModEntryModel"/> remains authoritative over this transitional state.</remarks>
    public void ObserveUpdateStatus(IModMetadata mod, ModHealthUpdateStatus updateStatus)
    {
        ArgumentNullException.ThrowIfNull(mod);
        Registration registration = this.Registrations.GetValue(mod, this.RegisterMod);
        ModHealthLedgerModStatus currentStatus = GetStatusCore(mod);
        lock (registration.SyncRoot)
        {
            registration.UpdateStatus = updateStatus;
            this.Ledger.UpdateMod(registration.Key, currentStatus, CreateObservation(mod, updateStatus));
        }
    }

    /// <summary>Record one structured callback failure without retaining exception details.</summary>
    public void ObserveCallbackFailure(
        IModMetadata mod,
        ModHealthExecutionPhase phase,
        ModHealthOperationKind operation,
        string callbackIdentity,
        Exception exception,
        IModMetadata? onBehalfOf = null
    )
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(exception);

        this.Ledger.ObserveCallbackFailure(
            new ModHealthCallbackFailureObservation(
                ModId: GetSafeModId(mod),
                ModName: GetSafeModName(mod),
                Phase: phase,
                Operation: operation,
                CallbackIdentity: callbackIdentity,
                ExceptionType: exception.GetType().FullName ?? exception.GetType().Name,
                OnBehalfOfModId: onBehalfOf is not null ? GetSafeModId(onBehalfOf) : null,
                ManagedThreadId: Environment.CurrentManagedThreadId
            )
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Register a discovered mod and retain its ledger key.</summary>
    private Registration RegisterMod(IModMetadata mod)
    {
        return new Registration(this.Ledger.RegisterMod(CreateObservation(mod)));
    }

    /// <summary>Create a privacy-safe copy of a mod's current metadata.</summary>
    private static ModHealthModObservation CreateObservation(IModMetadata mod, ModHealthUpdateStatus? explicitUpdateStatus = null)
    {
        bool hasValidManifest = HasValidManifest(mod);
        IManifest? manifest = hasValidManifest ? mod.Manifest : null;
        IReadOnlyList<string> dependencies = hasValidManifest
            ? GetDependencyIds(mod)
            : Array.Empty<string>();

        ModEntryModel? updateData = mod.UpdateCheckData;
        string? suggestedVersion = updateData?.SuggestedUpdate?.Version?.ToString();
        ModHealthUpdateStatus updateStatus = updateData switch
        {
            null => explicitUpdateStatus ?? ModHealthUpdateStatus.Unknown,
            { SuggestedUpdate: not null } => ModHealthUpdateStatus.UpdateAvailable,
            { Errors.Length: > 0 } => ModHealthUpdateStatus.Unavailable,
            _ => ModHealthUpdateStatus.UpToDate
        };

        return new ModHealthModObservation(
            HasValidManifest: hasValidManifest,
            UniqueId: manifest?.UniqueID,
            DisplayName: manifest?.Name,
            Version: manifest?.Version?.ToString(),
            Kind: manifest?.ContentPackFor is not null
                ? ModHealthLedgerModKind.ContentPack
                : !string.IsNullOrWhiteSpace(manifest?.EntryDll)
                    ? ModHealthLedgerModKind.CodeMod
                    : ModHealthLedgerModKind.Unknown,
            ParentId: manifest?.ContentPackFor?.UniqueID,
            DependencyIds: dependencies,
            Status: GetStatusCore(mod),
            FailureReason: GetFailureReason(mod.FailReason),
            WarningFlags: (ulong)mod.Warnings,
            UpdateStatus: updateStatus,
            SuggestedUpdateVersion: suggestedVersion
        );
    }

    /// <summary>Get dependency IDs without allowing malformed manifest data to affect loading.</summary>
    private static IReadOnlyList<string> GetDependencyIds(IModMetadata mod)
    {
        try
        {
            return mod.GetRequiredModIds(includeOptional: true)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Get whether the metadata has a manifest identity safe to retain.</summary>
    private static bool HasValidManifest(IModMetadata mod)
    {
        return
            mod.HasId()
            && mod.FailReason is not (ModFailReason.EmptyFolder or ModFailReason.InvalidManifest or ModFailReason.XnbMod);
    }

    /// <summary>Map current loader state to the report contract.</summary>
    private static ModHealthLedgerModStatus GetStatusCore(IModMetadata mod)
    {
        if (mod.IsIgnored || mod.FailReason == ModFailReason.DisabledByDotConvention)
            return ModHealthLedgerModStatus.Ignored;
        if (mod.FailReason is ModFailReason.EmptyFolder or ModFailReason.InvalidManifest or ModFailReason.XnbMod)
            return ModHealthLedgerModStatus.Invalid;
        if (mod.Status == ModMetadataStatus.Failed)
        {
            return mod.FailReason == ModFailReason.LoadFailed
                ? ModHealthLedgerModStatus.Failed
                : ModHealthLedgerModStatus.Skipped;
        }
        if (mod.Mod is not null || mod.ContentPack is not null)
            return ModHealthLedgerModStatus.Loaded;
        return ModHealthLedgerModStatus.Discovered;
    }

    /// <summary>Map a loader failure reason to the report contract.</summary>
    private static ModHealthModFailureReason GetFailureReason(ModFailReason? reason)
    {
        return reason switch
        {
            null => ModHealthModFailureReason.None,
            ModFailReason.DisabledByDotConvention => ModHealthModFailureReason.DisabledByConvention,
            ModFailReason.Duplicate => ModHealthModFailureReason.Duplicate,
            ModFailReason.EmptyFolder => ModHealthModFailureReason.EmptyFolder,
            ModFailReason.Incompatible => ModHealthModFailureReason.Incompatible,
            ModFailReason.InvalidManifest => ModHealthModFailureReason.InvalidManifest,
            ModFailReason.LoadFailed => ModHealthModFailureReason.LoadFailed,
            ModFailReason.Malicious => ModHealthModFailureReason.Malicious,
            ModFailReason.MissingDependencies => ModHealthModFailureReason.MissingDependencies,
            ModFailReason.Obsolete => ModHealthModFailureReason.Obsolete,
            ModFailReason.XnbMod => ModHealthModFailureReason.XnbMod,
            _ => ModHealthModFailureReason.Unknown
        };
    }

    /// <summary>Get a safe mod ID for callback attribution.</summary>
    private static string? GetSafeModId(IModMetadata mod)
    {
        return HasValidManifest(mod) ? mod.Manifest.UniqueID : null;
    }

    /// <summary>Get a safe mod name for callback attribution.</summary>
    private static string? GetSafeModName(IModMetadata mod)
    {
        return HasValidManifest(mod) ? mod.Manifest.Name : null;
    }

    /// <summary>A discovered mod's opaque ledger registration.</summary>
    private sealed class Registration(ModHealthModKey key)
    {
        public ModHealthModKey Key { get; } = key;
        public object SyncRoot { get; } = new();
        public ModHealthUpdateStatus? UpdateStatus { get; set; }
    }
}
