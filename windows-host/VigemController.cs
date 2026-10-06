using System.Runtime.InteropServices;

namespace RemoteGamePad.Host;

public sealed class VigemController : IGamepadSink
{
    private const uint Success = 0x20000000;
    private IntPtr _client;
    private IntPtr _target;
    private bool _connected;

    public VigemController()
    {
        _client = NativeMethods.VigemAlloc();
        if (_client == IntPtr.Zero)
        {
            throw new VigemException("ViGEm could not allocate a client handle.");
        }

        try
        {
            Check(NativeMethods.VigemConnect(_client), "connect to the ViGEm Bus");

            _target = NativeMethods.VigemTargetX360Alloc();
            if (_target == IntPtr.Zero)
            {
                throw new VigemException("ViGEm could not allocate an Xbox 360 controller.");
            }

            Check(NativeMethods.VigemTargetAdd(_client, _target), "add the virtual Xbox 360 controller");
            _connected = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void SetState(GamepadState state)
    {
        var report = new XusbReport
        {
            Buttons = state.Buttons,
            LeftTrigger = state.LeftTrigger,
            RightTrigger = state.RightTrigger,
            LeftX = state.LeftX,
            LeftY = state.LeftY,
            RightX = state.RightX,
            RightY = state.RightY
        };

        Check(
            NativeMethods.VigemTargetX360Update(_client, _target, report),
            "update the virtual Xbox 360 controller");
    }

    public void Dispose()
    {
        if (_target != IntPtr.Zero)
        {
            if (_connected)
            {
                var result = NativeMethods.VigemTargetRemove(_client, _target);
                if (result != Success)
                {
                    Console.Error.WriteLine(
                        $"ViGEm could not remove the virtual controller (0x{result:X8}).");
                }
            }

            NativeMethods.VigemTargetFree(_target);
            _target = IntPtr.Zero;
        }

        if (_client != IntPtr.Zero)
        {
            NativeMethods.VigemDisconnect(_client);
            NativeMethods.VigemFree(_client);
            _client = IntPtr.Zero;
        }

        _connected = false;
    }

    private static void Check(uint result, string operation)
    {
        if (result != Success)
        {
            throw new VigemException(
                $"ViGEm failed to {operation} (error 0x{result:X8}). " +
                "Check that the ViGEm Bus driver is installed and compatible.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XusbReport
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftX;
        public short LeftY;
        public short RightX;
        public short RightY;
    }

    private static class NativeMethods
    {
        [DllImport("vigemclient.dll", EntryPoint = "vigem_alloc", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VigemAlloc();

        [DllImport("vigemclient.dll", EntryPoint = "vigem_connect", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint VigemConnect(IntPtr client);

        [DllImport("vigemclient.dll", EntryPoint = "vigem_disconnect", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void VigemDisconnect(IntPtr client);

        [DllImport("vigemclient.dll", EntryPoint = "vigem_free", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void VigemFree(IntPtr client);

        [DllImport("vigemclient.dll", EntryPoint = "vigem_target_x360_alloc", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VigemTargetX360Alloc();

        [DllImport("vigemclient.dll", EntryPoint = "vigem_target_add", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint VigemTargetAdd(IntPtr client, IntPtr target);

        [DllImport("vigemclient.dll", EntryPoint = "vigem_target_remove", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint VigemTargetRemove(IntPtr client, IntPtr target);

        [DllImport("vigemclient.dll", EntryPoint = "vigem_target_free", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void VigemTargetFree(IntPtr target);

        [DllImport("vigemclient.dll", EntryPoint = "vigem_target_x360_update", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint VigemTargetX360Update(
            IntPtr client,
            IntPtr target,
            XusbReport report);
    }
}

public sealed class VigemException(string message) : Exception(message);
