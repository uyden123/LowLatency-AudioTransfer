using System;
using System.Threading.Tasks;
using AudioTransfer.Core.Facade;
using AudioTransfer.Core.Models;
using AudioTransfer.GUI.ViewModels;
using AudioTransfer.GUI.ViewModels.States;
using NSubstitute;
using Xunit;

namespace AudioTransfer.Tests.ViewModels;

public class StatesTests
{
    private MainViewModel CreateViewModel(IServerEngine server, IPlayerEngine player)
    {
        var configRepo = Substitute.For<IConfigRepository<PlayerConfig>>();
        configRepo.LoadOrDefaultAsync().Returns(Task.FromResult(new PlayerConfig()));
        return new MainViewModel(server, player, configRepo);
    }

    [Fact]
    public void InitialState_IsDisconnected()
    {
        var server = Substitute.For<IServerEngine>();
        var player = Substitute.For<IPlayerEngine>();
        
        var vm = CreateViewModel(server, player);

        Assert.IsType<DisconnectedState>(vm.CurrentState);
    }

    [Fact]
    public async Task DisconnectedState_NoIp_NotifiesUser()
    {
        var server = Substitute.For<IServerEngine>();
        var player = Substitute.For<IPlayerEngine>();
        
        var vm = CreateViewModel(server, player);
        vm.TargetIp = "";
        
        bool notified = false;
        vm.RequestShowNotification += (s, e) => { notified = true; };

        await vm.CurrentState.HandleConnectToggleAsync(vm);

        Assert.True(notified);
        Assert.IsType<DisconnectedState>(vm.CurrentState);
    }

    [Fact]
    public async Task DisconnectedState_ValidIp_TransitionsToConnecting()
    {
        var server = Substitute.For<IServerEngine>();
        var player = Substitute.For<IPlayerEngine>();
        
        // Mock connection failure to quickly return from ConnectingState
        player.StartAndroidMicListenerAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>()).Returns(Task.FromResult(false));

        var vm = CreateViewModel(server, player);
        vm.TargetIp = "192.168.1.100";
        
        await vm.CurrentState.HandleConnectToggleAsync(vm);

        // Since start failed, it should quickly revert to Disconnected
        Assert.IsType<DisconnectedState>(vm.CurrentState);
        await player.Received(1).StartAndroidMicListenerAsync("192.168.1.100", 5003, Arg.Any<string>());
    }
}
