namespace PokeTokenBar.Core;

/// <summary>
/// Display and energy-saver state from Windows power-setting notifications. The Windows counterpart of
/// <c>screensDidSleep</c>/<c>screensDidWake</c> and <c>isLowPowerModeEnabled</c>: always-on animation
/// stops while the display is off and slows down in energy saver (defect-log "에너지").
/// Pure state; the app feeds it <c>WM_POWERBROADCAST</c> values.
/// </summary>
public sealed class PowerState
{
    public static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    /// <summary>Battery saver (Windows 10, Windows 11 before 24H2). Kept as a fallback for older builds.</summary>
    public static readonly Guid PowerSavingStatus = new("e00958c0-c213-4ace-ac77-fecced2eeea5");
    /// <summary>Energy saver (Windows 11 24H2+): 0 off, 1 standard, 2 high savings.</summary>
    public static readonly Guid EnergySaverStatus = new("550e8400-e29b-41d4-a716-446655440000");

    public static readonly IReadOnlyList<Guid> Settings = [ConsoleDisplayState, PowerSavingStatus, EnergySaverStatus];

    private bool _batterySaver;
    private bool _energySaver;
    private uint _displayOffTick;

    public bool DisplayOn { get; private set; } = true;
    public bool LowPower => _batterySaver || _energySaver;

    /// <summary>Applies one notification. <paramref name="nowTick"/> is <c>Environment.TickCount</c>.
    /// Returns true when <see cref="DisplayOn"/> or <see cref="LowPower"/> changed.</summary>
    public bool Apply(Guid setting, int value, uint nowTick)
    {
        var (display, low) = (DisplayOn, LowPower);
        // Display state: 0 off, 1 on, 2 dimmed. Dimmed is still visible, so it counts as on.
        if (setting == ConsoleDisplayState)
        {
            if (DisplayOn && value == 0) _displayOffTick = nowTick;
            DisplayOn = value != 0;
        }
        else if (setting == PowerSavingStatus) _batterySaver = value != 0;
        else if (setting == EnergySaverStatus) _energySaver = value != 0;
        return display != DisplayOn || low != LowPower;
    }

    /// <summary>
    /// Recovery when the display-on notification never arrives (#350: a stop state must not depend on its
    /// paired event alone). User input after the display went off means the display is on again.
    /// <paramref name="lastInputTick"/> is <c>GetLastInputInfo</c>'s tick. Returns true when it turned the display on.
    /// </summary>
    public bool RecoverFromInput(uint lastInputTick)
    {
        // Tick counts wrap after 49.7 days; the signed difference stays correct across the wrap.
        if (DisplayOn || unchecked((int)(lastInputTick - _displayOffTick)) <= 0) return false;
        DisplayOn = true;
        return true;
    }
}
