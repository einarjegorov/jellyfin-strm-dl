using System.Text;

namespace Jellyfin.Plugin.StrmDownload;

internal static class StrmPointer
{
    internal const int MaxBytes = 64 * 1024;

    internal static async Task<Uri> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[MaxBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await file.ReadAsync(bytes.AsMemory(count), cancellationToken);
            if (read == 0)
                break;
            count += read;
        }
        if (count > MaxBytes)
            throw new InvalidDataException("STRM file is too large.");

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes, 0, count).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("STRM must use UTF-8.");
        }

        using var reader = new StringReader(text);
        Uri? source = null;
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (source is not null || !Uri.TryCreate(line, UriKind.Absolute, out source) || !IsHttp(source))
                throw new InvalidDataException("STRM must contain one HTTP(S) URL.");
        }
        return source ?? throw new InvalidDataException("STRM is empty.");
    }

    internal static bool IsHttp(Uri uri) => uri.Scheme is "http" or "https";
}
