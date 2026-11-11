using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

public class PowerStateTests
{
    [Fact]
    public void DisplayOffStopsAndOnResumes()
    {
        var s = new PowerState();
        Assert.True(s.DisplayOn);

        Assert.True(s.Apply(PowerState.ConsoleDisplayState, 0, 100));
        Assert.False(s.DisplayOn);
        Assert.True(s.Apply(PowerState.ConsoleDisplayState, 1, 200));
        Assert.True(s.DisplayOn);
    }

    [Fact]
    public void DimmedDisplayCountsAsOn()
    {
        var s = new PowerState();
        Assert.False(s.Apply(PowerState.ConsoleDisplayState, 2, 100));
        Assert.True(s.DisplayOn);
    }

    [Fact]
    public void RepeatedValueIsNotAChange()
    {
        var s = new PowerState();
        Assert.False(s.Apply(PowerState.ConsoleDisplayState, 1, 100));
        Assert.True(s.Apply(PowerState.EnergySaverStatus, 1, 100));
        Assert.False(s.Apply(PowerState.EnergySaverStatus, 2, 100));
    }

    [Fact]
    public void UnknownSettingIsIgnored()
    {
        var s = new PowerState();
        Assert.False(s.Apply(Guid.NewGuid(), 0, 100));
        Assert.True(s.DisplayOn);
        Assert.False(s.LowPower);
    }

    [Theory]
    [InlineData(1)]   // standard
    [InlineData(2)]   // high savings
    public void EnergySaverAloneIsLowPower(int level)
    {
        var s = new PowerState();
        s.Apply(PowerState.EnergySaverStatus, level, 0);
        Assert.True(s.LowPower);
    }

    [Fact]
    public void BatterySaverAloneIsLowPower()
    {
        var s = new PowerState();
        s.Apply(PowerState.PowerSavingStatus, 1, 0);
        Assert.True(s.LowPower);
    }

    [Fact]
    public void LowPowerEndsOnlyWhenBothSaversAreOff()
    {
        var s = new PowerState();
        s.Apply(PowerState.PowerSavingStatus, 1, 0);
        s.Apply(PowerState.EnergySaverStatus, 1, 0);

        Assert.False(s.Apply(PowerState.EnergySaverStatus, 0, 0));
        Assert.True(s.LowPower);
        Assert.True(s.Apply(PowerState.PowerSavingStatus, 0, 0));
        Assert.False(s.LowPower);
    }

    [Fact]
    public void InputAfterDisplayOffRecoversAMissedOnEvent()
    {
        var s = new PowerState();
        s.Apply(PowerState.ConsoleDisplayState, 0, 1000);

        Assert.False(s.RecoverFromInput(900));    // the input that preceded the display going off
        Assert.False(s.RecoverFromInput(1000));
        Assert.False(s.DisplayOn);
        Assert.True(s.RecoverFromInput(1001));
        Assert.True(s.DisplayOn);
    }

    [Fact]
    public void RecoveryHandlesTickWraparound()
    {
        var s = new PowerState();
        s.Apply(PowerState.ConsoleDisplayState, 0, uint.MaxValue - 10);

        Assert.True(s.RecoverFromInput(5));
    }

    [Fact]
    public void RecoveryKeepsTheFirstOffTick()
    {
        var s = new PowerState();
        s.Apply(PowerState.ConsoleDisplayState, 0, 1000);
        s.Apply(PowerState.ConsoleDisplayState, 0, 5000);   // repeated off must not move the off time

        Assert.True(s.RecoverFromInput(2000));
    }

    [Fact]
    public void RecoveryDoesNothingWhileTheDisplayIsOn()
    {
        var s = new PowerState();
        Assert.False(s.RecoverFromInput(1));
    }
}
