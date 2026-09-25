using System.Globalization;
using System.Reflection;
using System.Text.Json;
using TradePet.App.Runtime;
using TradePet.App.ViewModels;
using TradePet.Core.Protocol;
using TradePet.Infrastructure.Mt5;
using Xunit;

namespace TradePet.App.Tests;

public sealed class SetupSettingsTests
{
    [Fact]
    public void LegacyDesktopSettings_RequestSetupAndKeepMt5Default()
    {
        var type = typeof(TradePetRuntime).GetNestedType("DesktopSettings", BindingFlags.NonPublic)!;
        var oldJson = """{"focusMode":false,"topmost":true,"positionLocked":false,"mouseThrough":false,"opacity":1,"scale":0.7,"lossZoneTolerance":200,"terminalPath":"C:\\Broker\\terminal64.exe"}""";
        var settings = JsonSerializer.Deserialize(oldJson, type, ProtocolJson.Options)!;
        Assert.Equal(0, type.GetProperty("SetupVersion")!.GetValue(settings));
        Assert.Equal(TradingPlatform.Mt5, type.GetProperty("Platform")!.GetValue(settings));
        var json = JsonSerializer.SerializeToNode(settings, type, ProtocolJson.Options)!;
        json["platform"] = "mt4";
        json["setupVersion"] = 1;
        json["terminalPath"] = @"D:\MT4\terminal.exe";
        var restored = JsonSerializer.Deserialize(json.ToJsonString(), type, ProtocolJson.Options)!;
        Assert.Equal(1, type.GetProperty("SetupVersion")!.GetValue(restored));
        Assert.Equal(TradingPlatform.Mt4, type.GetProperty("Platform")!.GetValue(restored));
        Assert.Equal(@"D:\MT4\terminal.exe", type.GetProperty("TerminalPath")!.GetValue(restored));
    }

    [Fact]
    public void PlatformChange_DropsIncompatibleTerminalWithoutClaimingSetupIsComplete()
    {
        var vm = new MainViewModel { SelectedTerminalPath = @"C:\MT5\terminal64.exe" };
        vm.SelectedPlatform = TradingPlatform.Mt4;
        Assert.Null(vm.SelectedTerminalPath);
        Assert.True(vm.NeedsSetup);
        var converter = new HistoryMetricConverter();
        Assert.Equal("—", converter.Convert([false, 0m], typeof(string), "0.##", CultureInfo.InvariantCulture));
        Assert.Equal("0", converter.Convert([true, 0m], typeof(string), "0.##", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void MonitoringSession_CanBecomeLiveWithoutClaimingDealHistoryWasImported()
    {
        var session = new TradePet.Core.Session.WorkerSession();
        session.Connect("MT4:Broker|42");
        session.MarkSnapshotReceived(requiresDealHistory: false);
        Assert.Equal(TradePet.Core.Session.WorkerSessionPhase.Live, session.Phase);
        Assert.False(session.HasInitialDeals);
        session.Disconnect();
        session.MarkSnapshotReceived(requiresDealHistory: false);
        Assert.Equal(TradePet.Core.Session.WorkerSessionPhase.Disconnected, session.Phase);
    }
}
