using System.Text.Json;
using SecurityCameraViewer.SettingsMigration;

if (args.Length is < 1 or > 2 || args[0] is "--help" or "-h")
{
    Console.WriteLine("Usage: dotnet run --project tools/SecurityCameraViewer.SettingsMigration -- /absolute/path/settings.json [migration-inputs.json]");
    Console.WriteLine("Stop the server first. See docs/settings.md for backup, custom input paths and rollback instructions.");
    return args.Length == 1 && args[0] is "--help" or "-h" ? 0 : 1;
}

try
{
    var inputs = args.Length == 2
        ? JsonSerializer.Deserialize<MigrationInputs>(File.ReadAllText(args[1])) ?? throw new InvalidDataException()
        : new();
    var result = new LegacySettingsMigrator().Migrate(args[0], inputs);
    Console.WriteLine(result.BackupDirectory is null ? "Settings are already unified; no files changed."
        : $"Imported {result.SourceCount} files. Original files backed up to {result.BackupDirectory}.");
    Console.WriteLine("Settings validated. Start the updated server and verify cameras, recordings and integrations.");
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
{
    // Parser diagnostics and nested exceptions can contain passwords.
    var detail = ex is JsonException ? "A legacy document or migration input manifest contains invalid JSON." : ex.Message;
    Console.Error.WriteLine($"Migration stopped ({ex.GetType().Name}): {detail} No source files were removed. See docs/settings.md.");
    return 1;
}
