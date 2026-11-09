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
                    ? Path.Combine(xdg, AppName)
                    : throw new OpenShellException(OpenShellErrorKind.InvalidConfig, "XDG_CONFIG_HOME must be an absolute path");
            }

            if (OperatingSystem.IsWindows() && _env("APPDATA") is { Length: > 0 } appData)
            {
                return Path.Combine(appData, AppName);
            }

            var home = _env(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            return string.IsNullOrEmpty(home)
                ? throw new OpenShellException(OpenShellErrorKind.InvalidConfig, "cannot determine home directory")
                : Path.Combine(home, ".config", AppName);
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

    /// <summary>Loads a gateway by name; null selects the active gateway.</summary>
    public GatewayMetadata Load(string? name = null)
    {
        name ??= ResolveActiveName();
        var (directory, source) = ResolveDirectory(name);
        return ParseMetadata(name, directory, source);
    }

    /// <summary>Lists registered gateways, user registrations shadowing system ones.</summary>
    public IReadOnlyList<GatewayEntry> List()
    {
        var active = TryResolveActiveName();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<GatewayEntry>();

        foreach (var name in ListDirectories(UserConfigDirectory))
        {
            seen.Add(name);
            result.Add(new GatewayEntry(name, name == active, GatewayConfigSource.User));
        }

        if (SystemConfigDirectory is { } system)
        {
            foreach (var name in ListDirectories(system))
            {
                if (seen.Add(name))
                {
                    result.Add(new GatewayEntry(name, name == active, GatewayConfigSource.System));
                }
            }
        }

        return result;
    }

    /// <summary>Resolves the active gateway name from <c>$OPENSHELL_GATEWAY</c> or the <c>active_gateway</c> file.</summary>
    public string ResolveActiveName()
    {
        if (_env(ActiveGatewayVariable) is { Length: > 0 } fromEnv)
        {
            ValidateName(fromEnv);
            return fromEnv;
        }

        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(UserConfigDirectory, ActiveGatewayFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new OpenShellException(OpenShellErrorKind.NotFound, "no active gateway configured", e);
        }

        var name = text.Trim();
        if (name.Length == 0)
        {
            throw new OpenShellException(OpenShellErrorKind.NotFound, "no active gateway configured: active_gateway file is empty");
        }

        ValidateName(name);
        return name;
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

    private (string Directory, GatewayConfigSource Source) ResolveDirectory(string name)
    {
        ValidateName(name);

        var user = Path.Combine(UserConfigDirectory, GatewaySubdir, name);
        if (Directory.Exists(user))
        {
            return (user, GatewayConfigSource.User);
        }

        if (SystemConfigDirectory is { } system)
        {
            var sys = Path.Combine(system, GatewaySubdir, name);
            if (Directory.Exists(sys))
            {
                return (sys, GatewayConfigSource.System);
            }
        }

        throw new OpenShellException(OpenShellErrorKind.NotFound, $"gateway '{name}' not found");
    }

    private static GatewayMetadata ParseMetadata(string name, string directory, GatewayConfigSource source)
    {
        var path = Path.Combine(directory, MetadataFile);
        GatewayMetadataDto? meta;
        try
        {
            meta = JsonSerializer.Deserialize(File.ReadAllText(path), SdkJsonContext.Default.GatewayMetadataDto);
        }
        catch (JsonException e)
        {
            throw Invalid($"invalid JSON in {MetadataFile} for gateway '{name}'", e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw Invalid($"cannot read {MetadataFile} for gateway '{name}'", e);
        }

        return meta is { GatewayEndpoint: { Length: > 0 } gatewayEndpoint }
            ? new GatewayMetadata
            {
                Name = name,
                Endpoint = ParseEndpoint(gatewayEndpoint, name),
                AuthMode = ParseAuthMode(meta.AuthMode),
                Source = source,
                Directory = directory,
                OidcIssuer = NullIfEmpty(meta.OidcIssuer),
                OidcClientId = NullIfEmpty(meta.OidcClientId),
                OidcAudience = NullIfEmpty(meta.OidcAudience),
                OidcScopes = NullIfEmpty(meta.OidcScopes),
            }
            : throw Invalid($"missing gateway_endpoint in {MetadataFile} for gateway '{name}'");
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
        var root = Path.Combine(baseDirectory, GatewaySubdir);
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

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static OpenShellException Invalid(string message, Exception? inner = null) =>
        new(OpenShellErrorKind.InvalidConfig, message, inner);
}
