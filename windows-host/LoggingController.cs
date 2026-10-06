namespace RemoteGamePad.Host;

public sealed class LoggingController : IGamepadSink
{
    private GamepadState _lastState;

    public void SetState(GamepadState state)
    {
        if (state == _lastState)
        {
            return;
        }

        _lastState = state;
        Console.WriteLine($"Input: {state}");
    }

    public void Dispose()
    {
    }
}
