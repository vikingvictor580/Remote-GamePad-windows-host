using RemoteGamePad.Host;

var dryRun = args.Length == 1 && string.Equals(args[0], "--dry-run", StringComparison.Ordinal);
if (args.Length > 0 && !dryRun)
{
    MessageBox.Show(
        "Usage: RemoteGamePad.Host [--dry-run]",
        "Remote GamePad Host",
        MessageBoxButtons.OK,
        MessageBoxIcon.Information);
    return;
}

ApplicationConfiguration.Initialize();
Application.Run(new MainForm(dryRun));
