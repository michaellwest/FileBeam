namespace FileBeam;

public static class MimeTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"]  = "text/plain",
        [".md"]   = "text/markdown",
        [".html"] = "text/html",
        [".css"]  = "text/css",
        [".js"]   = "application/javascript",
        [".json"] = "application/json",
        [".xml"]  = "application/xml",
        [".pdf"]  = "application/pdf",
        [".zip"]  = "application/zip",
        [".7z"]   = "application/x-7z-compressed",
        [".rar"]  = "application/vnd.rar",
        [".tar"]  = "application/x-tar",
        [".gz"]   = "application/gzip",
        [".png"]  = "image/png",
        [".jpg"]  = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"]  = "image/gif",
        [".webp"] = "image/webp",
        [".svg"]  = "image/svg+xml",
        [".mp4"]  = "video/mp4",
        [".mkv"]  = "video/x-matroska",
        [".avi"]  = "video/x-msvideo",
        [".mov"]  = "video/quicktime",
        [".mp3"]  = "audio/mpeg",
        [".wav"]  = "audio/wav",
        [".flac"] = "audio/flac",
        [".aac"]  = "audio/aac",
        [".exe"]  = "application/octet-stream",
        [".msi"]  = "application/octet-stream",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".csv"]  = "text/csv",
        [".log"]  = "text/plain",
    };

    public static string GetMimeType(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return Map.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";
    }
}
