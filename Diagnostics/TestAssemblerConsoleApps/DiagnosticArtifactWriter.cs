using System.Text.Json;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed class DiagnosticArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true
    };

    public DiagnosticArtifactWriter(string artifactDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        ArtifactDirectory = artifactDirectory;
    }

    public string ArtifactDirectory { get; }

    public void EnsureDirectory()
    {
        Directory.CreateDirectory(ArtifactDirectory);
    }

    public string GetPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return Path.Combine(ArtifactDirectory, fileName);
    }

    public void WriteManifest(DiagnosticArtifactManifest manifest)
    {
        WriteJson("manifest.json", manifest);
    }

    public DiagnosticArtifactManifest? TryReadManifest()
    {
        return TryReadJson<DiagnosticArtifactManifest>("manifest.json");
    }

    public void WriteJson<T>(string fileName, T value)
    {
        string path = GetPath(fileName);
        string json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(path, json);
    }

    public T? TryReadJson<T>(string fileName)
    {
        string path = GetPath(fileName);
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public void WriteText(string fileName, string content)
    {
        string path = GetPath(fileName);
        File.WriteAllText(path, content);
    }
}
