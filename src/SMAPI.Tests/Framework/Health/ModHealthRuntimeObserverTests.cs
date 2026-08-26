using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.ModHelpers;
using StardewModdingAPI.Framework.ModLoading;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Toolkit;
using StardewModdingAPI.Toolkit.Framework.BundledModData;
using StardewModdingAPI.Toolkit.Framework.Clients.WebApi;
using StardewModdingAPI.Toolkit.Framework.ModBlacklistData;
using StardewModdingAPI.Toolkit.Serialization.Models;
using StardewValley;
using SemanticVersion = StardewModdingAPI.SemanticVersion;

namespace SMAPI.Tests.Framework.Health;

/// <summary>Unit tests for <see cref="ModHealthRuntimeObserver"/> and its authoritative metadata hook.</summary>
[TestFixture]
internal sealed class ModHealthRuntimeObserverTests
{
    [Test]
    public void Resolver_ObservesInvalidFolderBeforeCallerFiltersIt()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), $"smapi-health-{Guid.NewGuid():N}");
        string privateFolderName = "private-player-folder";
        Directory.CreateDirectory(Path.Combine(rootPath, privateFolderName));

        try
        {
            ModHealthLedger ledger = new();
            ModHealthRuntimeObserver observer = new(ledger);

            IModMetadata[] mods = new ModResolver(observer)
                .ReadManifests(new ModToolkit(), rootPath, new ModBlacklist(), new ModDatabase(), useCaseInsensitiveFilePaths: true)
                .ToArray();

            mods.Should().ContainSingle().Which.Status.Should().Be(ModMetadataStatus.Failed);
            ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();
            snapshot.TotalDiscoveredMods.Should().Be(1);
            ModHealthModSnapshot retained = snapshot.Mods.Should().ContainSingle().Subject;
            retained.Status.Should().Be(ModHealthLedgerModStatus.Invalid);
            retained.UsesGeneratedInvalidIdentity.Should().BeTrue();
            retained.DisplayName.Should().NotContain(privateFolderName);
            retained.UniqueId.Should().NotContain(privateFolderName);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Test]
    public void MetadataMutations_UpdateInventoryWithAllowlistedValues()
    {
        ModHealthLedger ledger = new();
        ModHealthRuntimeObserver observer = new(ledger);
        Manifest manifest = new(
            uniqueId: "Example.Mod",
            name: "Example Mod",
            author: "private author",
            description: "private description",
            version: new SemanticVersion("1.2.3"),
            minimumApiVersion: null,
            minimumGameVersion: null,
            entryDll: "Example.dll",
            contentPackFor: null,
            dependencies: [new ManifestDependency("Required.Mod", null as ISemanticVersion)],
            updateKeys: ["Nexus:secret"]
        );
        ModMetadata metadata = new(
            displayName: "filesystem fallback must not win",
            directoryPath: "/private/player/mods/Example",
            rootPath: "/private/player/mods",
            manifest: manifest,
            dataRecord: null,
            isIgnored: false,
            healthObserver: observer
        );

        metadata.SetWarning(ModWarning.PatchesGame);
        observer.ObserveUpdateStatus(metadata, ModHealthUpdateStatus.Pending);
        ledger.GetSnapshot().Mods.Should().ContainSingle().Which.UpdateStatus.Should().Be(ModHealthUpdateStatus.Pending);
        metadata.SetUpdateData(new ModEntryModel("Example.Mod")
        {
            SuggestedUpdate = new ModEntryVersionModel(new SemanticVersion("2.0.0"), "https://private.example/player")
        });
        Mock<IMod> modInstance = new();
        metadata.SetMod(modInstance.Object, new TranslationHelper(metadata, "en", LocalizedContentManager.LanguageCode.en));

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();
        ModHealthModSnapshot mod = snapshot.Mods.Should().ContainSingle().Subject;
        mod.UniqueId.Should().Be("Example.Mod");
        mod.DisplayName.Should().Be("Example Mod");
        mod.Version.Should().Be("1.2.3");
        mod.Kind.Should().Be(ModHealthLedgerModKind.CodeMod);
        mod.DependencyIds.Should().Equal("Required.Mod");
        mod.Status.Should().Be(ModHealthLedgerModStatus.Loaded);
        mod.WarningFlags.Should().Be((ulong)ModWarning.PatchesGame);
        mod.UpdateStatus.Should().Be(ModHealthUpdateStatus.UpdateAvailable);
        mod.SuggestedUpdateVersion.Should().Be("2.0.0");
        snapshot.ModStatusTotals[ModHealthLedgerModStatus.Loaded].Should().Be(1);
        snapshot.ModStatusTotals.Values.Sum().Should().Be(1);

        string retained = string.Join('|', mod.UniqueId, mod.DisplayName, mod.Version, mod.ParentId, mod.SuggestedUpdateVersion, string.Join('|', mod.DependencyIds));
        retained.ToLowerInvariant().Should().NotContain("private");
        retained.ToLowerInvariant().Should().NotContain("nexus");
        retained.ToLowerInvariant().Should().NotContain("https");
    }

    [Test]
    public void InvalidManifestAndCallbackFailure_DoNotRetainSensitiveDetails()
    {
        const string Secret = "Blossom-private-save-path";
        ModHealthLedger ledger = new();
        ModHealthRuntimeObserver observer = new(ledger);
        ModMetadata metadata = new(
            displayName: Secret,
            directoryPath: $"/home/player/{Secret}",
            rootPath: "/home/player",
            manifest: null,
            dataRecord: null,
            isIgnored: false,
            healthObserver: observer
        );

        metadata.SetStatus(ModMetadataStatus.Failed, ModFailReason.InvalidManifest, $"raw error {Secret}", $"stack {Secret}");
        observer.ObserveCallbackFailure(
            metadata,
            ModHealthExecutionPhase.Startup,
            ModHealthOperationKind.Entry,
            "Safe.Type.Entry",
            new InvalidOperationException($"raw exception {Secret}")
        );

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();
        ModHealthModSnapshot mod = snapshot.Mods.Should().ContainSingle().Subject;
        mod.Status.Should().Be(ModHealthLedgerModStatus.Invalid);
        mod.FailureReason.Should().Be(ModHealthModFailureReason.InvalidManifest);
        mod.UsesGeneratedInvalidIdentity.Should().BeTrue();
        ModHealthCallbackFailureSnapshot failure = snapshot.CallbackFailures.Should().ContainSingle().Subject;
        failure.CallbackIdentity.Should().Be("Safe.Type.Entry");
        failure.ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);

        string retained = string.Join('|', mod.UniqueId, mod.DisplayName, failure.ModId, failure.ModName, failure.CallbackIdentity, failure.ExceptionType);
        retained.Should().NotContain(Secret);
        snapshot.ModStatusTotals[ModHealthLedgerModStatus.Discovered].Should().Be(0);
        snapshot.ModStatusTotals[ModHealthLedgerModStatus.Invalid].Should().Be(1);
    }

    [Test]
    public void StatusTransitions_PreserveAggregateTotalsForSkippedAndFailed()
    {
        ModHealthLedger ledger = new();
        ModHealthRuntimeObserver observer = new(ledger);
        ModMetadata metadata = this.CreateMetadata(observer);

        metadata.SetStatus(ModMetadataStatus.Failed, ModFailReason.MissingDependencies, "legacy message");
        ledger.GetSnapshot().Mods.Should().ContainSingle().Which.Status.Should().Be(ModHealthLedgerModStatus.Skipped);

        metadata.SetStatusFound();
        metadata.SetStatus(ModMetadataStatus.Failed, ModFailReason.LoadFailed, "legacy message");

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();
        snapshot.ModStatusTotals[ModHealthLedgerModStatus.Discovered].Should().Be(0);
        snapshot.ModStatusTotals[ModHealthLedgerModStatus.Skipped].Should().Be(0);
        snapshot.ModStatusTotals[ModHealthLedgerModStatus.Failed].Should().Be(1);
        snapshot.ModStatusTotals.Values.Sum().Should().Be(1);
        snapshot.Mods.Should().ContainSingle().Which.FailureReason.Should().Be(ModHealthModFailureReason.LoadFailed);
    }

    [Test]
    public void PerModApiFailure_RecordsTargetCallbackWithoutExceptionMessage()
    {
        ModHealthLedger ledger = new();
        ModHealthRuntimeObserver observer = new(ledger);
        Mock<IModMetadata> requestingMod = new();
        Mock<IManifest> manifest = new();
        manifest.SetupGet(instance => instance.UniqueID).Returns("Target.Mod");
        manifest.SetupGet(instance => instance.Name).Returns("Target Mod");
        Mock<IMod> targetInstance = new();
        targetInstance.Setup(instance => instance.GetApi(requestingMod.Object)).Throws(new InvalidOperationException("private API detail"));
        Mock<IModMetadata> targetMod = new();
        targetMod.Setup(instance => instance.HasId()).Returns(true);
        targetMod.Setup(instance => instance.HasManifest()).Returns(true);
        targetMod.SetupGet(instance => instance.Manifest).Returns(manifest.Object);
        targetMod.SetupGet(instance => instance.Mod).Returns(targetInstance.Object);
        ModRegistry registry = new() { AreAllModsInitialized = true };
        registry.Add(targetMod.Object);
        Mock<IMonitor> monitor = new();
        ModRegistryHelper helper = new(requestingMod.Object, registry, Mock.Of<IInterfaceProxyFactory>(), monitor.Object, observer);

        helper.GetApi("Target.Mod").Should().BeNull();

        ModHealthCallbackFailureSnapshot failure = ledger.GetSnapshot().CallbackFailures.Should().ContainSingle().Subject;
        failure.ModId.Should().Be("Target.Mod");
        failure.ModName.Should().Be("Target Mod");
        failure.Operation.Should().Be(ModHealthOperationKind.GetApi);
        failure.CallbackIdentity.Should().EndWith(".GetApi");
        failure.ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        failure.CallbackIdentity.Should().NotContain("private");
    }

    private ModMetadata CreateMetadata(ModHealthRuntimeObserver observer)
    {
        Manifest manifest = new("Example.Mod", "Example", "author", "description", new SemanticVersion("1.0.0"));
        return new ModMetadata("Example", "/mods/Example", "/mods", manifest, null, false, observer);
    }
}
