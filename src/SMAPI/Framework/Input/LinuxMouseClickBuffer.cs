using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Input;

namespace StardewModdingAPI.Framework.Input;

/// <summary>Buffers native SDL mouse click pulses which begin and end between two game input polls.</summary>
/// <remarks>
/// SDL can receive a mouse-button down and up event during one long game frame, while MonoGame's next
/// <see cref="MouseState"/> snapshot only exposes the final released state. This class watches the native event
/// stream and replays only those complete pulses which weren't represented by either adjacent snapshot.
/// </remarks>
internal sealed class LinuxMouseClickBuffer : IDisposable
{
    private const uint SdlMouseButtonDown = 0x401;
    private const uint SdlMouseButtonUp = 0x402;

    private static readonly SButton[] Buttons =
    [
        SButton.MouseLeft,
        SButton.MouseMiddle,
        SButton.MouseRight,
        SButton.MouseX1,
        SButton.MouseX2
    ];

    private readonly ButtonBuffer[] ButtonBuffers =
    [
        new(),
        new(),
        new(),
        new(),
        new()
    ];
    private readonly object SyncRoot = new();
    private readonly SdlEventFilter? EventFilter;
    private bool IsDisposed;

    /// <summary>Create a buffer without registering a native watcher, for deterministic tests.</summary>
    internal LinuxMouseClickBuffer() { }

    /// <summary>Create and register a native SDL watcher.</summary>
    private LinuxMouseClickBuffer(bool registerNativeWatcher)
    {
        if (!registerNativeWatcher)
            return;

        this.EventFilter = this.OnSdlEvent;
        SdlAddEventWatch(this.EventFilter, IntPtr.Zero);
    }

    /// <summary>Try to register the native click buffer, or disable it if the expected SDL interface isn't available.</summary>
    public static LinuxMouseClickBuffer? TryCreate()
    {
        try
        {
            return new LinuxMouseClickBuffer(registerNativeWatcher: true);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Apply any complete native click pulse which wasn't visible in the adjacent polled states.</summary>
    /// <param name="mouse">The current polled mouse state.</param>
    public void Apply(MouseStateBuilder mouse)
    {
        MouseState actualState = mouse.GetState();
        lock (this.SyncRoot)
        {
            for (int i = 0; i < LinuxMouseClickBuffer.Buttons.Length; i++)
            {
                ButtonBuffer buffer = this.ButtonBuffers[i];
                SButton button = LinuxMouseClickBuffer.Buttons[i];
                bool isActuallyDown = LinuxMouseClickBuffer.IsDown(actualState, button);

                if (!buffer.WasDownAtLastPoll && !isActuallyDown && buffer.CompletedPulsesSincePoll > 0)
                    buffer.QueuedPulses += buffer.CompletedPulsesSincePoll;

                buffer.CompletedPulsesSincePoll = 0;
                buffer.SawDownSincePoll = false;
                buffer.WasDownAtLastPoll = isActuallyDown;

                if (buffer.NeedsSyntheticRelease)
                {
                    buffer.NeedsSyntheticRelease = false;
                    continue;
                }

                if (!isActuallyDown && buffer.QueuedPulses > 0)
                {
                    buffer.QueuedPulses--;
                    buffer.NeedsSyntheticRelease = true;
                    mouse.OverrideButton(button, SButtonState.Pressed);
                }
            }
        }
    }

    /// <summary>Record a native SDL mouse-button event.</summary>
    /// <param name="eventType">The SDL event type.</param>
    /// <param name="nativeButton">The one-based SDL mouse button number.</param>
    internal void RecordNativeEvent(uint eventType, byte nativeButton)
    {
        int index = nativeButton - 1;
        if (index < 0 || index >= this.ButtonBuffers.Length)
            return;

        lock (this.SyncRoot)
        {
            ButtonBuffer buffer = this.ButtonBuffers[index];
            switch (eventType)
            {
                case LinuxMouseClickBuffer.SdlMouseButtonDown:
                    buffer.SawDownSincePoll = true;
                    break;

                case LinuxMouseClickBuffer.SdlMouseButtonUp when buffer.SawDownSincePoll:
                    buffer.SawDownSincePoll = false;
                    buffer.CompletedPulsesSincePoll++;
                    break;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.IsDisposed)
            return;

        this.IsDisposed = true;
        if (this.EventFilter is not null)
            SdlDelEventWatch(this.EventFilter, IntPtr.Zero);
    }

    /// <summary>Receive an event from SDL without changing its delivery to MonoGame.</summary>
    private int OnSdlEvent(IntPtr userData, IntPtr eventPointer)
    {
        try
        {
            uint eventType = unchecked((uint)Marshal.ReadInt32(eventPointer));
            if (eventType is LinuxMouseClickBuffer.SdlMouseButtonDown or LinuxMouseClickBuffer.SdlMouseButtonUp)
                this.RecordNativeEvent(eventType, Marshal.ReadByte(eventPointer, 16));
        }
        catch
        {
            // Never let a managed exception cross the native callback boundary.
        }

        return 0;
    }

    /// <summary>Get whether a mouse button is down in a polled state.</summary>
    private static bool IsDown(MouseState state, SButton button)
    {
        ButtonState buttonState = button switch
        {
            SButton.MouseLeft => state.LeftButton,
            SButton.MouseMiddle => state.MiddleButton,
            SButton.MouseRight => state.RightButton,
            SButton.MouseX1 => state.XButton1,
            SButton.MouseX2 => state.XButton2,
            _ => ButtonState.Released
        };
        return buttonState == ButtonState.Pressed;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SdlEventFilter(IntPtr userData, IntPtr sdlEvent);

    [DllImport("libSDL2-2.0.so.0", EntryPoint = "SDL_AddEventWatch", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SdlAddEventWatch(SdlEventFilter filter, IntPtr userData);

    [DllImport("libSDL2-2.0.so.0", EntryPoint = "SDL_DelEventWatch", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SdlDelEventWatch(SdlEventFilter filter, IntPtr userData);

    /// <summary>Mutable edge state for one native mouse button.</summary>
    private sealed class ButtonBuffer
    {
        public bool WasDownAtLastPoll;
        public bool SawDownSincePoll;
        public int CompletedPulsesSincePoll;
        public int QueuedPulses;
        public bool NeedsSyntheticRelease;
    }
}
