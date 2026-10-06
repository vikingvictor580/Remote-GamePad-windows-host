namespace RemoteGamePad.Host;

public readonly record struct GamepadState(
    ushort Buttons,
    byte LeftTrigger,
    byte RightTrigger,
    short LeftX,
    short LeftY,
    short RightX,
    short RightY,
    float GyroX = 0,
    float GyroY = 0,
    float GyroZ = 0,
    float AccelX = 0,
    float AccelY = 0,
    float AccelZ = 0)
{
    public static GamepadState Neutral => default;
}
