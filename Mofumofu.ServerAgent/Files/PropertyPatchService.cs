using System.Globalization;
using System.Text;
using Mofumofu.RemoteProtocol;

namespace Mofumofu.ServerAgent.Files;

public sealed class PropertyPatchService
{
    private static readonly HashSet<string> BooleanKeys = new(StringComparer.Ordinal)
    {
        "online-mode", "white-list", "enforce-whitelist", "pvp", "allow-flight", "spawn-monsters",
        "spawn-animals", "spawn-npcs", "generate-structures", "allow-nether", "enable-command-block",
        "enable-query", "enable-rcon", "enable-jmx-monitoring", "sync-chunk-writes", "use-native-transport",
        "prevent-proxy-connections", "hardcore", "broadcast-console-to-ops", "broadcast-rcon-to-ops",
        "log-ips", "require-resource-pack", "debug", "enable-status", "hide-online-players",
    };
    private static readonly Dictionary<string, (int Minimum, int Maximum)> IntegerKeys = new(StringComparer.Ordinal)
    {
        ["server-port"] = (1, 65535), ["query.port"] = (1, 65535), ["rcon.port"] = (1, 65535),
        ["max-players"] = (0, 100000), ["view-distance"] = (3, 32), ["simulation-distance"] = (3, 32),
        ["spawn-protection"] = (0, 1000000), ["op-permission-level"] = (1, 4),
        ["function-permission-level"] = (1, 4), ["player-idle-timeout"] = (0, int.MaxValue),
        ["rate-limit"] = (0, int.MaxValue), ["max-tick-time"] = (-1, int.MaxValue),
        ["network-compression-threshold"] = (-1, int.MaxValue), ["max-world-size"] = (1, 29999984),
    };
    private static readonly HashSet<string> KnownKeys = new(BooleanKeys, StringComparer.Ordinal);

    private readonly SafeFileService _files;
    private readonly IClock _clock;
    private bool _restartRequired;

    static PropertyPatchService()
    {
        KnownKeys.UnionWith(IntegerKeys.Keys);
        KnownKeys.Add("difficulty");
        KnownKeys.Add("gamemode");
        KnownKeys.UnionWith(new[]
        {
            "motd", "level-name", "level-seed", "level-type", "resource-pack", "resource-pack-sha1",
            "resource-pack-id", "server-ip", "initial-enabled-packs", "initial-disabled-packs",
            "generator-settings", "region-file-compression", "management-server-host",
        });
    }

    public PropertyPatchService(SafeFileService files, IClock clock)
    {
        _files = files;
        _clock = clock;
    }

