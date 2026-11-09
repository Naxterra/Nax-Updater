using System.Text;

namespace NaxUpdater.Core.Services;

// Version pages, feeds and API answers are small documents. Reading them through this limit keeps
// a server that sends no Content-Length and an endless or very large body from filling the memory.
internal static class HttpBodyLimit
{
    public const int DocumentBytes = 16 * 1024 * 1024;

    public static async Task<byte[]> ReadBytesAsync(HttpContent content, CancellationToken token, int maxBytes = DocumentBytes)
    {
        if (content.Headers.ContentLength > maxBytes) throw TooLarge(maxBytes);
        await using var stream = await content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw TooLarge(maxBytes);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public static async Task<string> ReadStringAsync(HttpContent content, CancellationToken token, int maxBytes = DocumentBytes)
    {
        var bytes = await ReadBytesAsync(content, token, maxBytes);
        var encoding = Encoding.UTF8;
        if (content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
        {
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { } // An unknown charset name is read as UTF-8.
        }
        using var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static async Task<Stream> ReadStreamAsync(HttpContent content, CancellationToken token, int maxBytes = DocumentBytes) =>
        new MemoryStream(await ReadBytesAsync(content, token, maxBytes), writable: false);

    public static async Task<string> GetStringAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        return await ReadStringAsync(response.Content, token);
    }

    private static InvalidDataException TooLarge(int maxBytes) =>
        new($"The server's answer is larger than {maxBytes / (1024 * 1024)} MB and was not read.");
}
