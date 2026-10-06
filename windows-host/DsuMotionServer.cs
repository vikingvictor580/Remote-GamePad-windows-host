using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RemoteGamePad.Host;

public sealed class DsuMotionServer(int port, Func<GamepadState> getState)
{
    private const ushort ProtocolVersion = 1001;
    private const uint VersionRequest = 0x00100000;
    private const uint VersionResponse = 0x00100000;
    private const uint PortListRequest = 0x00100001;
    private const uint PortInfoResponse = 0x00100001;
    private const uint PadDataRequest = 0x00100002;
    private const uint PadDataResponse = 0x00100002;
    private const int HeaderSize = 16;
    private const int MotionIntervalMilliseconds = 16;
    private const int SubscriberTimeoutMilliseconds = 5000;
    private const byte PadId = 0;
    private const byte PadStateConnected = 2;
    private const byte ModelDualShock4 = 2;
    private const byte ConnectionUsb = 1;
    private const byte BatteryFull = 5;
    private static readonly byte[] PadMacAddress = [0x02, 0x52, 0x47, 0x50, 0x01, 0x01];
    private readonly ConcurrentDictionary<IPEndPoint, long> _subscribers = new();
    private readonly uint _serverId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
    private uint _packetNumber;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        Console.WriteLine($"DSU motion server listening on UDP port {port}.");
        var receiveTask = udp.ReceiveAsync(cancellationToken).AsTask();
        var timerTask = Task.Delay(MotionIntervalMilliseconds, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            var completedTask = await Task.WhenAny(receiveTask, timerTask);
            if (completedTask == receiveTask)
            {
                var packet = await receiveTask;
                receiveTask = udp.ReceiveAsync(cancellationToken).AsTask();
                await HandlePacketAsync(udp, packet, cancellationToken);
            }

            if (completedTask == timerTask)
            {
                await BroadcastMotionAsync(udp, cancellationToken);
                timerTask = Task.Delay(MotionIntervalMilliseconds, cancellationToken);
            }
        }
    }

    private async Task HandlePacketAsync(
        UdpClient udp,
        UdpReceiveResult packet,
        CancellationToken cancellationToken)
    {
        var data = packet.Buffer;
        if (data.Length < HeaderSize + sizeof(uint) ||
            data[0] != (byte)'D' || data[1] != (byte)'S' ||
            data[2] != (byte)'U' || data[3] != (byte)'C')
        {
            return;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4, 2));
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6, 2));
        var totalLength = HeaderSize + payloadLength;
        if (version > ProtocolVersion || totalLength > data.Length || totalLength < HeaderSize + sizeof(uint))
        {
            return;
        }

        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
        data.AsSpan(8, 4).Clear();
        var actualCrc = CalculateCrc32(data.AsSpan(0, totalLength));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), expectedCrc);
        if (expectedCrc != actualCrc)
        {
            return;
        }

        var messageType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(HeaderSize, sizeof(uint)));
        switch (messageType)
        {
            case VersionRequest:
                var versionPayload = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(versionPayload, VersionResponse);
                BinaryPrimitives.WriteUInt16LittleEndian(versionPayload.AsSpan(4), ProtocolVersion);
                await SendPacketAsync(udp, packet.RemoteEndPoint, versionPayload, cancellationToken);
                break;

            case PortListRequest:
                await SendPortInfoAsync(udp, packet, totalLength, cancellationToken);
                break;

            case PadDataRequest:
                if (totalLength >= HeaderSize + 12)
                {
                    var flags = data[HeaderSize + 4];
                    var requestedPad = data[HeaderSize + 5];
                    var requestedMacMatches = data.AsSpan(HeaderSize + 6, PadMacAddress.Length)
                        .SequenceEqual(PadMacAddress);
                    if (flags == 0 ||
                        ((flags & 0x01) != 0 && requestedPad == PadId) ||
                        ((flags & 0x02) != 0 && requestedMacMatches))
                    {
                        _subscribers[packet.RemoteEndPoint] = Environment.TickCount64;
                    }
                }

                break;
        }
    }

    private async Task SendPortInfoAsync(
        UdpClient udp,
        UdpReceiveResult packet,
        int totalLength,
        CancellationToken cancellationToken)
    {
        var data = packet.Buffer;
        if (totalLength < HeaderSize + 8)
        {
            return;
        }

        var requestedCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(HeaderSize + 4, 4));
        if (requestedCount < 0 || requestedCount > 4 ||
            totalLength < HeaderSize + 8 + requestedCount)
        {
            return;
        }

        for (var index = 0; index < requestedCount; index++)
        {
            var requestedPad = data[HeaderSize + 8 + index];
            if (requestedPad is not (PadId or 0xFF))
            {
                continue;
            }

            var payload = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, PortInfoResponse);
            payload[4] = PadId;
            payload[5] = PadStateConnected;
            payload[6] = ModelDualShock4;
            payload[7] = ConnectionUsb;
            PadMacAddress.CopyTo(payload, 8);
            payload[14] = BatteryFull;
            payload[15] = 0;
            await SendPacketAsync(udp, packet.RemoteEndPoint, payload, cancellationToken);
        }
    }

    private async Task BroadcastMotionAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        var now = Environment.TickCount64;
        foreach (var subscriber in _subscribers)
        {
            if (now - subscriber.Value > SubscriberTimeoutMilliseconds)
            {
                _subscribers.TryRemove(subscriber.Key, out _);
                continue;
            }

            var payload = CreatePadDataPayload(getState(), _packetNumber++);
            await SendPacketAsync(udp, subscriber.Key, payload, cancellationToken);
        }
    }

    private static byte[] CreatePadDataPayload(GamepadState state, uint packetNumber)
    {
        var payload = new byte[80];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, PadDataResponse);
        payload[4] = PadId;
        payload[5] = PadStateConnected;
        payload[6] = ModelDualShock4;
        payload[7] = ConnectionUsb;
        PadMacAddress.CopyTo(payload, 8);
        payload[14] = BatteryFull;
        payload[15] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), packetNumber);

        payload[20] = (byte)((state.Buttons & 0x000F) |
            ((state.Buttons & 0x4000) != 0 ? 0x10 : 0) |
            ((state.Buttons & 0x1000) != 0 ? 0x20 : 0) |
            ((state.Buttons & 0x2000) != 0 ? 0x40 : 0) |
            ((state.Buttons & 0x8000) != 0 ? 0x80 : 0));
        payload[21] = (byte)(
            ((state.Buttons & 0x0100) != 0 ? 0x01 : 0) |
            ((state.Buttons & 0x0200) != 0 ? 0x02 : 0) |
            (state.LeftTrigger != 0 ? 0x04 : 0) |
            (state.RightTrigger != 0 ? 0x08 : 0) |
            ((state.Buttons & 0x0020) != 0 ? 0x10 : 0) |
            ((state.Buttons & 0x0010) != 0 ? 0x20 : 0) |
            ((state.Buttons & 0x0040) != 0 ? 0x40 : 0) |
            ((state.Buttons & 0x0080) != 0 ? 0x80 : 0));

        payload[24] = ToAnalogStick(state.LeftX);
        payload[25] = ToAnalogStick(state.LeftY);
        payload[26] = ToAnalogStick(state.RightX);
        payload[27] = ToAnalogStick(state.RightY);
        payload[28] = state.LeftTrigger;
        payload[29] = state.RightTrigger;

        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(48), (ulong)Environment.TickCount64 * 1000);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(56), state.AccelX);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(60), state.AccelY);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(64), state.AccelZ);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(68), state.GyroX);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(72), state.GyroY);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(76), state.GyroZ);
        return payload;
    }

    private async Task SendPacketAsync(
        UdpClient udp,
        IPEndPoint endpoint,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var packet = new byte[HeaderSize + payload.Length];
        packet[0] = (byte)'D';
        packet[1] = (byte)'S';
        packet[2] = (byte)'U';
        packet[3] = (byte)'S';
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), _serverId);
        payload.CopyTo(packet, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), CalculateCrc32(packet));
        await udp.SendAsync(packet, endpoint, cancellationToken);
    }

    private static byte ToAnalogStick(short value)
    {
        return (byte)(((int)value - short.MinValue) / 257);
    }

    private static uint CalculateCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }
}