    public async Task<ServerPropertiesResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        PropertyDocument document = await _files.ReadPropertiesAsync(cancellationToken);
        Dictionary<string, string> properties = Parse(document.Content).Values;
        return new ServerPropertiesResponse
        {
            Revision = document.Revision,
            RestartRequired = _restartRequired,
            Properties = WithoutSecrets(properties),
        };
    }

    public async Task<ServerPropertiesPatchResponse> PatchAsync(string expectedRevision,
        IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken = default)
    {
        if (changes is null || changes.Count == 0)
        {
            throw ApiException.InvalidRequest("At least one property change is required.");
        }

        PropertyDocument document = await _files.ReadPropertiesAsync(cancellationToken);
        if (!string.Equals(document.Revision, expectedRevision, StringComparison.Ordinal))
        {
            throw ApiException.RevisionConflict(expectedRevision ?? string.Empty);
        }

        ParsedProperties parsed = Parse(document.Content);
        Dictionary<string, string> updated = new(parsed.Values, StringComparer.Ordinal);
        foreach ((string rawKey, string? value) in changes)
        {
            string key = ValidateKey(rawKey);
            if (key.Equals("rcon.password", StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiException(StatusCodes.Status403Forbidden, ProtocolErrors.Forbidden,
                    "rcon.password is neither readable nor remotely mutable.");
            }

            if (value is null)
            {
                if (KnownKeys.Contains(key))
                {
                    throw ApiException.InvalidRequest($"The known server property '{key}' cannot be removed.");
                }

                updated.Remove(key);
                continue;
            }

            ValidateValue(key, value);
            updated[key] = value;
        }

        string content = Serialize(parsed.Lines, updated);
        string revision = await _files.WritePropertiesAsync(content, expectedRevision, cancellationToken);
        _restartRequired = true;
        return new ServerPropertiesPatchResponse
        {
            Revision = revision,
            RestartRequired = true,
            AppliedAt = _clock.UtcNow,
            Properties = WithoutSecrets(updated),
        };
    }

    public void NotifyServerStarted() => _restartRequired = false;

    public static Dictionary<string, string> AuditChanges(IReadOnlyDictionary<string, string?> changes)
    {
        string[] set = changes.Where(pair => pair.Value is not null).Select(pair => pair.Key)
            .OrderBy(key => key, StringComparer.Ordinal).ToArray();
        string[] removed = changes.Where(pair => pair.Value is null).Select(pair => pair.Key)
            .OrderBy(key => key, StringComparer.Ordinal).ToArray();
        return new Dictionary<string, string>
        {
            ["setKeys"] = string.Join(',', set),
            ["removedKeys"] = string.Join(',', removed),
            ["values"] = "redacted",
        };
    }

    private static string ValidateKey(string? rawKey)
    {
        string key = rawKey?.Trim() ?? string.Empty;
        if (key.Length is 0 or > 128 || key.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) ||
            c is '=' or '\\' or '/'))
        {
            throw ApiException.InvalidRequest("A property key is invalid.");
        }

        return key;
    }

    private static void ValidateValue(string key, string value)
    {
        if (value.Length > 4096 || value.Any(char.IsControl))
        {
            throw ApiException.InvalidRequest($"The value for '{key}' is invalid.");
        }

        if (BooleanKeys.Contains(key) && value is not ("true" or "false"))
        {
            throw ApiException.InvalidRequest($"The value for '{key}' must be 'true' or 'false'.");
        }

        if (IntegerKeys.TryGetValue(key, out (int Minimum, int Maximum) range) &&
            (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number) ||
             number < range.Minimum || number > range.Maximum))
        {
            throw ApiException.InvalidRequest($"The value for '{key}' is outside its valid range.");
        }

        if (key == "difficulty" && value is not ("peaceful" or "easy" or "normal" or "hard") ||
            key == "gamemode" && value is not ("survival" or "creative" or "adventure" or "spectator"))
        {
            throw ApiException.InvalidRequest($"The value for '{key}' is invalid.");
        }
    }

    private static Dictionary<string, string> WithoutSecrets(Dictionary<string, string> properties)
    {
        return properties.Where(pair => !pair.Key.Equals("rcon.password", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static ParsedProperties Parse(string content)
    {
        List<PropertyLine> lines = [];
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string raw in content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n'))
        {
            string trimmed = raw.Trim();
            int equals = trimmed.IndexOf('=');
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || equals < 0)
            {
                lines.Add(new PropertyLine(null, raw));
                continue;
            }

            string key = trimmed[..equals].Trim();
            string value = trimmed[(equals + 1)..];
            lines.Add(new PropertyLine(key, raw));
            values[key] = value;
        }

        return new ParsedProperties(lines, values);
    }

    private static string Serialize(IEnumerable<PropertyLine> lines, Dictionary<string, string> values)
    {
        HashSet<string> written = new(StringComparer.Ordinal);
        StringBuilder output = new();
        foreach (PropertyLine line in lines)
        {
            if (line.Key is null)
            {
                if (line.Raw.Length > 0)
                {
                    output.Append(line.Raw).Append('\n');
                }
                continue;
            }

            if (!written.Add(line.Key))
            {
                continue;
            }

            if (values.TryGetValue(line.Key, out string? value))
            {
                output.Append(line.Key).Append('=').Append(value).Append('\n');
            }
        }

        foreach ((string key, string value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (written.Add(key))
            {
                output.Append(key).Append('=').Append(value).Append('\n');
            }
        }

        return output.ToString();
    }

    private sealed record PropertyLine(string? Key, string Raw);
    private sealed record ParsedProperties(List<PropertyLine> Lines, Dictionary<string, string> Values);
}
