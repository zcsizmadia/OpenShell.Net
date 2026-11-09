// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace OpenShell.Gateway;

/// <summary>Overrides for <see cref="GatewayLocator"/>, mainly for tests and containers.</summary>
public sealed record GatewayLocatorOptions
{
    /// <summary>Replaces the per-user <c>openshell</c> configuration directory.</summary>
    public string? UserConfigDirectory { get; init; }

    /// <summary>
    /// Replaces the system-wide configuration directory. Takes precedence over <c>$OPENSHELL_SYSTEM_GATEWAY_DIR</c>
    /// and the default <c>/etc/openshell</c> (none on Windows).
    /// </summary>
    public string? SystemConfigDirectory { get; init; }

    /// <summary>Environment lookup; defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    public Func<string, string?>? GetEnvironmentVariable { get; init; }
}

/// <summary>Finds gateways registered on disk by the OpenShell CLI.</summary>
public sealed class GatewayLocator
{
    internal const string MetadataFile = "metadata.json";
    private const string AppName = "openshell";
    private const string GatewaySubdir = "gateways";
    private const string ActiveGatewayFile = "active_gateway";
    private const string ActiveGatewayVariable = "OPENSHELL_GATEWAY";

    // Kept in sync with the OpenShell CLI, so the SDK sees the same gateway set.
    private const string SystemGatewayDirVariable = "OPENSHELL_SYSTEM_GATEWAY_DIR";

    private readonly GatewayLocatorOptions _options;
    private readonly Func<string, string?> _env;

