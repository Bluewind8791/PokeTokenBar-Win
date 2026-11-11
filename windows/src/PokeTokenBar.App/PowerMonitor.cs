using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Feeds <see cref="PowerState"/> from <c>WM_POWERBROADCAST</c> power-setting notifications on a hidden
/// window: display on/off (macOS <c>screensDidSleep</c>/<c>screensDidWake</c>) and energy saver (macOS Low
/// Power Mode). Windows sends each setting's current value at registration, so the state is right from launch.
/// </summary>
public sealed class PowerMonitor : IDisposable
{
    private const int WmPowerBroadcast = 0x0218, PbtPowerSettingChange = 0x8013;
    /// <summary>While the display is off, how often to check for input that means it is on again.</summary>
    private static readonly TimeSpan RecoveryProbeInterval = TimeSpan.FromMinutes(1);

    private readonly PowerState _state = new();
    private readonly HwndSource _window;
    private readonly List<IntPtr> _registrations = [];
    private readonly DispatcherTimer _recoveryProbe = new() { Interval = RecoveryProbeInterval };

    /// <summary>Raised on the UI thread when <see cref="DisplayOn"/> or <see cref="LowPower"/> changes.</summary>
    public event Action? Changed;

    public bool DisplayOn => _state.DisplayOn;
    public bool LowPower => _state.LowPower;

    public PowerMonitor()
    {
        // A hidden top-level window: message-only windows do not receive power broadcasts.
        _window = new HwndSource(new HwndSourceParameters("PokeTokenBar.Power") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);
        _recoveryProbe.Tick += (_, _) => ProbeInput();
        foreach (var setting in PowerState.Settings)
        {
            var guid = setting;
            var handle = RegisterPowerSettingNotification(_window.Handle, ref guid, DeviceNotifyWindowHandle);
            if (handle == IntPtr.Zero) AppLog.Write($"power: cannot register {setting} (error {Marshal.GetLastWin32Error()})");
            else _registrations.Add(handle);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmPowerBroadcast || wParam.ToInt32() != PbtPowerSettingChange || lParam == IntPtr.Zero) return IntPtr.Zero;
        // POWERBROADCAST_SETTING: GUID PowerSetting; DWORD DataLength; UCHAR Data[] — every setting here is a DWORD.
        var setting = Marshal.PtrToStructure<Guid>(lParam);
        if (Marshal.ReadInt32(lParam, 16) < 4) return IntPtr.Zero;
        var value = Marshal.ReadInt32(lParam, 20);
        AppLog.WriteIfChanged($"power:{setting}", $"power: {Name(setting)} = {value}", TimeSpan.MaxValue);
        if (_state.Apply(setting, value, unchecked((uint)Environment.TickCount))) OnChanged();
        return IntPtr.Zero;
    }

    private void ProbeInput()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info) || !_state.RecoverFromInput(info.Time)) return;
        AppLog.Write("power: display assumed on (input after display off, no display-on notification)");
        OnChanged();
    }

    private void OnChanged()
    {
        if (_state.DisplayOn) _recoveryProbe.Stop();
        else _recoveryProbe.Start();
        Changed?.Invoke();
    }

    private static string Name(Guid setting) =>
        setting == PowerState.ConsoleDisplayState ? "display"
        : setting == PowerState.PowerSavingStatus ? "battery saver"
        : setting == PowerState.EnergySaverStatus ? "energy saver"
        : setting.ToString();

    public void Dispose()
    {
        _recoveryProbe.Stop();
        foreach (var handle in _registrations) UnregisterPowerSettingNotification(handle);
        _registrations.Clear();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    private const int DeviceNotifyWindowHandle = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
