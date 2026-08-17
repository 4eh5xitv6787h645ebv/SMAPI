using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;

namespace StardewModdingAPI.Framework.Input;

/// <summary>Manages mouse state.</summary>
internal class MouseStateBuilder : IInputStateBuilder<MouseStateBuilder, MouseState>
{
    /*********
    ** Fields
    *********/
    /// <summary>The underlying mouse state.</summary>
    /// <remarks>This value is null if it needs to be regenerated for overrides. Most code should call <see cref="GetState"/> instead.</remarks>
    private MouseState? State;

    /// <summary>The pressed buttons.</summary>
    private readonly HashSet<SButton> PressedButtons = [];

    /// <summary>Whether <see cref="PressedButtons"/> has been initialized from the current state for overrides.</summary>
    private bool ArePressedButtonsInitialized;

    /// <summary>The mouse wheel scroll value.</summary>
    private int ScrollWheelValue;


    /*********
    ** Accessors
    *********/
    /// <summary>The X cursor position.</summary>
    public int X { get; private set; }

    /// <summary>The Y cursor position.</summary>
    public int Y { get; private set; }


    /*********
    ** Public methods
    *********/
    /// <inheritdoc />
    public void Reset(MouseState state)
    {
        this.State = state;
        this.ArePressedButtonsInitialized = false;

        this.X = state.X;
        this.Y = state.Y;
        this.ScrollWheelValue = state.ScrollWheelValue;

    }

    /// <summary>Override the state for a button.</summary>
    /// <param name="button">The button to override.</param>
    /// <param name="state">The new state to set.</param>
    public void OverrideButton(SButton button, SButtonState state)
    {
        this.EnsurePressedButtons();
        bool changed = state.IsDown()
            ? this.PressedButtons.Add(button)
            : this.PressedButtons.Remove(button);

        if (changed)
            this.State = null;
    }

    /// <inheritdoc />
    public void FillPressedButtons(HashSet<SButton> set)
    {
        if (this.ArePressedButtonsInitialized)
        {
            foreach (SButton button in this.PressedButtons)
                set.Add(button);
            return;
        }

        MouseState state = this.State.GetValueOrDefault();
        MouseStateBuilder.AddIfPressed(set, SButton.MouseLeft, state.LeftButton);
        MouseStateBuilder.AddIfPressed(set, SButton.MouseMiddle, state.MiddleButton);
        MouseStateBuilder.AddIfPressed(set, SButton.MouseRight, state.RightButton);
        MouseStateBuilder.AddIfPressed(set, SButton.MouseX1, state.XButton1);
        MouseStateBuilder.AddIfPressed(set, SButton.MouseX2, state.XButton2);
    }

    /// <inheritdoc />
    public MouseState GetState()
    {
        return this.State ??= new MouseState(
            x: this.X,
            y: this.Y,
            scrollWheel: this.ScrollWheelValue,
            leftButton: GetButtonState(this.PressedButtons, SButton.MouseLeft),
            middleButton: GetButtonState(this.PressedButtons, SButton.MouseMiddle),
            rightButton: GetButtonState(this.PressedButtons, SButton.MouseRight),
            xButton1: GetButtonState(this.PressedButtons, SButton.MouseX1),
            xButton2: GetButtonState(this.PressedButtons, SButton.MouseX2)
        );

        static ButtonState GetButtonState(HashSet<SButton> pressed, SButton button)
        {
            return pressed.Contains(button)
                ? ButtonState.Pressed
                : ButtonState.Released;
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Initialize the mutable pressed-button set from the current state.</summary>
    private void EnsurePressedButtons()
    {
        if (this.ArePressedButtonsInitialized)
            return;

        MouseState state = this.State.GetValueOrDefault();
        this.PressedButtons.Clear();
        MouseStateBuilder.AddIfPressed(this.PressedButtons, SButton.MouseLeft, state.LeftButton);
        MouseStateBuilder.AddIfPressed(this.PressedButtons, SButton.MouseMiddle, state.MiddleButton);
        MouseStateBuilder.AddIfPressed(this.PressedButtons, SButton.MouseRight, state.RightButton);
        MouseStateBuilder.AddIfPressed(this.PressedButtons, SButton.MouseX1, state.XButton1);
        MouseStateBuilder.AddIfPressed(this.PressedButtons, SButton.MouseX2, state.XButton2);
        this.ArePressedButtonsInitialized = true;
    }

    /// <summary>Add a mouse button to a set if it's pressed.</summary>
    private static void AddIfPressed(HashSet<SButton> pressed, SButton button, ButtonState state)
    {
        if (state == ButtonState.Pressed)
            pressed.Add(button);
    }
}
