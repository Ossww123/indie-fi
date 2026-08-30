using System.Text.Json;

namespace IndieFi.Seed;

internal static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<T> ReadAsync<T>(string path)
    {
        if (!File.Exists(path))
        {
            throw new ArgumentException($"JSON file does not exist: {path}");
        }
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options)
                ?? throw new ArgumentException($"JSON file is empty or invalid: {path}");
        }
        catch (JsonException)
        {
            throw new ArgumentException($"JSON file is invalid: {path}");
        }
    }

    public static async Task WriteNewAsync<T>(string path, T value)
    {
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"Refusing to overwrite an existing evidence file: {path}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, value, Options);
            await stream.WriteAsync("\n"u8.ToArray());
            await stream.FlushAsync();
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }
}
