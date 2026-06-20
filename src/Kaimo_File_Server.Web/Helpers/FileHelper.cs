namespace Kaimo_File_Server.Web.Helpers;

/// <summary>
/// How a file should be rendered in the inline preview dialog.
/// Anything that maps to <see cref="Unsupported"/> gets the download fallback.
/// </summary>
public enum PreviewKind
{
    Image,
    Pdf,
    Video,
    Audio,
    /// <summary>Plain text / source code — rendered as escaped, preformatted text.</summary>
    Text,
    /// <summary>Word .docx — converted to HTML client-side via mammoth.js.</summary>
    Docx,
    /// <summary>No inline preview available (e.g. pptx, xlsx, binaries) — download only.</summary>
    Unsupported,
}

public class FileHelper
{
    private static readonly Dictionary<string, string> IconMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Documents
        [".pdf"]  = "file_pdf.svg",
        [".doc"]  = "file_word.svg",
        [".docx"] = "file_word.svg",
        [".xls"]  = "file_excel.svg",
        [".xlsx"] = "file_excel.svg",
        [".ppt"]  = "file_powerpoint.svg",
        [".pptx"] = "file_powerpoint.svg",
        [".txt"]  = "file_text.svg",
        [".md"]   = "file_text.svg",
        [".csv"]  = "file_excel.svg",
        [".rtf"]  = "file_text.svg",

        // Images
        [".png"]  = "file_image.svg",
        [".jpg"]  = "file_image.svg",
        [".jpeg"] = "file_image.svg",
        [".gif"]  = "file_image.svg",
        [".webp"] = "file_image.svg",
        [".svg"]  = "file_image.svg",
        [".bmp"]  = "file_image.svg",
        [".ico"]  = "file_image.svg",

        // Video
        [".mp4"]  = "file_video.svg",
        [".webm"] = "file_video.svg",
        [".avi"]  = "file_video.svg",
        [".mkv"]  = "file_video.svg",
        [".mov"]  = "file_video.svg",
        [".wmv"]  = "file_video.svg",

        // Audio
        [".mp3"]  = "file_audio.svg",
        [".wav"]  = "file_audio.svg",
        [".flac"] = "file_audio.svg",
        [".ogg"]  = "file_audio.svg",
        [".aac"]  = "file_audio.svg",
        [".wma"]  = "file_audio.svg",

        // Archives
        [".zip"]  = "file_zip.svg",
        [".tar"]  = "file_zip.svg",
        [".gz"]   = "file_zip.svg",
        [".7z"]   = "file_zip.svg",
        [".rar"]  = "file_zip.svg",
        [".bz2"]  = "file_zip.svg",

        // Code
        [".cs"]   = "file_code.svg",
        [".js"]   = "file_code.svg",
        [".ts"]   = "file_code.svg",
        [".py"]   = "file_code.svg",
        [".java"] = "file_code.svg",
        [".html"] = "file_code.svg",
        [".css"]  = "file_code.svg",
        [".json"] = "file_code.svg",
        [".xml"]  = "file_code.svg",
        [".yaml"] = "file_code.svg",
        [".yml"]  = "file_code.svg",
        [".sh"]   = "file_code.svg",
        [".sql"]  = "file_code.svg",

