using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;

namespace InterviewCoach.Infrastructure.Settings;

/// <summary>
/// settings.json under %LOCALAPPDATA%\InterviewCoach. API keys are encrypted with DPAPI (CurrentUser scope)
/// and written as "dpapi:&lt;base64&gt;"; everything else is plain JSON.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private const string Prefix = "dpapi:";
    private static readonly string[] SecretProperties =
        [nameof(AppSettings.AnthropicApiKey), nameof(AppSettings.OpenAiApiKey), nameof(AppSettings.AzureSpeechKey)];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private AppSettings _current;

    public SettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        _current = Load();
    }

    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InterviewCoach");

    public static string DefaultPath => Path.Combine(AppDataDirectory, "settings.json");

    public string FilePath => _path;

    public event EventHandler? Changed;

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public void Save(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, Json)!.AsObject();
        foreach (var name in SecretProperties)
        {
            if (node[name] is JsonValue v && v.TryGetValue<string>(out var plain) && !string.IsNullOrEmpty(plain))
                node[name] = Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, node.ToJsonString(Json));
        File.Move(tmp, _path, overwrite: true);

        lock (_gate) _current = settings.Clone();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(_path))?.AsObject();
            if (node is null) return new AppSettings();
            foreach (var name in SecretProperties)
            {
                if (node[name] is JsonValue v && v.TryGetValue<string>(out var stored) && stored.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    try
                    {
                        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), null, DataProtectionScope.CurrentUser);
                        node[name] = Encoding.UTF8.GetString(bytes);
                    }
                    catch (CryptographicException)
                    {
                        node[name] = null; // encrypted by another user/machine; the user will have to re-enter it
                    }
                }
            }
            return node.Deserialize<AppSettings>(Json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings(); // corrupt file: start over rather than refusing to launch
        }
    }
}
