namespace RemoteGamePad.Host;

public interface IGamepadSink : IDisposable
{
    void SetState(GamepadState state);
}
