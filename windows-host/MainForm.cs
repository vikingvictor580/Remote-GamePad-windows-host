using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using QRCoder;

namespace RemoteGamePad.Host;

public sealed class MainForm : Form
{
    private const int Port = 26760;
    private const int DsuPort = 26761;
    private static readonly Color BackgroundColor = Color.FromArgb(244, 247, 251);
    private static readonly Color AccentColor = Color.FromArgb(43, 91, 218);
    private static readonly Color TextColor = Color.FromArgb(35, 45, 65);
    private static readonly Color MutedColor = Color.FromArgb(104, 116, 137);
    private static readonly UiPalette[] Themes =
    [
        new("Light", Color.FromArgb(244, 247, 251), Color.White,
            Color.FromArgb(35, 45, 65), Color.FromArgb(104, 116, 137),
            Color.FromArgb(43, 91, 218), Color.FromArgb(232, 239, 255),
            Color.FromArgb(239, 243, 250), Color.FromArgb(220, 227, 238)),
        new("Dark", Color.FromArgb(26, 30, 38), Color.FromArgb(37, 43, 54),
            Color.FromArgb(235, 239, 247), Color.FromArgb(164, 175, 194),
            Color.FromArgb(112, 151, 255), Color.FromArgb(40, 52, 76),
            Color.FromArgb(51, 59, 73), Color.FromArgb(66, 76, 94)),
        new("Ocean", Color.FromArgb(13, 35, 47), Color.FromArgb(20, 52, 67),
            Color.FromArgb(225, 244, 249), Color.FromArgb(147, 190, 202),
            Color.FromArgb(44, 184, 190), Color.FromArgb(21, 67, 78),
            Color.FromArgb(29, 70, 83), Color.FromArgb(43, 91, 104))
    ];

    private readonly bool _dryRun;
    private readonly Label _statusLabel;
    private readonly Label _clientLabel;
    private readonly TextBox _addressesBox;
    private readonly Label _pinLabel;
    private readonly PictureBox _qrCodePicture;
    private readonly Label _qrCodeLabel;
    private readonly ComboBox _themeSelector;
    private readonly List<Control> _themedControls = [];
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly System.Windows.Forms.Timer _addressRefreshTimer;
    private CancellationTokenSource? _shutdown;
    private Task? _hostTask;
    private IPAddress? _preferredAddress;
    private bool _allowClose;
    private bool _hostFailed;

