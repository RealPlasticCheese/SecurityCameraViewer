using System.Security.AccessControl;
using System.Security.Principal;

namespace SecurityCameraViewer.Web.Settings;

/// <summary>Writes private settings and runtime documents using an atomic replacement.</summary>
public static class PrivateFile
{
    public static void Write(string path, string content) => WriteBytes(path, System.Text.Encoding.UTF8.GetBytes(content));

    public static void WriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = OperatingSystem.IsWindows()
                ? new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, CreateWindowsSecurity())
                : new FileStream(temporary, options))
            {
                stream.Write(content);
                stream.Flush(true);
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static void RestrictAccess(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            new FileInfo(path).SetAccessControl(CreateWindowsSecurity());
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static FileSecurity CreateWindowsSecurity()
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Cannot identify the settings owner.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
