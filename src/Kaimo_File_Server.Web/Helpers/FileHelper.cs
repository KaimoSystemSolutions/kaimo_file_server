namespace Kaimo_File_Server.Web.Helpers;

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
        [".json"] = "application/json",
        [".xml"]  = "application/xml",
        [".html"] = "text/html",
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

    public static string GetIcon(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return IconMap.GetValueOrDefault(ext, "file.svg");
    }

    public static string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLower();
        return ContentTypeMap.GetValueOrDefault(ext, "application/octet-stream");
    }
}