    public MainForm(bool dryRun)
    {
        _dryRun = dryRun;
        Text = "Remote GamePad Host";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(640, 600);
        ClientSize = new Size(720, 700);
        BackColor = BackgroundColor;
        Font = new Font("Segoe UI", 10);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(20, 16, 20, 16),
            BackColor = BackgroundColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = BackgroundColor
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        header.Controls.Add(MakeLabel("Connect your phone", 23, FontStyle.Bold, TextColor), 0, 0);
        header.Controls.Add(MakeLabel(
            "Use the same Wi-Fi, or connect by USB tethering. Scan the QR code in the Android app.",
            10,
            FontStyle.Regular,
            MutedColor), 0, 1);
        var themePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = BackgroundColor,
            Padding = new Padding(0, 14, 0, 0)
        };
        themePanel.Controls.Add(MakeLabel("APPEARANCE", 8, FontStyle.Bold, MutedColor));
        _themeSelector = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 140,
            Height = 30,
            Font = new Font("Segoe UI", 9),
            FlatStyle = FlatStyle.Flat
        };
        _themeSelector.Items.AddRange(Themes.Select(theme => theme.Name).Cast<object>().ToArray());
        _themeSelector.SelectedIndex = 0;
        _themeSelector.SelectedIndexChanged += (_, _) =>
            ApplyTheme(Themes[_themeSelector.SelectedIndex]);
        themePanel.Controls.Add(_themeSelector);
        header.Controls.Add(themePanel, 1, 0);
        header.SetRowSpan(themePanel, 2);
        root.Controls.Add(header, 0, 0);

        var statusPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(232, 239, 255),
            Padding = new Padding(14, 0, 14, 0),
            Tag = "status"
        };
        _statusLabel = MakeLabel("Starting host...", 10, FontStyle.Bold, AccentColor);
        _statusLabel.Dock = DockStyle.Fill;
        statusPanel.Controls.Add(_statusLabel);
        root.Controls.Add(statusPanel, 0, 1);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0, 10, 0, 4),
            BackColor = BackgroundColor
        };
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        root.Controls.Add(content, 0, 2);

        var connectionCard = MakeCard();
        var connectionLayout = MakeCardLayout(4);
        connectionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        connectionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        connectionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        connectionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        var addressHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.White
        };
        addressHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addressHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        addressHeader.Controls.Add(
            MakeLabel("1  ·  PC CONNECTION", 9, FontStyle.Bold, MutedColor), 0, 0);
        addressHeader.Controls.Add(MakeButton("Refresh list", RefreshAddresses), 1, 0);
        connectionLayout.Controls.Add(addressHeader, 0, 0);

        var addressArea = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.White
        };
        addressArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addressArea.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        _addressesBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = TextColor,
            Font = new Font("Consolas", 11),
            ScrollBars = ScrollBars.Vertical,
            Text = "Searching for network addresses..."
        };
        addressArea.Controls.Add(_addressesBox, 0, 0);
        addressArea.Controls.Add(MakeButton("Copy address", CopyAddresses), 1, 0);
        connectionLayout.Controls.Add(addressArea, 0, 1);

        var portRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.White
        };
        portRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        portRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        portRow.Controls.Add(
            MakeLabel($"Gamepad UDP   {Port}\r\nMotion UDP   {DsuPort}", 9, FontStyle.Bold, TextColor), 0, 0);
        portRow.Controls.Add(MakeButton("Copy port", () => CopyText(Port.ToString())), 1, 0);
        connectionLayout.Controls.Add(portRow, 0, 2);

        var connectionHint = MakeLabel(
            "USB tethering is listed first. Pick the same network your phone uses.",
            9,
            FontStyle.Regular,
            MutedColor);
        connectionLayout.Controls.Add(connectionHint, 0, 3);
        connectionCard.Controls.Add(connectionLayout);
        content.Controls.Add(connectionCard, 0, 0);

        var pinCard = MakeCard();
        var pinLayout = MakeCardLayout(2);
        pinLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        pinLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pinLayout.Controls.Add(MakeLabel("2  ·  SCAN TO PAIR", 9, FontStyle.Bold, MutedColor), 0, 0);
        var pinRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.White
        };
        pinRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pinRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        pinRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        _pinLabel = MakeLabel("--------", 22, FontStyle.Bold, TextColor);
        _pinLabel.Font = new Font("Consolas", 22, FontStyle.Bold);
        pinRow.Controls.Add(_pinLabel, 0, 0);
        pinRow.Controls.Add(MakeButton("Copy PIN", CopyPin), 1, 0);
        var qrCodePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White,
            Margin = Padding.Empty
        };
        qrCodePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        qrCodePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _qrCodeLabel = MakeLabel("SCAN TO CONNECT", 8, FontStyle.Bold, MutedColor);
        _qrCodeLabel.TextAlign = ContentAlignment.MiddleCenter;
        _qrCodePicture = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
            Margin = Padding.Empty
        };
        qrCodePanel.Controls.Add(_qrCodeLabel, 0, 0);
        qrCodePanel.Controls.Add(_qrCodePicture, 0, 1);
        pinRow.Controls.Add(qrCodePanel, 2, 0);
        pinLayout.Controls.Add(pinRow, 0, 1);
        pinCard.Controls.Add(pinLayout);
        content.Controls.Add(pinCard, 0, 1);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackgroundColor
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        _clientLabel = MakeLabel("Phone: Not paired", 9, FontStyle.Regular, MutedColor);
        footer.Controls.Add(_clientLabel, 0, 0);
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = BackgroundColor
        };
        _stopButton = MakeButton("Stop host", StopHost);
        _startButton = MakeButton("Start host", StartHost);
        _startButton.Tag = "primary";
        buttonPanel.Controls.Add(_stopButton);
        buttonPanel.Controls.Add(_startButton);
        footer.Controls.Add(buttonPanel, 1, 0);
        root.Controls.Add(footer, 0, 3);

        _startButton.Visible = false;
        _stopButton.Visible = true;
        _addressRefreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _addressRefreshTimer.Tick += (_, _) => RefreshAddresses();
        RefreshAddresses();
        _addressRefreshTimer.Start();
        Shown += (_, _) => StartHost();
        FormClosing += HandleFormClosing;
        _statusLabel.Text = _dryRun ? "Starting test mode..." : "Starting host...";
        FormClosed += (_, _) => _addressRefreshTimer.Dispose();
        _themedControls.AddRange(Descendants(this));
        _themedControls.Add(this);
        ApplyTheme(Themes[0]);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private void ApplyTheme(UiPalette theme)
    {
        foreach (var control in _themedControls)
        {
            switch (control)
            {
                case Form:
                    control.BackColor = theme.Background;
                    control.ForeColor = theme.Text;
                    break;
                case Button button:
                    button.BackColor = button.Tag as string == "primary" ? theme.Accent : theme.Button;
                    button.ForeColor = button.Tag as string == "primary" ? Color.White : theme.Text;
                    break;
                case RoundedCardPanel card:
                    card.FillColor = theme.Surface;
                    card.BorderColor = theme.Border;
                    card.Invalidate();
                    break;
                case ComboBox comboBox:
                    comboBox.BackColor = theme.Surface;
                    comboBox.ForeColor = theme.Text;
                    break;
                case TextBox textBox:
                    textBox.BackColor = theme.Surface;
                    textBox.ForeColor = theme.Text;
                    break;
                case Label label:
                    if (label == _statusLabel)
                    {
                        label.ForeColor = _statusTone switch
                        {
                            StatusTone.Success => Color.FromArgb(35, 132, 82),
                            StatusTone.Error => Color.FromArgb(220, 87, 87),
                            _ => theme.Accent
                        };
                    }
                    else if (label.Tag as string == "muted" || label.ForeColor == MutedColor)
                    {
                        label.ForeColor = theme.Muted;
                    }
                    else
                    {
                        label.ForeColor = theme.Text;
                    }

                    break;
                default:
                    if (control.Tag as string == "status")
                    {
                        control.BackColor = theme.StatusBackground;
                    }
                    else if (control.BackColor == Color.White)
                    {
                        control.BackColor = theme.Surface;
                    }
                    else
                    {
                        control.BackColor = theme.Background;
                    }

                    break;
            }
        }

        _qrCodePicture.BackColor = theme.Surface;
        _themeSelector.Invalidate();
    }

    private void StartHost()
    {
        if (_hostTask is { IsCompleted: false })
        {
            return;
        }

        _shutdown?.Dispose();
        _shutdown = null;
        _hostTask = null;
        IGamepadSink controller;
        try
        {
            controller = _dryRun ? new LoggingController() : new VigemController();
        }
        catch (DllNotFoundException exception)
        {
            ShowStartupError(
                $"ViGEm client library not found: {exception.Message}\r\n\r\n" +
                "Install the ViGEm client DLL and ViGEm Bus driver, or run with --dry-run.");
            return;
        }
        catch (EntryPointNotFoundException exception)
        {
            ShowStartupError($"The installed ViGEm client DLL is incompatible: {exception.Message}");
            return;
        }
        catch (VigemException exception)
        {
            ShowStartupError(exception.Message);
            return;
        }

        var session = new PairingSession();
        _pinLabel.Text = session.Pin;
        UpdateQrCode();
        _clientLabel.Text = "Phone: Not paired";
        var shutdown = new CancellationTokenSource();
        _shutdown = shutdown;
        _hostFailed = false;
        _statusLabel.ForeColor = AccentColor;
        _statusTone = StatusTone.Info;
        _statusLabel.Text = _dryRun
            ? "Test mode is running — no virtual controller will be created."
            : "Host is running — Xbox 360 controller ready.";
        _startButton.Visible = false;
        _stopButton.Visible = true;
        _hostTask = Task.Run(async () =>
        {
            using (controller)
            {
                try
                {
                    var host = new UdpControllerHost(
                        Port,
                        session,
                        controller.SetState,
                        endpoint => UpdateClient(endpoint.Address));
                    await host.RunAsync(shutdown.Token);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                }
                catch (SocketException exception)
                {
                    UpdateHostFailure($"Could not start the UDP server: {exception.Message}");
                }
                catch (VigemException exception)
                {
                    UpdateHostFailure(exception.Message);
                }
            }

            if (!shutdown.IsCancellationRequested)
            {
                UpdateHostStopped();
            }
        });
    }

    private async void StopHost()
    {
        await StopHostAsync();
    }

    private async Task StopHostAsync()
    {
        var task = _hostTask;
        if (task is null)
        {
            return;
        }

        _shutdown?.Cancel();
        await task;
        _shutdown?.Dispose();
        _shutdown = null;
        _hostTask = null;
        if (!_allowClose)
        {
            _statusLabel.ForeColor = MutedColor;
            _statusTone = StatusTone.Info;
            _statusLabel.Text = "Host stopped.";
            _startButton.Visible = true;
            _stopButton.Visible = false;
        }
    }

    private async void HandleFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        await StopHostAsync();
        _allowClose = true;
        Close();
    }

    private void ShowStartupError(string message)
    {
        _statusLabel.ForeColor = Color.FromArgb(184, 55, 55);
        _statusTone = StatusTone.Error;
        _statusLabel.Text = "Host could not start.";
        _startButton.Visible = true;
        _stopButton.Visible = false;
        MessageBox.Show(this, message, "Remote GamePad Host", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void UpdateClient(IPAddress address)
    {
        UpdateUi(() =>
        {
            _clientLabel.Text = $"Phone: Paired ({address})";
            _statusLabel.ForeColor = Color.FromArgb(35, 132, 82);
            _statusTone = StatusTone.Success;
            _statusLabel.Text = "Phone paired — controller is ready.";
        });
    }

    private void UpdateHostFailure(string message)
    {
        UpdateUi(() =>
        {
            _hostFailed = true;
            _statusLabel.ForeColor = Color.FromArgb(184, 55, 55);
            _statusTone = StatusTone.Error;
            _statusLabel.Text = message;
        });
    }

    private void UpdateHostStopped()
    {
        UpdateUi(() =>
        {
            if (_shutdown?.IsCancellationRequested == true)
            {
                return;
            }

            _startButton.Visible = true;
            _stopButton.Visible = false;
            if (!_hostFailed)
            {
                _statusLabel.Text = "Host stopped after an error. Check the message above and try again.";
            }

            _statusLabel.ForeColor = Color.FromArgb(184, 55, 55);
            _statusTone = StatusTone.Error;
        });
    }

    private void UpdateUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(action);
    }

    private void CopyAddresses()
    {
        var addressLine = _addressesBox.Text
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (addressLine is null || addressLine.StartsWith("No local IPv4", StringComparison.Ordinal))
        {
            MessageBox.Show(
                this,
                "No active network address is available. Connect the phone by Wi-Fi or enable USB tethering, then refresh.",
                "Connection address unavailable",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var separator = addressLine.IndexOf(": ", StringComparison.Ordinal);
        CopyText(separator >= 0 ? addressLine[(separator + 2)..] : addressLine);
    }

    private void CopyPin()
    {
        if (_pinLabel.Text == "--------")
        {
            MessageBox.Show(this, "Start the host before copying the pairing PIN.", "PIN unavailable",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        CopyText(_pinLabel.Text);
    }

    private void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            MessageBox.Show(this, "Could not copy to the clipboard. Please select and copy the text.",
                "Clipboard unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshAddresses()
    {
        var connectionAddresses = GetLocalIpv4Addresses()
            .Select(item => new
            {
                item.Address,
                item.InterfaceName,
                Category = GetInterfaceCategory(item.InterfaceName, item.Description, item.Type),
                Priority = GetInterfacePriority(item.InterfaceName, item.Description, item.Type)
            })
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Address.ToString(), StringComparer.Ordinal)
            .ToArray();
        _preferredAddress = connectionAddresses.FirstOrDefault()?.Address;
        var addresses = connectionAddresses
            .Select(item => $"{item.Category}: {item.Address}:{Port}")
            .ToArray();

        var updated = addresses.Length == 0
            ? "No local IPv4 address found"
            : string.Join(Environment.NewLine, addresses);
        if (_addressesBox.Text != updated)
        {
            _addressesBox.Text = updated;
        }

        UpdateQrCode();
    }

    private void UpdateQrCode()
    {
        if (_pinLabel.Text == "--------" || _preferredAddress is null)
        {
            _qrCodePicture.Image?.Dispose();
            _qrCodePicture.Image = null;
            _qrCodeLabel.Text = _preferredAddress is null ? "NO NETWORK ADDRESS" : "START HOST TO PAIR";
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            host = _preferredAddress.ToString(),
            pin = _pinLabel.Text
        });
        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(qrData);
        using var stream = new MemoryStream(qrCode.GetGraphic(6));
        using var image = Image.FromStream(stream);
        var replacement = new Bitmap(image);
        var previous = _qrCodePicture.Image;
        _qrCodePicture.Image = replacement;
        previous?.Dispose();
        _qrCodeLabel.Text = $"SCAN ({_preferredAddress})";
    }

    private static IEnumerable<(IPAddress Address, string InterfaceName, string Description, NetworkInterfaceType Type)>
        GetLocalIpv4Addresses()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var address in network.GetIPProperties().UnicastAddresses)
            {
                var ip = address.Address;
                if (ip.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ip) &&
                    !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    yield return (ip, network.Name, network.Description, network.NetworkInterfaceType);
                }
            }
        }
    }

    private static string GetInterfaceCategory(
        string name,
        string description,
        NetworkInterfaceType type)
    {
        if (IsUsbTetheringAdapter(name, description))
        {
            return "USB tethering";
        }

        if (IsUsbNetworkAdapter(name, description))
        {
            return "USB network";
        }

        return type switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet => "Ethernet",
            _ => $"Network ({name})"
        };
    }

    private static int GetInterfacePriority(
        string name,
        string description,
        NetworkInterfaceType type)
    {
        if (IsUsbTetheringAdapter(name, description) || IsUsbNetworkAdapter(name, description))
        {
            return 0;
        }

        return type switch
        {
            NetworkInterfaceType.Wireless80211 => 1,
            NetworkInterfaceType.Ethernet => 2,
            _ => 3
        };
    }

    private static bool IsUsbTetheringAdapter(string name, string description)
    {
        var adapterDetails = $"{name} {description}";
        return adapterDetails.Contains("RNDIS", StringComparison.OrdinalIgnoreCase) ||
               adapterDetails.Contains("Remote NDIS", StringComparison.OrdinalIgnoreCase) ||
               adapterDetails.Contains("Ethernet Gadget", StringComparison.OrdinalIgnoreCase) ||
               adapterDetails.Contains("Android", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsbNetworkAdapter(string name, string description)
    {
        return ($"{name} {description}").Contains("USB", StringComparison.OrdinalIgnoreCase);
    }

    private static Panel MakeCard()
    {
        return new RoundedCardPanel
        {
            Dock = DockStyle.Fill,
            FillColor = Color.White,
            BorderColor = Color.FromArgb(230, 234, 241),
            Padding = new Padding(14),
            Margin = new Padding(0, 0, 0, 10)
        };
    }

    private static TableLayoutPanel MakeCardLayout(int rows)
    {
        return new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = rows,
            BackColor = Color.White
        };
    }

    private static Label MakeLabel(string text, float size, FontStyle style, Color color)
    {
        return new Label
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", size, style),
            ForeColor = color,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(239, 243, 250),
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            Cursor = Cursors.Hand,
            Margin = new Padding(4, 2, 0, 2),
            MinimumSize = new Size(88, 30),
            FlatAppearance = { BorderSize = 0 }
        };
        void UpdateRegion()
        {
            if (button.Width <= 0 || button.Height <= 0)
            {
                return;
            }

            using var path = RoundedCardPanel.CreateRoundedPath(
                new Rectangle(0, 0, button.Width, button.Height), 8);
            button.Region?.Dispose();
            button.Region = new Region(path);
        }

        button.Resize += (_, _) => UpdateRegion();
        button.HandleCreated += (_, _) => UpdateRegion();
        button.Click += (_, _) => onClick();
        return button;
    }

    private enum StatusTone
    {
        Info,
        Success,
        Error
    }

    private StatusTone _statusTone = StatusTone.Info;

    private sealed record UiPalette(
        string Name,
        Color Background,
        Color Surface,
        Color Text,
        Color Muted,
        Color Accent,
        Color StatusBackground,
        Color Button,
        Color Border);

    private sealed class RoundedCardPanel : Panel
    {
        private const int CornerRadius = 14;

        public RoundedCardPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        public Color FillColor { get; set; } = Color.White;
        public Color BorderColor { get; set; } = Color.FromArgb(230, 234, 241);

        protected override void OnPaintBackground(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            eventArgs.Graphics.SmoothingMode =
                System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var bounds = ClientRectangle;
            bounds.Width--;
            bounds.Height--;
            using var path = CreateRoundedPath(bounds, CornerRadius);
            using var fill = new SolidBrush(FillColor);
            using var border = new Pen(BorderColor);
            eventArgs.Graphics.FillPath(fill, path);
            eventArgs.Graphics.DrawPath(border, path);
        }

        public static System.Drawing.Drawing2D.GraphicsPath CreateRoundedPath(
            Rectangle bounds,
            int radius)
        {
            var diameter = radius * 2;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
