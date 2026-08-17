using System.Collections.Generic;
using System.Reflection;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Input;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for lazy mutable input-state builders.</summary>
[TestFixture]
internal class InputStateBuilderTests
{
    [Test(Description = "Assert that keyboard state stays immutable until an override and retains equivalent output afterward.")]
    public void Keyboard_LazilyAppliesOverrides()
    {
        KeyboardStateBuilder builder = new();
        builder.Reset(new KeyboardState(Keys.W, Keys.LeftShift));

        HashSet<SButton> pressed = [];
        builder.FillPressedButtons(pressed);
        pressed.Should().BeEquivalentTo([Keys.W.ToSButton(), Keys.LeftShift.ToSButton()]);
        IsPressedSetInitialized(builder).Should().BeFalse();

        builder.OverrideButton(Keys.W, SButtonState.Released);
        builder.OverrideButton(Keys.A, SButtonState.Pressed);
        IsPressedSetInitialized(builder).Should().BeTrue();

        pressed.Clear();
        builder.FillPressedButtons(pressed);
        pressed.Should().BeEquivalentTo([Keys.A.ToSButton(), Keys.LeftShift.ToSButton()]);
        KeyboardState result = builder.GetState();
        result.IsKeyDown(Keys.A).Should().BeTrue();
        result.IsKeyDown(Keys.LeftShift).Should().BeTrue();
        result.IsKeyUp(Keys.W).Should().BeTrue();

        builder.Reset(new KeyboardState(Keys.D));
        pressed.Clear();
        builder.FillPressedButtons(pressed);
        pressed.Should().BeEquivalentTo([Keys.D.ToSButton()]);
        IsPressedSetInitialized(builder).Should().BeFalse();
    }

    [Test(Description = "Assert that mouse state stays immutable until an override and retains coordinates and buttons afterward.")]
    public void Mouse_LazilyAppliesOverrides()
    {
        MouseStateBuilder builder = new();
        builder.Reset(new MouseState(
            x: 10,
            y: 20,
            scrollWheel: 120,
            leftButton: ButtonState.Pressed,
            middleButton: ButtonState.Released,
            rightButton: ButtonState.Released,
            xButton1: ButtonState.Pressed,
            xButton2: ButtonState.Released
        ));

        HashSet<SButton> pressed = [];
        builder.FillPressedButtons(pressed);
        pressed.Should().BeEquivalentTo([SButton.MouseLeft, SButton.MouseX1]);
        IsPressedSetInitialized(builder).Should().BeFalse();

        builder.OverrideButton(SButton.MouseLeft, SButtonState.Released);
        builder.OverrideButton(SButton.MouseRight, SButtonState.Pressed);
        IsPressedSetInitialized(builder).Should().BeTrue();

        pressed.Clear();
        builder.FillPressedButtons(pressed);
        pressed.Should().BeEquivalentTo([SButton.MouseRight, SButton.MouseX1]);
        MouseState result = builder.GetState();
        result.X.Should().Be(10);
        result.Y.Should().Be(20);
        result.ScrollWheelValue.Should().Be(120);
        result.LeftButton.Should().Be(ButtonState.Released);
        result.RightButton.Should().Be(ButtonState.Pressed);
        result.XButton1.Should().Be(ButtonState.Pressed);

        builder.Reset(new MouseState(0, 0, 0, ButtonState.Released, ButtonState.Pressed, ButtonState.Released, ButtonState.Released, ButtonState.Released));
        pressed.Clear();
        builder.FillPressedButtons(pressed);
        pressed.Should().BeEquivalentTo([SButton.MouseMiddle]);
        IsPressedSetInitialized(builder).Should().BeFalse();
    }

    [Test(Description = "Assert that controller state stays immutable until an override and retains digital and analog input afterward.")]
    public void GamePad_LazilyAppliesOverrides()
    {
        GamePadStateBuilder builder = new();
        builder.Reset(new GamePadState(
            leftThumbStick: new Vector2(1, 0),
            rightThumbStick: Vector2.Zero,
            leftTrigger: 0.5f,
            rightTrigger: 0,
            buttons: [Buttons.A, Buttons.DPadUp]
        ));

        HashSet<SButton> pressed = [];
        builder.FillPressedButtons(pressed);
        pressed.Should().BeEquivalentTo([SButton.ControllerA, SButton.DPadUp, SButton.LeftTrigger, SButton.LeftThumbstickRight]);
        IsPressedSetInitialized(builder).Should().BeFalse();

        builder.OverrideButton(Buttons.A, SButtonState.Released);
        builder.OverrideButton(Buttons.B, SButtonState.Pressed);
        builder.OverrideButton(Buttons.LeftThumbstickRight, SButtonState.Released);
        IsPressedSetInitialized(builder).Should().BeTrue();

        pressed.Clear();
        builder.FillPressedButtons(pressed);
        pressed.Should().BeEquivalentTo([SButton.ControllerB, SButton.DPadUp, SButton.LeftTrigger]);
        GamePadState result = builder.GetState();
        result.IsButtonUp(Buttons.A).Should().BeTrue();
        result.IsButtonDown(Buttons.B).Should().BeTrue();
        result.IsButtonDown(Buttons.DPadUp).Should().BeTrue();
        result.Triggers.Left.Should().Be(0.5f);
        result.ThumbSticks.Left.X.Should().Be(0);

        builder.Reset(new GamePadState(Vector2.Zero, Vector2.Zero, 0, 0, Buttons.X));
        pressed.Clear();
        builder.FillPressedButtons(pressed);
        pressed.Should().BeEquivalentTo([SButton.ControllerX]);
        IsPressedSetInitialized(builder).Should().BeFalse();
    }

    /// <summary>Get whether a builder materialized its mutable override set.</summary>
    private static bool IsPressedSetInitialized(object builder)
    {
        return (bool)builder
            .GetType()
            .GetField("ArePressedButtonsInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(builder)!;
    }
}