    /// <summary>Creates a locator with the default OS layout.</summary>
    public GatewayLocator(GatewayLocatorOptions? options = null)
    {
        _options = options ?? new GatewayLocatorOptions();
        _env = _options.GetEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>The per-user configuration directory (<c>$XDG_CONFIG_HOME/openshell</c> or the OS default).</summary>
    public string UserConfigDirectory
    {
        get
        {
            if (_options.UserConfigDirectory is { Length: > 0 } overridden)
            {
                return overridden;
            }

            if (_env("XDG_CONFIG_HOME") is { Length: > 0 } xdg)
            {
                return Path.IsPathRooted(xdg)
                    ? Path.Join(xdg, AppName)
                    : throw new OpenShellException(OpenShellErrorKind.InvalidConfig, "XDG_CONFIG_HOME must be an absolute path");
            }

            if (OperatingSystem.IsWindows() && _env("APPDATA") is { Length: > 0 } appData)
            {
                return Path.Join(appData, AppName);
            }

            var home = _env(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            return string.IsNullOrEmpty(home)
                ? throw new OpenShellException(OpenShellErrorKind.InvalidConfig, "cannot determine home directory")
                : Path.Join(home, ".config", AppName);
        }
    }

    /// <summary>
    /// The system-wide configuration directory, or null when none applies: <see cref="GatewayLocatorOptions.SystemConfigDirectory"/>,
    /// else <c>$OPENSHELL_SYSTEM_GATEWAY_DIR</c> when it is an absolute path (empty or relative values are ignored, as the
    /// CLI does), else <c>/etc/openshell</c> (none on Windows).
    /// </summary>
    public string? SystemConfigDirectory =>
        _options.SystemConfigDirectory is { Length: > 0 } overridden
            ? overridden
            : _env(SystemGatewayDirVariable) is { Length: > 0 } fromEnv && Path.IsPathFullyQualified(fromEnv)
                ? fromEnv
                : OperatingSystem.IsWindows() ? null : "/etc/openshell";

    /// <summary>
    /// Loads a gateway by name; null selects the active gateway. A user registration (one with <c>metadata.json</c>)
    /// shadows a system one; credentials are always read from the user gateway directory
    /// (<see cref="GatewayMetadata.StateDirectory"/>).
    /// </summary>
    public GatewayMetadata Load(string? name = null)
    {
        name ??= ResolveActiveName();
        var (directory, source) = ResolveDirectory(name);
        return ParseMetadata(name, directory, source, UserGatewayDirectory(name));
    }

    /// <summary>
    /// Lists registered gateways sorted by name, as the OpenShell CLI does: a user directory counts only when it holds
    /// <c>metadata.json</c> (and then shadows a system registration of the same name), and registrations whose metadata
    /// cannot be read are skipped.
    /// </summary>
    public IReadOnlyList<GatewayEntry> List()
    {
        var active = TryResolveActiveName();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<GatewayEntry>();

        foreach (var name in ListDirectories(UserConfigDirectory))
        {
            var directory = Path.Join(UserConfigDirectory, GatewaySubdir, name);
            if (HasMetadata(directory) && seen.Add(name) && IsReadable(name, directory, GatewayConfigSource.User))
            {
                result.Add(new GatewayEntry(name, name == active, GatewayConfigSource.User));
            }
        }

        if (SystemConfigDirectory is { } system)
        {
            foreach (var name in ListDirectories(system))
            {
                var directory = Path.Join(system, GatewaySubdir, name);
                if (seen.Add(name) && IsReadable(name, directory, GatewayConfigSource.System))
                {
                    result.Add(new GatewayEntry(name, name == active, GatewayConfigSource.System));
                }
            }
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>
    /// Resolves the active gateway name: <c>$OPENSHELL_GATEWAY</c>, else the user <c>active_gateway</c> file, else the
    /// system <c>active_gateway</c> file. As in the OpenShell CLI, a missing, empty, or invalid file falls through to the
    /// next source.
    /// </summary>
    public string ResolveActiveName()
    {
        if (_env(ActiveGatewayVariable) is { Length: > 0 } fromEnv)
        {
            ValidateName(fromEnv);
            return fromEnv;
        }

        var problems = new List<string>();
        if (ReadActiveFile(Path.Join(UserConfigDirectory, ActiveGatewayFile), problems) is { } user)
        {
            return user;
        }

        if (SystemConfigDirectory is { } system && ReadActiveFile(Path.Join(system, ActiveGatewayFile), problems) is { } fromSystem)
        {
            return fromSystem;
        }

        throw new OpenShellException(
            OpenShellErrorKind.NotFound,
            problems.Count == 0 ? "no active gateway configured" : "no active gateway configured: " + string.Join("; ", problems));
    }

    /// <summary>
    /// Validates a gateway name as a portable, single path component, as the OpenShell CLI does: ASCII letters, digits,
    /// <c>-</c>, <c>_</c> and <c>.</c>, so host-derived FQDN and IPv4 names such as <c>gw.example.com</c> work. Rejects
    /// <c>.</c> and <c>..</c>, a leading or trailing dot, <c>..</c> anywhere, and Windows device names
    /// (<c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>COM0</c>-<c>COM9</c>, <c>LPT0</c>-<c>LPT9</c>, also before a
    /// dot, in any case) on every platform.
    /// </summary>
    public static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw Invalid("gateway name must not be empty");
        }

        if (name is "." or "..")
        {
            throw Invalid("gateway name is not a valid path component");
        }

        if (name.AsSpan().IndexOfAny('/', '\\') >= 0)
        {
            throw Invalid("gateway name must not contain path separators");
        }

        if (name.Contains('\0', StringComparison.Ordinal))
        {
            throw Invalid("gateway name must not contain NUL");
        }

        if (name.StartsWith('.') || name.EndsWith('.') || name.Contains("..", StringComparison.Ordinal))
        {
            throw Invalid("gateway name has an invalid dot placement");
        }

        // ASCII only: excludes Windows reserved characters (including ':' in raw IPv6 addresses), spaces, and controls.
        foreach (var c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.'))
            {
                throw Invalid("gateway name contains a non-portable character");
            }
        }

        // Windows treats device names as reserved even with an extension (CON.txt, COM1.example.com).
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? name : name[..dot]).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3])))
        {
            throw Invalid("gateway name is a reserved Windows device name");
        }
    }

    private string? TryResolveActiveName()
    {
        try
        {
            return ResolveActiveName();
        }
        catch (OpenShellException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a gateway name from an <c>active_gateway</c> file; null, with the reason added to
    /// <paramref name="problems"/> when the file exists, if it is missing, unreadable, empty, or invalid.
    /// </summary>
    private static string? ReadActiveFile(string path, List<string> problems)
    {
        if (ReadOptionalText(path, problems) is not { } text)
        {
            return null;
        }

        var name = text.Trim();
        if (name.Length == 0)
        {
            problems.Add($"{path} is empty");
            return null;
        }

        try
        {
            ValidateName(name);
        }
        catch (OpenShellException e)
        {
            problems.Add($"{path} names an invalid gateway ({e.Message})");
            return null;
        }

        return name;
    }

    /// <summary>The file's text; null when it is missing, and null with a problem recorded when it cannot be read.</summary>
    private static string? ReadOptionalText(string path, List<string> problems)
    {
        // Returning from inside the try (rather than assigning a local declared outside it) keeps Stryker able to
        // mutate the caller: a mutated catch would otherwise leave the local unassigned (CS0165).
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"cannot read {path}");
            return null;
        }
    }

    private string UserGatewayDirectory(string name) => Path.Join(UserConfigDirectory, GatewaySubdir, name);

    private static bool HasMetadata(string directory) => File.Exists(Path.Join(directory, MetadataFile));

    private static bool IsReadable(string name, string directory, GatewayConfigSource source)
    {
        try
        {
            ParseMetadata(name, directory, source, directory);
            return true;
        }
        catch (OpenShellException)
        {
            return false;
        }
    }

    private (string Directory, GatewayConfigSource Source) ResolveDirectory(string name)
    {
        ValidateName(name);

        // Only metadata.json falls back to the system configuration: a user directory holding just credentials (such as
        // the oidc_token.json the CLI writes when signing in to a system gateway) does not shadow the system registration.
        var user = UserGatewayDirectory(name);
        if (HasMetadata(user))
        {
            return (user, GatewayConfigSource.User);
        }

        if (SystemConfigDirectory is { } system)
        {
            var sys = Path.Join(system, GatewaySubdir, name);
            if (HasMetadata(sys))
            {
                return (sys, GatewayConfigSource.System);
            }
        }

        throw new OpenShellException(OpenShellErrorKind.NotFound, $"gateway '{name}' not found");
    }

    private static GatewayMetadata ParseMetadata(string name, string directory, GatewayConfigSource source, string stateDirectory)
    {
        var meta = ReadMetadataFile(name, directory);
        return meta is { GatewayEndpoint: { Length: > 0 } gatewayEndpoint }
            ? new GatewayMetadata
            {
                Name = name,
                Endpoint = ParseEndpoint(gatewayEndpoint, name),
                AuthMode = ParseAuthMode(meta.AuthMode),
                Source = source,
                Directory = directory,
                StateDirectory = stateDirectory,
                OidcIssuer = NullIfEmpty(meta.OidcIssuer),
                OidcClientId = NullIfEmpty(meta.OidcClientId),
                OidcAudience = NullIfEmpty(meta.OidcAudience),
                OidcScopes = NullIfEmpty(meta.OidcScopes),
            }
            : throw Invalid($"missing gateway_endpoint in {MetadataFile} for gateway '{name}'");
    }

    private static GatewayMetadataDto? ReadMetadataFile(string name, string directory)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(Path.Join(directory, MetadataFile)), SdkJsonContext.Default.GatewayMetadataDto);
        }
        catch (JsonException e)
        {
            throw Invalid($"invalid JSON in {MetadataFile} for gateway '{name}'", e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw Invalid($"cannot read {MetadataFile} for gateway '{name}'", e);
        }
    }

    private static Uri ParseEndpoint(string value, string name) =>
        Uri.TryCreate(value, UriKind.Absolute, out var endpoint) && endpoint.Scheme is "http" or "https"
            ? endpoint
            : throw Invalid($"gateway_endpoint for gateway '{name}' must be an absolute http:// or https:// URL");

    private static GatewayAuthMode ParseAuthMode(string? raw) => raw switch
    {
        null or "" or "none" => GatewayAuthMode.None,
        "plaintext" => GatewayAuthMode.Plaintext,
        "cloudflare_jwt" => GatewayAuthMode.CloudflareJwt,
        "oidc" => GatewayAuthMode.Oidc,
        "mtls" => GatewayAuthMode.Mtls,
        _ => throw Invalid("unsupported auth_mode in gateway metadata"),
    };

    private static List<string> ListDirectories(string baseDirectory)
    {
        var root = Path.Join(baseDirectory, GatewaySubdir);
        var names = new List<string>();
        if (!Directory.Exists(root))
        {
            return names;
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            try
            {
                ValidateName(name);
                names.Add(name);
            }
            catch (OpenShellException)
            {
                // Skip directories that are not valid gateway registrations.
            }
        }

        // No sort here: List() sorts the merged result, and shadowing depends only on the user root coming first.
        return names;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static OpenShellException Invalid(string message, Exception? inner = null) =>
        new(OpenShellErrorKind.InvalidConfig, message, inner);
}
