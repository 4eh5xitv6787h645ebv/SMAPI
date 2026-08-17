using System.Collections.Generic;

namespace StardewModdingAPI.Framework.Content;

/// <summary>A set of operations to apply to an asset.</summary>
/// <param name="LoadOperations">The load operations to apply.</param>
/// <param name="EditOperations">The edit operations to apply.</param>
internal readonly record struct AssetOperationGroup(List<AssetLoadOperation>? LoadOperations, List<AssetEditOperation>? EditOperations);
