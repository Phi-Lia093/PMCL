using System.Text.Json;
using System.Text.Json.Serialization;
using PMCL.Models;

namespace PMCL.Services;

/// <summary>
/// Persists the launcher's saved accounts in <c>%APPDATA%\PMCL\accounts.json</c>.
/// The file uses PMCL's own schema; the old CmlLib account file is migrated
/// best-effort and kept as <c>accounts.legacy.json</c>.
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private readonly List<SavedAccount> _accounts = new();

    public AccountStore(AppPaths paths)
    {
        _paths = paths;
        Load();
    }

    public IReadOnlyList<SavedAccount> GetAccounts()
    {
        lock (_gate)
        {
            return _accounts
                .OrderByDescending(account => account.LastAccess)
                .ThenBy(account => account.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public SavedAccount? Find(string id)
    {
        lock (_gate)
        {
            return _accounts.FirstOrDefault(account =>
                string.Equals(account.Id, id, StringComparison.Ordinal));
        }
    }

    public SavedAccount Upsert(SavedAccount account)
    {
        lock (_gate)
        {
            var existing = _accounts.FirstOrDefault(candidate =>
                               string.Equals(candidate.Id, account.Id, StringComparison.Ordinal))
                           ?? _accounts.FirstOrDefault(candidate => IsSameAccount(candidate, account));

            if (existing is null)
            {
                _accounts.Add(account);
                existing = account;
            }
            else
            {
                CopyValues(account, existing);
            }

            SaveCore();
            return existing;
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var removed = _accounts.RemoveAll(account =>
                string.Equals(account.Id, id, StringComparison.Ordinal)) > 0;

            if (removed)
                SaveCore();
            return removed;
        }
    }

    public bool MarkUsed(string id, DateTimeOffset usedAt)
    {
        lock (_gate)
        {
            var account = _accounts.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (account is null)
                return false;

            account.LastAccess = usedAt;
            SaveCore();
            return true;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            SaveCore();
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            _accounts.Clear();
            if (!File.Exists(_paths.AccountsFile))
                return;

            string json;
            try
            {
                json = File.ReadAllText(_paths.AccountsFile);
            }
            catch
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && TryGetProperty(root, "accounts", out var accountsElement)
                    && accountsElement.ValueKind == JsonValueKind.Array)
                {
                    var stored = JsonSerializer.Deserialize<AccountStoreDocument>(json, JsonOptions);
                    if (stored?.Accounts is not null)
                        _accounts.AddRange(stored.Accounts);
                }
                else if (TryMigrateLegacy(root, out var migrated))
                {
                    _accounts.AddRange(migrated);
                    BackupLegacyFile();
                    NormalizeAccounts();
                    SaveCore();
                    return;
                }
            }
            catch
            {
                // Never let a broken account file prevent the launcher from starting.
                BackupCorruptFile();
            }

            NormalizeAccounts();
        }
    }

    private void SaveCore()
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            var document = new AccountStoreDocument
            {
                Version = 1,
                Accounts = _accounts.ToList()
            };
            var json = JsonSerializer.Serialize(document, JsonOptions);
            var tempFile = _paths.AccountsFile + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, _paths.AccountsFile, overwrite: true);
        }
        catch
        {
            // Account persistence is best-effort; the in-memory list remains usable.
        }
    }

    private void NormalizeAccounts()
    {
        var normalized = new List<SavedAccount>();
        foreach (var account in _accounts)
        {
            if (account is null)
                continue;

            account.Id = string.IsNullOrWhiteSpace(account.Id)
                ? Guid.NewGuid().ToString("N")
                : account.Id.Trim();
            account.Username = account.Username?.Trim() ?? string.Empty;
            account.Uuid = account.Uuid?.Trim() ?? string.Empty;
            account.Gamertag = account.Gamertag?.Trim() ?? string.Empty;
            account.Xuid = account.Xuid?.Trim() ?? string.Empty;
            account.RefreshToken = account.RefreshToken?.Trim() ?? string.Empty;
            account.MinecraftAccessToken = account.MinecraftAccessToken?.Trim() ?? string.Empty;

            if (!Enum.IsDefined(account.Kind))
                account.Kind = AccountKind.Offline;

            if (account.Kind == AccountKind.Offline)
            {
                if (string.IsNullOrWhiteSpace(account.Username))
                    continue;

                if (string.IsNullOrWhiteSpace(account.Uuid))
                    account.Uuid = SavedAccount.CreateOfflineUuid(account.Username);
            }
            else if (string.IsNullOrWhiteSpace(account.RefreshToken)
                     && string.IsNullOrWhiteSpace(account.MinecraftAccessToken))
            {
                continue;
            }

            var duplicate = normalized.FirstOrDefault(candidate => IsSameAccount(candidate, account));
            if (duplicate is null)
            {
                normalized.Add(account);
                continue;
            }

            if (account.LastAccess > duplicate.LastAccess)
                CopyValues(account, duplicate);
        }

        _accounts.Clear();
        _accounts.AddRange(normalized);
    }

    private static bool IsSameAccount(SavedAccount left, SavedAccount right)
    {
        if (left.Kind != right.Kind)
            return false;

        if (left.Kind == AccountKind.Microsoft)
        {
            if (!string.IsNullOrWhiteSpace(left.Uuid)
                && string.Equals(left.Uuid, right.Uuid, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(left.RefreshToken)
                   && string.Equals(left.RefreshToken, right.RefreshToken, StringComparison.Ordinal);
        }

        return !string.IsNullOrWhiteSpace(left.Username)
               && string.Equals(left.Username, right.Username, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyValues(SavedAccount source, SavedAccount destination)
    {
        destination.Kind = source.Kind;
        destination.Username = source.Username;
        destination.Uuid = source.Uuid;
        destination.Gamertag = source.Gamertag;
        destination.Xuid = source.Xuid;
        destination.RefreshToken = source.RefreshToken;
        destination.MinecraftAccessToken = source.MinecraftAccessToken;
        destination.MinecraftTokenExpiresAt = source.MinecraftTokenExpiresAt;
        destination.LastAccess = source.LastAccess;
    }

    private void BackupLegacyFile()
    {
        try
        {
            if (File.Exists(_paths.AccountsFile)
                && !File.Exists(_paths.LegacyAccountsBackupFile))
            {
                File.Copy(_paths.AccountsFile, _paths.LegacyAccountsBackupFile);
            }
        }
        catch
        {
            // Migration still works even if the backup cannot be written.
        }
    }

    private void BackupCorruptFile()
    {
        try
        {
            if (!File.Exists(_paths.AccountsFile))
                return;

            var backup = Path.Combine(
                _paths.DataDirectory,
                $"accounts.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(_paths.AccountsFile, backup);
        }
        catch
        {
            // Ignore backup failures.
        }
    }

    /// <summary>
    /// Reads the JSON object written by CmlLib's JsonXboxGameAccountManager.
    /// Each root property is a UUID and contains MicrosoftOAuth, JEProfile and
    /// JEToken session values.
    /// </summary>
    private static bool TryMigrateLegacy(JsonElement root, out List<SavedAccount> accounts)
    {
        accounts = new List<SavedAccount>();
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;

            var session = property.Value;
            if (!TryGetProperty(session, "MicrosoftOAuth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var refreshToken = FindFirstString(oauth, "RefreshToken", "RawRefreshToken");
            if (string.IsNullOrWhiteSpace(refreshToken))
                continue;

            var uuid = property.Name;
            var username = string.Empty;
            if (TryGetProperty(session, "JEProfile", out var profile)
                && profile.ValueKind == JsonValueKind.Object)
            {
                username = FindFirstString(profile, "Username") ?? string.Empty;
                uuid = FindFirstString(profile, "UUID") ?? uuid;
            }

            var minecraftAccessToken = string.Empty;
            DateTimeOffset? minecraftExpiresAt = null;
            if (TryGetProperty(session, "JEToken", out var jeToken)
                && jeToken.ValueKind == JsonValueKind.Object)
            {
                minecraftAccessToken = FindFirstString(jeToken, "AccessToken") ?? string.Empty;
                username = string.IsNullOrWhiteSpace(username)
                    ? FindFirstString(jeToken, "Username") ?? string.Empty
                    : username;
                minecraftExpiresAt = FindDateTimeOffset(jeToken, "ExpiresOn");
            }

            var gamertag = FindFirstString(session, "Gamertag", "GamertagName") ?? string.Empty;
            var xuid = FindFirstString(session, "XboxUserId", "Xuid") ?? string.Empty;

            accounts.Add(new SavedAccount
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = AccountKind.Microsoft,
                Username = username,
                Uuid = uuid,
                Gamertag = gamertag,
                Xuid = xuid,
                RefreshToken = refreshToken,
                MinecraftAccessToken = minecraftAccessToken,
                MinecraftTokenExpiresAt = minecraftExpiresAt,
                LastAccess = DateTimeOffset.Now
            });
        }

        return accounts.Count > 0;
    }

    private static string? FindFirstString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var nested = FindFirstString(property.Value, names);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    var nested = FindFirstString(item, names);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? FindDateTimeOffset(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var unixSeconds)
            && unixSeconds is > 0 and < 4102444800)
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed class AccountStoreDocument
    {
        public int Version { get; set; } = 1;

        public List<SavedAccount> Accounts { get; set; } = new();
    }
}
