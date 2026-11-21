namespace NaxUpdater.Core.Services;

internal static class PeArchitectureReader
{
    public static string? Detect(string? executablePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath) ||
                !Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            using var stream = File.OpenRead(executablePath);
            if (stream.Length < 0x40)
            {
                return null;
            }
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5A4D)
            {
                return null;
            }
            stream.Position = 0x3c;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > stream.Length - 6)
            {
                return null;
            }
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550)
            {
                return null;
            }
            return reader.ReadUInt16() switch
            {
                0x8664 => "x64",
                0xAA64 => "arm64",
                0x014c => "x86",
                _ => null
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or EndOfStreamException)
        {
            return null;
        }
    }
}
