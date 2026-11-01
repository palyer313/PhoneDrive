namespace PhoneDrive.Core;

public static class AppPaths
{
    // Allows isolated tests and portable deployments without touching existing user data.
    public static string DataDirectory => Path.GetFullPath(
        Environment.GetEnvironmentVariable("PHONEDRIVE_DATA_DIR") is { Length: > 0 } path
            ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneDrive"));
}
