using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Input;

namespace StardewModdingAPI.Framework.Input;

/// <summary>Manages keyboard state.</summary>
internal class KeyboardStateBuilder : IInputStateBuilder<KeyboardStateBuilder, KeyboardState>
{
    /*********
    ** Fields
    *********/
    /// <summary>The underlying keyboard state.</summary>
    /// <remarks>This value is null if it needs to be regenerated for overrides. Most code should call <see cref="GetState"/> instead.</remarks>
    private KeyboardState? State;

    /// <summary>The pressed buttons.</summary>
    private readonly HashSet<Keys> PressedButtons = [];

    /// <summary>Whether <see cref="PressedButtons"/> has been initialized from the current state for overrides.</summary>
    private bool ArePressedButtonsInitialized;

    /// <summary>A reusable buffer for reading the pressed keys from MonoGame.</summary>
    private Keys[] PressedKeyBuffer = new Keys[8];

    /// <summary>The number of pressed keys in <see cref="PressedKeyBuffer"/>.</summary>
    private int PressedKeyCount;


    /*********
    ** Public methods
    *********/
    /// <inheritdoc />
    public void Reset(KeyboardState state)
    {
        this.State = state;
        this.ArePressedButtonsInitialized = false;

        // Retain the caller-owned buffer snapshot for the normal path. The mutable hash set is only
        // materialized if a mod actually overrides a key before the game handles input.
        this.PressedKeyCount = state.GetPressedKeyCount();
        if (this.PressedKeyCount > 0)
        {
            if (this.PressedKeyCount > this.PressedKeyBuffer.Length)
                Array.Resize(ref this.PressedKeyBuffer, Math.Max(this.PressedKeyCount, this.PressedKeyBuffer.Length * 2));

            state.GetPressedKeys(this.PressedKeyBuffer);
        }
    }

    /// <summary>Override the state for a key.</summary>
    /// <param name="key">The key to override.</param>
    /// <param name="state">The new state to set.</param>
    public void OverrideButton(Keys key, SButtonState state)
    {
        this.EnsurePressedButtons();
        bool changed = state.IsDown()
            ? this.PressedButtons.Add(key)
            : this.PressedButtons.Remove(key);

        if (changed)
            this.State = null;
    }

    /// <inheritdoc />
    public void FillPressedButtons(HashSet<SButton> set)
    {
        if (this.ArePressedButtonsInitialized)
        {
            foreach (Keys key in this.PressedButtons)
                set.Add(key.ToSButton());
        }
        else
        {
            for (int i = 0; i < this.PressedKeyCount; i++)
                set.Add(this.PressedKeyBuffer[i].ToSButton());
        }
    }

    /// <inheritdoc />
    public KeyboardState GetState()
    {
        return this.State ??= new KeyboardState(this.PressedButtons.ToArray());
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Initialize the mutable pressed-key set from the current state.</summary>
    private void EnsurePressedButtons()
    {
        if (this.ArePressedButtonsInitialized)
            return;

        this.PressedButtons.Clear();
        for (int i = 0; i < this.PressedKeyCount; i++)
            this.PressedButtons.Add(this.PressedKeyBuffer[i]);
        this.ArePressedButtonsInitialized = true;
    }
}
