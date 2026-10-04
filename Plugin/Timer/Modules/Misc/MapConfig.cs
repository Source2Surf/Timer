using System.Runtime.InteropServices;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;
using Source2Surf.Timer.Extensions;

namespace Source2Surf.Timer.Modules;

internal unsafe partial class MiscModule
{
    private static byte*    _emptyCommand;
    private static Register _mapConfigRegister;

    // CCSGameRules' constructor formats "exec maps/%s.cfg\n" for ServerCommand; swap the format for "\n".
    private void BlockMapConfigs()
    {
        var server = _bridge.Modules.Server;
        var format = server.FindStringExact("exec maps/%s.cfg\n");

        if (format == nint.Zero || server.GetReferencesFromPointer(format) is not [var reference])
        {
            _logger.LogWarning("Failed to find the map config exec, maps can run their own cfg");

            return;
        }

        var decoder = Decoder.Create(64, new UnsafeCodeReader(reference, 16), (ulong) reference, DecoderOptions.AMD);
        var lea     = decoder.Decode();

        if (lea.Code != Code.Lea_r64_m || (nint) lea.IPRelativeMemoryAddress != format || !lea.Op0Register.IsGPR64())
        {
            _logger.LogWarning("Unexpected map config exec at 0x{Address:X} ({Code}), maps can run their own cfg", reference, lea.Code);

            return;
        }

        _mapConfigRegister = lea.Op0Register;

        _emptyCommand    = (byte*) NativeMemory.Alloc(2);
        _emptyCommand[0] = (byte) '\n';
        _emptyCommand[1] = 0;

        // After the lea, so the register already holds the format.
        if (!_inlineHookManager.AddMidFuncHook(reference + lea.Length, (nint) (delegate* unmanaged<MidHookContext*, void>) &OnMapConfigExec))
        {
            _logger.LogWarning("Failed to hook the map config exec, maps can run their own cfg");
        }
    }

    private static void FreeEmptyCommand()
    {
        NativeMemory.Free(_emptyCommand);
        _emptyCommand = null;
    }

    [UnmanagedCallersOnly]
    private static void OnMapConfigExec(MidHookContext* context)
        => context->GetGpr(_mapConfigRegister) = (nint) _emptyCommand;
}
