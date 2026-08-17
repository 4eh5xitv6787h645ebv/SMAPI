using System.Collections.Generic;
using FluentAssertions;
using Netcode;
using NUnit.Framework;
using StardewModdingAPI.Enums;
using StardewModdingAPI.Framework.StateTracking;
using StardewModdingAPI.Framework.StateTracking.FieldWatchers;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for event-driven value and player-skill change tracking.</summary>
[TestFixture]
internal class NetValueWatcherTests
{
    [Test(Description = "Assert that a net-value watcher pushes one dirty notification per reset window.")]
    public void FieldChange_NotifiesOnceUntilReset()
    {
        NetInt field = new(1);
        int notifications = 0;
        using NetValueWatcher<int, NetInt> watcher = new("field", field, () => notifications++);

        field.Value = 2;
        field.Value = 3;

        notifications.Should().Be(1);
        watcher.IsChanged.Should().BeTrue();
        watcher.PreviousValue.Should().Be(1);
        watcher.CurrentValue.Should().Be(3);

        watcher.Reset();
        field.Value = 4;

        notifications.Should().Be(2);
        watcher.IsChanged.Should().BeTrue();
        watcher.PreviousValue.Should().Be(3);
        watcher.CurrentValue.Should().Be(4);
    }

    [Test(Description = "Assert that dirty skills are deduplicated in the established LevelChanged event order.")]
    public void AddChangedSkill_PreservesEventOrder()
    {
        List<SkillType> skills = [];

        PlayerTracker.AddChangedSkill(skills, SkillType.Mining);
        PlayerTracker.AddChangedSkill(skills, SkillType.Fishing);
        PlayerTracker.AddChangedSkill(skills, SkillType.Luck);
        PlayerTracker.AddChangedSkill(skills, SkillType.Combat);
        PlayerTracker.AddChangedSkill(skills, SkillType.Fishing);
        PlayerTracker.AddChangedSkill(skills, SkillType.Foraging);
        PlayerTracker.AddChangedSkill(skills, SkillType.Farming);

        skills.Should().Equal(
            SkillType.Farming,
            SkillType.Fishing,
            SkillType.Foraging,
            SkillType.Mining,
            SkillType.Combat,
            SkillType.Luck
        );
    }
}
