using System;
using System.Collections.Generic;
using StardewModdingAPI.Framework.Input;

namespace StardewModdingAPI.Events;

/// <summary>Event arguments when any buttons were pressed or released.</summary>
public class ButtonsChangedEventArgs : EventArgs
{
    /*********
    ** Accessors
    *********/
    /// <summary>The current cursor position.</summary>
    public ICursorPosition Cursor { get; }

    /// <summary>The buttons which were pressed since the previous tick.</summary>
    public IEnumerable<SButton> Pressed { get; }

    /// <summary>The buttons which were held since the previous tick.</summary>
    public IEnumerable<SButton> Held { get; }

    /// <summary>The buttons which were released since the previous tick.</summary>
    public IEnumerable<SButton> Released { get; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="cursor">The cursor position.</param>
    /// <param name="inputState">The game's current input state.</param>
    internal ButtonsChangedEventArgs(ICursorPosition cursor, SInputState inputState)
    {
        Dictionary<SButton, SButtonState> buttonStates = inputState.GetActiveButtonStates();
        int pressedCount = 0;
        int heldCount = 0;
        int releasedCount = 0;

        foreach (SButtonState state in buttonStates.Values)
        {
            switch (state)
            {
                case SButtonState.Pressed:
                    pressedCount++;
                    break;

                case SButtonState.Held:
                    heldCount++;
                    break;

                case SButtonState.Released:
                    releasedCount++;
                    break;
            }
        }

        SButton[] pressed = pressedCount > 0 ? new SButton[pressedCount] : Array.Empty<SButton>();
        SButton[] held = heldCount > 0 ? new SButton[heldCount] : Array.Empty<SButton>();
        SButton[] released = releasedCount > 0 ? new SButton[releasedCount] : Array.Empty<SButton>();
        pressedCount = 0;
        heldCount = 0;
        releasedCount = 0;
        foreach ((SButton button, SButtonState state) in buttonStates)
        {
            switch (state)
            {
                case SButtonState.Pressed:
                    pressed[pressedCount++] = button;
                    break;

                case SButtonState.Held:
                    held[heldCount++] = button;
                    break;

                case SButtonState.Released:
                    released[releasedCount++] = button;
                    break;
            }
        }

        this.Cursor = cursor;
        this.Pressed = pressed;
        this.Held = held;
        this.Released = released;
    }
}
