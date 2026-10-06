# Remote GamePad Windows host

The host runs on the Windows PC, creates one virtual Xbox 360 controller through ViGEm, and receives controller state over UDP from an Android phone. Connect over Wi-Fi or use Android USB tethering; the desktop window lists active network addresses, UDP port, pairing PIN, a QR code to scan in the Android app, and phone pairing status.

## Prerequisites

- .NET 8 or later.
- The ViGEm Bus driver and matching `vigemclient.dll` available to the host process.
- Windows Firewall permission for inbound UDP on ports `26760` (gamepad) and `26761` (DSU motion).

ViGEm and ViGEmClient have been retired by their maintainers. This host uses the documented ViGEmClient native API and does not install or bundle the driver. The virtual-controller startup requires a compatible ViGEm installation. Use `--dry-run` to test the network host without loading the native library.

## Run

From this directory: windows-host\bin\Debug\net8.0-windows\RemoteGamePad.Host.exe

```powershell
dotnet run
```

The host window opens and starts the server. In the Android app, tap **Scan QR** and scan the code shown beside the pairing PIN. The QR code contains the preferred local address and the current PIN; USB tethering addresses are preferred when available. You can also enter the matching address, port `26760`, and PIN manually. Use **Stop host** to stop; the virtual controller is removed on shutdown.

Choose **Light**, **Dark**, or **Ocean** from the appearance menu in the top-right of the host window. The connection steps are numbered in order: choose the matching PC network, scan the pairing QR code, then connect from the phone.

The Android app sends motion sensor samples along with controller reports. Gyro-to-stick aiming is configured in the app's **Settings → Gyroscope Motion Controls**. The host also exposes a DSU/Cemuhook-compatible motion server on UDP port `26761`; configure a compatible emulator/client to connect to the PC's address on that port. It identifies as a DualShock 4 and streams the phone's accelerometer and gyroscope readings, while the normal Xbox 360 virtual controller continues to receive buttons and sticks. Sensor axes are sent in Android's device coordinate frame.

### Connect by USB tethering

1. Connect the Android phone to the PC with a USB cable.
2. On the phone, open the network/hotspot settings and enable **USB tethering**. The exact menu name varies by Android version and manufacturer.
3. Wait for Windows to bring up the phone's USB network adapter. The host window refreshes its address list automatically and marks recognized tethering adapters; use **Refresh** if needed.
4. Enter the displayed **USB tethering** address, port `26760`, and pairing PIN in the Android app. If Windows Firewall asks, allow the host on the USB network.

USB tethering carries the same UDP protocol as Wi-Fi, so no USB serial mode or separate Android protocol is needed. Keep the phone connected with tethering enabled while using the controller.

For protocol development without ViGEm:

```powershell
dotnet run -- --dry-run
```

## UDP protocol

Controller packets are UTF-8 JSON, sent to UDP port `26760`; the host replies to the packet's source address and port. The separate binary DSU/Cemuhook protocol listens on UDP port `26761`.

Pair using the PIN printed by the host:

```json
{"type":"pair","pin":"01234567"}
```

Successful response:

```json
{"type":"paired","token":"session-token"}
```

Send controller reports using the returned token. Gyroscope values are in radians per second and accelerometer values are in meters per second squared; all six motion fields are optional for older clients. Keep sending reports while connected; if no report arrives for 500 ms, the host releases all controls and clears the motion state:

```json
{"type":"state","token":"session-token","buttons":4096,"leftTrigger":0,"rightTrigger":0,"leftX":0,"leftY":0,"rightX":0,"rightY":0,"gyroX":0.01,"gyroY":0.02,"gyroZ":0.03,"accelX":0,"accelY":0,"accelZ":9.81}
```

`buttons` is the Xbox XUSB button bitmask: D-pad up/down/left/right `1/2/4/8`, Start `16`, Back `32`, left/right stick clicks `64/128`, left/right shoulders `256/512`, A/B/X/Y `4096/8192/16384/32768`. Triggers use `0..255`; stick axes use signed 16-bit values. Unsupported button bits, non-finite motion values, and out-of-range values are rejected.

Pairing binds the session token to the phone's source IP and UDP port. The PIN/token are not encrypted, and DSU subscriptions are not authenticated; use both services only on a trusted local network. This MVP does not provide internet access, vibration feedback, or multi-phone gamepad pairing.
