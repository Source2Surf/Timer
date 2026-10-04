using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Managers.Movement;
using Source2Surf.Timer.Shared.Interfaces;
using Xunit;

namespace Timer.Tests;

public sealed class MovementExtensionTests : IDisposable
{
    private readonly nint _moveData = Marshal.AllocHGlobal(0x100);

    public MovementExtensionTests()
        => Marshal.WriteInt32(_moveData, 0xd4, 1234); // CMoveData's tick

    public void Dispose()
        => Marshal.FreeHGlobal(_moveData);

    [Fact]
    public void WithoutAnExtensionEachTickIsOneAcceleration()
    {
        var proxy = new MovementExtensionProxy(null!, NullLogger<MovementExtensionProxy>.Instance);

        Assert.Equal(1234, proxy.GetAccelerationStep(0, _moveData));
    }

    [Fact]
    public void UnloadingTheExtensionsModuleGoesBackToTicks()
    {
        var proxy = new MovementExtensionProxy(null!, NullLogger<MovementExtensionProxy>.Instance);
        proxy.Use(new Extension());

        proxy.OnModuleUnloading("Some.Other.Module");
        Assert.Equal(7, proxy.GetAccelerationStep(0, _moveData));

        proxy.OnModuleUnloading(typeof(Extension).Assembly.GetName().Name!);
        Assert.Equal(1234, proxy.GetAccelerationStep(0, _moveData));
    }

    private sealed class Extension : IMovementExtension
    {
        public long GetAccelerationStep(PlayerSlot slot, nint moveData)
            => 7;
    }
}