        // Executables
        [".exe"]  = "file_exe.svg",
        [".msi"]  = "file_exe.svg",
        [".deb"]  = "file_exe.svg",
        [".rpm"]  = "file_exe.svg",
        [".appimage"] = "file_exe.svg",
    };

    private static readonly Dictionary<string, string> ContentTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Documents
        [".pdf"]  = "application/pdf",
        [".doc"]  = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"]  = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"]  = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".txt"]  = "text/plain",
        [".md"]   = "text/markdown",
        [".csv"]  = "text/csv",
        [".rtf"]  = "application/rtf",
        [".json"] = "text/json",
        [".xml"]  = "text/xml",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"]  = "text/css",
        [".js"]   = "text/javascript",

        // Images
        [".png"]  = "image/png",
        [".jpg"]  = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"]  = "image/gif",
        [".webp"] = "image/webp",
        [".svg"]  = "image/svg+xml",
        [".bmp"]  = "image/bmp",
        [".ico"]  = "image/x-icon",

        // Video
        [".mp4"]  = "video/mp4",
        [".webm"] = "video/webm",
        [".avi"]  = "video/x-msvideo",
        [".mkv"]  = "video/x-matroska",
        [".mov"]  = "video/quicktime",
        [".wmv"]  = "video/x-ms-wmv",

        // Audio
        [".mp3"]  = "audio/mpeg",
        [".wav"]  = "audio/wav",
        [".flac"] = "audio/flac",
        [".ogg"]  = "audio/ogg",
        [".aac"]  = "audio/aac",
        [".wma"]  = "audio/x-ms-wma",

        // Archives
        [".zip"]  = "application/zip",
        [".tar"]  = "application/x-tar",
        [".gz"]   = "application/gzip",
        [".7z"]   = "application/x-7z-compressed",
        [".rar"]  = "application/vnd.rar",
        [".bz2"]  = "application/x-bzip2",
    };

    /// <summary>
    /// Extensions that are plain text / source code and can be shown as escaped
    /// preformatted text — even when the browser has no native viewer for them.
    /// </summary>
    private static readonly HashSet<string> TextPreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Plain text & data
        ".txt", ".text", ".md", ".markdown", ".rst", ".log", ".csv", ".tsv", ".rtf",
        // Config / data formats
        ".json", ".json5", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".config",
        ".properties", ".env", ".editorconfig", ".gitignore", ".gitattributes", ".dockerignore",
        ".resx", ".plist", ".csv",
        // Markup / web / project files
        ".html", ".htm", ".xml", ".xaml", ".axaml", ".xsd", ".xsl", ".xslt",
        ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".sln", ".slnx",
        ".css", ".scss", ".sass", ".less",
        // Shell / scripts
        ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".psd1", ".bat", ".cmd",
        // Source code
        ".cs", ".vb", ".fs", ".fsx", ".razor", ".cshtml", ".vbhtml",
        ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx",
        ".py", ".pyw", ".rb", ".php", ".pl", ".pm", ".lua", ".r",
        ".java", ".kt", ".kts", ".scala", ".groovy", ".gradle",
        ".c", ".h", ".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx", ".m", ".mm",
        ".go", ".rs", ".swift", ".dart",
        ".sql", ".graphql", ".gql", ".cmake",
    };

    /// <summary>
    /// Extensionless files (by exact name) that are still plain text.
    /// </summary>
    private static readonly HashSet<string> TextPreviewNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dockerfile", "makefile", "readme", "license", "licence", "changelog",
        ".gitignore", ".gitattributes", ".dockerignore", ".editorconfig", ".env",
    };

    private static readonly HashSet<string> ArchiveFileEndings = new()
    {

        ".zip",
        ".tar",
        ".gz",
        ".7z",
        ".rar",
        ".bz2",
    };
    
    public static string GetIcon(string fileName)
        => IconMap.GetValueOrDefault(getExt(fileName), "file.svg");
    

    public static string GetContentType(string fileName)
        => ContentTypeMap.GetValueOrDefault(getExt(fileName), "application/octet-stream");


    public static bool IsArchive(string fileName)
        => ArchiveFileEndings.Contains(getExt(fileName));

    /// <summary>True if the file can be shown as escaped, preformatted text.</summary>
    public static bool IsTextPreviewable(string fileName)
    {
        var ext = getExt(fileName);
        if (!string.IsNullOrEmpty(ext))
            return TextPreviewExtensions.Contains(ext);

        // Extensionless files (Dockerfile, Makefile, LICENSE, dotfiles …)
        return TextPreviewNames.Contains(Path.GetFileName(fileName));
    }

    /// <summary>
    /// Decides how a file should be rendered in the preview dialog. Media types
    /// are derived from the content type; source/text files fall back to the text
    /// renderer; .docx is handled client-side; everything else is download-only.
    /// </summary>
    public static PreviewKind GetPreviewKind(string fileName)
    {
        var contentType = GetContentType(fileName);

        if (contentType.StartsWith("image/")) return PreviewKind.Image;
        if (contentType == "application/pdf") return PreviewKind.Pdf;
        if (contentType.StartsWith("video/")) return PreviewKind.Video;
        if (contentType.StartsWith("audio/")) return PreviewKind.Audio;
        if (getExt(fileName) == ".docx") return PreviewKind.Docx;
        if (IsTextPreviewable(fileName)) return PreviewKind.Text;

        return PreviewKind.Unsupported;
    }

    private static string getExt(string fileName)
    {
        return Path.GetExtension(fileName).ToLower();
    }
}