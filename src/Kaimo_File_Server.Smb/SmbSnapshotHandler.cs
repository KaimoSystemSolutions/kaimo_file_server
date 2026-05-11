using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;

namespace Kaimo_File_Server.Smb
{
    /// <summary>
    /// Handles SMB snapshot/versioning protocol integration:
    ///   1. FSCTL_SRV_ENUMERATE_SNAPSHOTS returns @GMT- timestamps to Windows Explorer
    ///   2. @GMT- path parsing resolves versioned file paths to version content
    /// 
    /// This enables Windows "Previous Versions" tab (right-click → Properties → Previous Versions)
    /// to work transparently with the Kaimo file server's versioning system.
    /// 
    /// Protocol flow:
    ///   Client: FSCTL_SRV_ENUMERATE_SNAPSHOTS on share root
    ///   Server: responds with list of @GMT-YYYY.MM.DD-HH.MM.SS timestamps
    ///   Client: user selects a version
    ///   Client: opens \\server\share\@GMT-2026.05.10-12.00.00\path\to\file.txt
    ///   Server: resolves @GMT- prefix → reads version from FileVersionService
    /// </summary>
    public static class SmbSnapshotHandler
    {
        /// <summary>
        /// FSCTL code for enumerating snapshots. Windows sends this to discover
        /// available "Previous Versions" on a share.
        /// </summary>
        public const uint FSCTL_SRV_ENUMERATE_SNAPSHOTS = 0x00144064;

        /// <summary>
        /// Handles the FSCTL_SRV_ENUMERATE_SNAPSHOTS IOCTL.
        /// Returns the response buffer that SMBLibrary will send to the client.
        /// 
        /// Response format (MS-SMB2 2.2.32.2):
        ///   NumberOfSnapshots (4 bytes, uint32)
        ///   NumberOfSnapshotsReturned (4 bytes, uint32)  
        ///   SnapshotArraySize (4 bytes, uint32) size in bytes of the string array
        ///   SnapshotArray null-terminated Unicode strings + final null terminator
        /// </summary>
        public static byte[] BuildEnumerateSnapshotsResponse(List<DateTime> timestamps)
        {
            // Build the snapshot strings: @GMT-YYYY.MM.DD-HH.MM.SS\0
            var snapshotStrings = new List<string>();
            foreach (var ts in timestamps.OrderByDescending(t => t))
            {
                snapshotStrings.Add(ts.ToString("'@GMT-'yyyy.MM.dd-HH.mm.ss"));
            }

            // Calculate the snapshot array: each string is null-terminated Unicode,
            // plus a final null terminator for the array itself
            var arrayBuilder = new StringBuilder();
            foreach (var s in snapshotStrings)
            {
                arrayBuilder.Append(s);
                arrayBuilder.Append('\0');
            }
            arrayBuilder.Append('\0'); // final array terminator

            var arrayBytes = Encoding.Unicode.GetBytes(arrayBuilder.ToString());

            // Build response: 3x uint32 header + snapshot array
            var response = new byte[12 + arrayBytes.Length];
            BitConverter.GetBytes((uint)timestamps.Count).CopyTo(response, 0);         // NumberOfSnapshots
            BitConverter.GetBytes((uint)timestamps.Count).CopyTo(response, 4);          // NumberOfSnapshotsReturned
            BitConverter.GetBytes((uint)arrayBytes.Length).CopyTo(response, 8);          // SnapshotArraySize
            arrayBytes.CopyTo(response, 12);

            return response;
        }

        /// <summary>
        /// Checks if a path contains an @GMT- snapshot prefix.
        /// </summary>
        public static bool IsSnapshotPath(string path)
        {
            return path.Contains("@GMT-");
        }

        /// <summary>
        /// Parses a snapshot path into the snapshot timestamp and the real file path.
        /// 
        /// Input:  "@GMT-2026.05.10-12.00.00\docs\report.docx"
        /// Output: (2026-05-10T12:00:00Z, "docs\report.docx")
        /// 
        /// Input:  "@GMT-2026.05.10-12.00.00"  (just the snapshot root)
        /// Output: (2026-05-10T12:00:00Z, "")
        /// 
        /// Returns null if the @GMT- token can't be parsed.
        /// </summary>
        public static SnapshotPathInfo? ParseSnapshotPath(string path)
        {
            // Normalize separators
            path = path.Replace('/', '\\');

            // Find the @GMT- token it's always exactly 24 chars: @GMT-YYYY.MM.DD-HH.MM.SS
            int gmtIndex = path.IndexOf("@GMT-", StringComparison.OrdinalIgnoreCase);
            if (gmtIndex < 0)
                return null;

            const int gmtTokenLength = 24; // "@GMT-YYYY.MM.DD-HH.MM.SS"
            if (gmtIndex + gmtTokenLength > path.Length)
                return null;

            var gmtToken = path.Substring(gmtIndex, gmtTokenLength);
            var timestamp = FileVersion.ParseGmtToken(gmtToken);
            if (timestamp == null)
                return null;

            // Everything after the @GMT- token (and optional separator) is the real path
            var remaining = "";
            var afterToken = gmtIndex + gmtTokenLength;
            if (afterToken < path.Length)
            {
                remaining = path.Substring(afterToken).TrimStart('\\', '/');
            }

            // Everything before the @GMT- token is a prefix (usually empty or share-relative)
            var prefix = "";
            if (gmtIndex > 0)
            {
                prefix = path.Substring(0, gmtIndex).TrimEnd('\\', '/');
            }

            return new SnapshotPathInfo
            {
                SnapshotTimestamp = timestamp.Value,
                RealPath = remaining,
                Prefix = prefix,
                GmtToken = gmtToken
            };
        }
    }

    /// <summary>
    /// Parsed result of a @GMT- snapshot path.
    /// </summary>
    public class SnapshotPathInfo
    {
        /// <summary>UTC timestamp of the requested snapshot.</summary>
        public DateTime SnapshotTimestamp { get; set; }

        /// <summary>The actual file/directory path after the @GMT- token.</summary>
        public string RealPath { get; set; } = "";

        /// <summary>Any path prefix before the @GMT- token (usually empty).</summary>
        public string Prefix { get; set; } = "";

        /// <summary>The raw @GMT- token string.</summary>
        public string GmtToken { get; set; } = "";
    }
}