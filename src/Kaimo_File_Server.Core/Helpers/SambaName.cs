namespace Kaimo_File_Server.Core.Helpers;

/// <summary>
/// Samba-facing account and share-name constraints. These limits are byte
/// limits at every protocol boundary, not database character limits.
/// </summary>
public static class SambaName
{
    public const int MaxUsernameBytes = 32;
    public const int MaxShareNameBytes = 64;

    public static bool IsValidUsername(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > MaxUsernameBytes ||
            !IsAsciiAlphaNumeric(value[0]) ||
            value[^1] == '.' ||
            IsReservedDeviceName(value))
            return false;

        // Purely numeric names are rejected: POSIX tools (useradd, id, chown)
        // interpret them as UIDs, so the Samba host cannot provision them.
        // Trailing dots and device names are rejected so every username is
        // also a valid Windows/SMB folder name.
        return value.All(IsAsciiNameCharacter) && !value.All(char.IsAsciiDigit);
    }

    public static bool IsValidShareName(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > MaxShareNameBytes ||
            value[0] == '.' ||
            value[^1] == '.' ||
            IsReservedShareName(value))
            return false;

        return value.All(IsAsciiNameCharacter);
    }

    public static bool IsValidContext(string? username, string? shareName) =>
        IsValidUsername(username) && IsValidShareName(shareName);

    public static void EnsureValidUsername(string? value, string parameterName)
    {
        if (!IsValidUsername(value))
            throw new ArgumentException(
                $"Samba username must be 1-{MaxUsernameBytes} ASCII bytes, " +
                "start with an alphanumeric character, and contain only " +
                "letters, digits, '.', '_' or '-', not end with '.', and not " +
                "be purely numeric or a reserved Windows device name.",
                parameterName);
    }

    public static void EnsureValidShareName(string? value, string parameterName)
    {
        if (!IsValidShareName(value))
            throw new ArgumentException(
                $"Samba share name must be 1-{MaxShareNameBytes} ASCII bytes, " +
                "contain only letters, digits, '.', '_' or '-', not start or " +
                "end with '.', and not be a reserved Samba section name.",
                parameterName);
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static bool IsAsciiNameCharacter(char value) =>
        IsAsciiAlphaNumeric(value) || value is '.' or '_' or '-';

    // Windows reserves these names regardless of extension ("con.txt").
    private static bool IsReservedDeviceName(string value)
    {
        string baseName = value.Split('.', 2)[0].ToUpperInvariant();
        return baseName is "CON" or "PRN" or "AUX" or "NUL"
            || (baseName.Length == 4
                && baseName[3] is >= '0' and <= '9'
                && baseName[..3] is "COM" or "LPT");
    }

    private static bool IsReservedShareName(string value) =>
        value.Equals("global", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("homes", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("printers", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("print$", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("ipc$", StringComparison.OrdinalIgnoreCase);
}
