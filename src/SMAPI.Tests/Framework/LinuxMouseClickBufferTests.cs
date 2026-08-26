using Microsoft.Xna.Framework.Input;
using NUnit.Framework;
using StardewModdingAPI.Framework.Input;

namespace SMAPI.Tests.Framework;

/// <summary>Regression tests for buffering Linux mouse pulses which occur between game input polls.</summary>
[TestFixture]
internal class LinuxMouseClickBufferTests
{
    private const uint MouseButtonDown = 0x401;
    private const uint MouseButtonUp = 0x402;

    [Test]
    public void ReplaysCompletePulseMissedBetweenPolls()
    {
        using LinuxMouseClickBuffer buffer = new();
        MouseStateBuilder mouse = new();
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Released);

        buffer.RecordNativeEvent(LinuxMouseClickBufferTests.MouseButtonDown, nativeButton: 1);
        buffer.RecordNativeEvent(LinuxMouseClickBufferTests.MouseButtonUp, nativeButton: 1);

        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Pressed);
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Released);
    }

    [Test]
    public void DoesNotReplayPulseRepresentedByAdjacentPolls()
    {
        using LinuxMouseClickBuffer buffer = new();
        MouseStateBuilder mouse = new();
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Released);

        buffer.RecordNativeEvent(LinuxMouseClickBufferTests.MouseButtonDown, nativeButton: 1);
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Pressed).ShouldBe(ButtonState.Pressed);
        buffer.RecordNativeEvent(LinuxMouseClickBufferTests.MouseButtonUp, nativeButton: 1);
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Released);
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Released);
    }

    [Test]
    public void RetainsUnmatchedDownUntilNativeUpIsPumped()
    {
        using LinuxMouseClickBuffer buffer = new();
        MouseStateBuilder mouse = new();
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Released);

        buffer.RecordNativeEvent(LinuxMouseClickBufferTests.MouseButtonDown, nativeButton: 1);
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Released);
        buffer.RecordNativeEvent(LinuxMouseClickBufferTests.MouseButtonUp, nativeButton: 1);

        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Pressed);
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Released);
    }

    [Test]
    public void ReplaysPulseCompletedAfterNormallyObservedPress()
    {
        using LinuxMouseClickBuffer buffer = new();
        MouseStateBuilder mouse = new();
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Released);

        buffer.RecordNativeEvent(LinuxMouseClickBufferTests.MouseButtonDown, nativeButton: 1);
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Pressed).ShouldBe(ButtonState.Pressed);
        buffer.RecordNativeEvent(LinuxMouseClickBufferTests.MouseButtonUp, nativeButton: 1);
        buffer.RecordNativeEvent(LinuxMouseClickBufferTests.MouseButtonDown, nativeButton: 1);
        buffer.RecordNativeEvent(LinuxMouseClickBufferTests.MouseButtonUp, nativeButton: 1);

        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Released);
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Pressed);
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Released);
    }

    [Test]
    public void SeparatesMultipleMissedPulsesWithReleaseTicks()
    {
        using LinuxMouseClickBuffer buffer = new();
        MouseStateBuilder mouse = new();
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Released);

        for (int i = 0; i < 2; i++)
        {
            buffer.RecordNativeEvent(LinuxMouseClickBufferTests.MouseButtonDown, nativeButton: 1);
            buffer.RecordNativeEvent(LinuxMouseClickBufferTests.MouseButtonUp, nativeButton: 1);
        }

        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Pressed);
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Released);
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Pressed);
        LinuxMouseClickBufferTests.Apply(buffer, mouse, ButtonState.Released).ShouldBe(ButtonState.Released);
    }

    /// <summary>Apply one actual input snapshot and return the state exposed after buffering.</summary>
    private static ButtonState Apply(LinuxMouseClickBuffer buffer, MouseStateBuilder mouse, ButtonState actualState)
    {
        mouse.Reset(new MouseState(100, 200, 0, actualState, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released));
        buffer.Apply(mouse);
        return mouse.GetState().LeftButton;
    }
}

internal static class ButtonStateTestExtensions
{
    /// <summary>Assert a mouse button state without adding another assertion-library dependency to the helper.</summary>
    public static void ShouldBe(this ButtonState actual, ButtonState expected)
    {
        Assert.That(actual, Is.EqualTo(expected));
    }
}
