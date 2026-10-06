using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemoteGamePad.Host;

public sealed class UdpControllerHost(
    int port,
    PairingSession session,
    Action<GamepadState> setState,
    Action<IPEndPoint>? onClientPaired = null)
{
    private const int MaxPacketBytes = 1024;
    private const int WatchdogMilliseconds = 500;
    private const int DsuPort = 26761;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        var stateLock = new object();
        var latestState = GamepadState.Neutral;
        var dsuServer = new DsuMotionServer(DsuPort, () =>
        {
            lock (stateLock)
            {
                return latestState;
            }
        });
        var dsuTask = dsuServer.RunAsync(cancellationToken);
        var lastStateAt = Stopwatch.GetTimestamp();
        var stateIsActive = false;
        var receiveTask = udp.ReceiveAsync(cancellationToken).AsTask();

        while (!cancellationToken.IsCancellationRequested)
        {
            var timerTask = Task.Delay(100, cancellationToken);
            var completedTask = await Task.WhenAny(receiveTask, timerTask);

            if (completedTask == receiveTask)
            {
                var packet = await receiveTask;
                receiveTask = udp.ReceiveAsync(cancellationToken).AsTask();
                await HandlePacketAsync(udp, session, packet, cancellationToken,
                    onClientPaired,
                    state =>
                    {
                        lock (stateLock)
                        {
                            latestState = state;
                        }

                        setState(state);
                        stateIsActive = state != GamepadState.Neutral;
                        lastStateAt = Stopwatch.GetTimestamp();
                    });
            }

            if (dsuTask.IsFaulted)
            {
                await dsuTask;
            }

            if (stateIsActive &&
                Stopwatch.GetElapsedTime(lastStateAt).TotalMilliseconds >= WatchdogMilliseconds)
            {
                lock (stateLock)
                {
                    latestState = GamepadState.Neutral;
                }

                setState(GamepadState.Neutral);
                stateIsActive = false;
                Console.WriteLine("Controller input timed out; all buttons and sticks were released.");
            }
        }

        await dsuTask;
    }

    private static async Task HandlePacketAsync(
        UdpClient udp,
        PairingSession session,
        UdpReceiveResult packet,
        CancellationToken cancellationToken,
        Action<IPEndPoint>? onClientPaired,
        Action<GamepadState> setState)
    {
        if (packet.Buffer.Length == 0 || packet.Buffer.Length > MaxPacketBytes)
        {
            return;
        }

        ControllerPacket? message;
        try
        {
            message = JsonSerializer.Deserialize<ControllerPacket>(packet.Buffer, JsonOptions);
        }
        catch (JsonException)
        {
            await SendAsync(udp, packet.RemoteEndPoint,
                new ServerResponse("error", "Invalid JSON packet."), cancellationToken);
            return;
        }

        if (message is null)
        {
            return;
        }

        if (string.Equals(message.Type, "pair", StringComparison.Ordinal))
        {
            if (!session.TryPair(packet.RemoteEndPoint, message.Pin))
            {
                await SendAsync(udp, packet.RemoteEndPoint,
                    new ServerResponse("error", "Pairing PIN is invalid or pairing is rate-limited."),
                    cancellationToken);
                return;
            }

            await SendAsync(udp, packet.RemoteEndPoint,
                new ServerResponse("paired", Token: session.Token), cancellationToken);
            Console.WriteLine($"Paired Android client at {packet.RemoteEndPoint.Address}.");
            onClientPaired?.Invoke(packet.RemoteEndPoint);
            return;
        }

        if (!string.Equals(message.Type, "state", StringComparison.Ordinal) ||
            !session.Authenticates(packet.RemoteEndPoint, message.Token))
        {
            await SendAsync(udp, packet.RemoteEndPoint,
                new ServerResponse("error", "Pair this device before sending controller input."),
                cancellationToken);
            return;
        }

        if (!message.TryGetState(out var state))
        {
            await SendAsync(udp, packet.RemoteEndPoint,
                new ServerResponse("error", "Controller state is outside the supported range."),
                cancellationToken);
            return;
        }

        setState(state);
    }

    private static async Task SendAsync(
        UdpClient udp,
        IPEndPoint endpoint,
        ServerResponse response,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        await udp.SendAsync(bytes, endpoint, cancellationToken);
    }

    private sealed record ServerResponse(string Type, string? Message = null, string? Token = null);

    private sealed class ControllerPacket
    {
        private const int SupportedButtons = 0xF3FF;

        public string? Type { get; init; }
        public string? Pin { get; init; }
        public string? Token { get; init; }
        public int? Buttons { get; init; }
        public int? LeftTrigger { get; init; }
        public int? RightTrigger { get; init; }
        public int? LeftX { get; init; }
        public int? LeftY { get; init; }
        public int? RightX { get; init; }
        public int? RightY { get; init; }
        public float? GyroX { get; init; }
        public float? GyroY { get; init; }
        public float? GyroZ { get; init; }
        public float? AccelX { get; init; }
        public float? AccelY { get; init; }
        public float? AccelZ { get; init; }

        public bool TryGetState(out GamepadState state)
        {
            state = GamepadState.Neutral;
            if (Buttons is not int buttons || buttons < 0 || (buttons & ~SupportedButtons) != 0 ||
                LeftTrigger is not int leftTrigger || leftTrigger is < byte.MinValue or > byte.MaxValue ||
                RightTrigger is not int rightTrigger || rightTrigger is < byte.MinValue or > byte.MaxValue ||
                LeftX is not int leftX || leftX is < short.MinValue or > short.MaxValue ||
                LeftY is not int leftY || leftY is < short.MinValue or > short.MaxValue ||
                RightX is not int rightX || rightX is < short.MinValue or > short.MaxValue ||
                RightY is not int rightY || rightY is < short.MinValue or > short.MaxValue ||
                !IsValidMotion(GyroX) || !IsValidMotion(GyroY) || !IsValidMotion(GyroZ) ||
                !IsValidMotion(AccelX) || !IsValidMotion(AccelY) || !IsValidMotion(AccelZ))
            {
                return false;
            }

            state = new GamepadState(
                (ushort)buttons,
                (byte)leftTrigger,
                (byte)rightTrigger,
                (short)leftX,
                (short)leftY,
                (short)rightX,
                (short)rightY,
                GyroX ?? 0,
                GyroY ?? 0,
                GyroZ ?? 0,
                AccelX ?? 0,
                AccelY ?? 0,
                AccelZ ?? 0);
            return true;
        }

        private static bool IsValidMotion(float? value)
        {
            return value is null || (float.IsFinite(value.Value) && Math.Abs(value.Value) <= 1000);
        }
    }
}

public sealed class PairingSession
{
    private static readonly TimeSpan AttemptInterval = TimeSpan.FromMilliseconds(500);
    private readonly string _pin;
    private long _lastAttemptAt;

    public PairingSession()
        : this(RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8"))
    {
    }

    internal PairingSession(string pin)
    {
        _pin = pin;
    }

    public string Pin => _pin;
    public string? Token { get; private set; }
    private IPEndPoint? _endpoint;

    public bool TryPair(IPEndPoint endpoint, string? pin)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastAttemptAt != 0 &&
            Stopwatch.GetElapsedTime(_lastAttemptAt, now) < AttemptInterval)
        {
            return false;
        }

        _lastAttemptAt = now;
        if (!string.Equals(pin, _pin, StringComparison.Ordinal))
        {
            return false;
        }

        _endpoint = endpoint;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return true;
    }

    public bool Authenticates(IPEndPoint endpoint, string? token)
    {
        return _endpoint is not null &&
               _endpoint.Equals(endpoint) &&
               Token is not null &&
               string.Equals(Token, token, StringComparison.Ordinal);
    }
}
