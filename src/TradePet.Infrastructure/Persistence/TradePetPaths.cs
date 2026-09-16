namespace TradePet.Infrastructure.Persistence;

public static class TradePetPaths
{
    public static string GetDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "TradePet");
    }

    public static string GetDatabasePath() => Path.Combine(GetDataDirectory(), "tradepet.db");

    public static string GetDatabaseBackupDirectory() => Path.Combine(GetDataDirectory(), "backups");

    public static string GetLogDirectory() => Path.Combine(GetDataDirectory(), "logs");

    public static string GetAttachmentDirectory() => Path.Combine(GetDataDirectory(), "attachments");

    public static string GetExportDirectory() => Path.Combine(GetDataDirectory(), "exports");
}
