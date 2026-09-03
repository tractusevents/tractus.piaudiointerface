using System.Text.Json;

namespace PiUsbAudio.Control;

public sealed class ConfigStore(string path)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public async Task<RouterConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadUnlockedAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        RouterConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await SaveUnlockedAsync(configuration, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RouterConfiguration> UpdateAsync(
        Action<RouterConfiguration> update,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var configuration = await LoadUnlockedAsync(cancellationToken);
            update(configuration);
            await SaveUnlockedAsync(configuration, cancellationToken);
            return configuration;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RouterConfiguration> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path);
        var configuration = await JsonSerializer.DeserializeAsync<RouterConfiguration>(
            stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"Configuration {Path} is empty");
        configuration.Normalize();
        return configuration;
    }

    private async Task SaveUnlockedAsync(
        RouterConfiguration configuration,
        CancellationToken cancellationToken)
    {
        configuration.Normalize();
        var errors = configuration.Validate();
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join("; ", errors));
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("Configuration path has no parent directory");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporaryPath, Path, overwrite: true);
    }
}
