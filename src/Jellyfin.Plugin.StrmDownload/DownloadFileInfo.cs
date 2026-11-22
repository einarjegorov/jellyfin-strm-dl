using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.StrmDownload;

internal static class DownloadFileInfo
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    internal static void SetHeaders(HttpResponse response, HttpResponseMessage upstream, string originalName)
    {
        var disposition = upstream.Content.Headers.ContentDisposition;
        var name = CleanName(disposition?.FileNameStar ?? disposition?.FileName);
        var contentType = upstream.Content.Headers.ContentType?.ToString();
        var mediaType = upstream.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrEmpty(name))
        {
            var pathName = CleanName(upstream.RequestMessage?.RequestUri?.LocalPath);
            // URLs often end in opaque IDs. Use the library title instead of that ID.
            if (pathName is not null && ContentTypes.TryGetContentType(pathName, out _))
                name = pathName;
        }
        if (string.IsNullOrEmpty(name) || Path.GetExtension(name).Equals(".strm", StringComparison.OrdinalIgnoreCase))
        {
            var extension = mediaType?.ToLowerInvariant() switch
            {
                "video/x-matroska" => ".mkv",
                "video/mp4" or "application/mp4" => ".mp4",
                "video/webm" => ".webm",
                "video/mp2t" => ".ts",
                "video/quicktime" => ".mov",
                "video/x-msvideo" => ".avi",
                "audio/mpeg" => ".mp3",
                "audio/flac" or "audio/x-flac" => ".flac",
                "audio/mp4" => ".m4a",
                "audio/ogg" or "application/ogg" => ".ogg",
                _ => ".bin"
            };
            name = (CleanName(Path.GetFileNameWithoutExtension(originalName)) ?? "download") + extension;
        }
        if ((string.IsNullOrEmpty(contentType) || mediaType == "application/octet-stream")
            && ContentTypes.TryGetContentType(name, out var inferredType))
            contentType = inferredType;

        var attachment = new ContentDispositionHeaderValue("attachment");
        attachment.SetHttpFileName(name);
        response.Headers.ContentDisposition = attachment.ToString();
        response.ContentType = contentType ?? "application/octet-stream";
        response.ContentLength = upstream.Content.Headers.ContentLength;
        if (upstream.Content.Headers.ContentEncoding.Count > 0)
            response.Headers.ContentEncoding = upstream.Content.Headers.ContentEncoding.ToArray();
    }

    private static string? CleanName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var leaf = value.Trim('"').Replace('\\', '/').Split('/')[^1];
        var clean = new string(leaf.Where(c => !char.IsControl(c) && !"<>:\"|?*".Contains(c)).ToArray()).Trim(' ', '.');
        if (clean.Length == 0)
            return null;
        return clean.Length <= 200 ? clean : clean[^200..];
    }
}
