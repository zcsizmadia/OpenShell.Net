// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace OpenShell.Oidc;

/// <summary>A token bundle read from <c>oidc_token.json</c>, with the issuer and client it was issued for.</summary>
/// <param name="Tokens">The tokens.</param>
/// <param name="Issuer">The OIDC issuer; null when the file does not record one.</param>
/// <param name="ClientId">The OIDC client id; null when the file does not record one.</param>
internal sealed record StoredOidcToken(OidcTokens Tokens, string? Issuer, string? ClientId)
{
    /// <summary>
    /// True when the bundle was issued for this issuer (ignoring a trailing slash) and client id. A file that records
    /// neither, as early SDK builds wrote, matches nothing; the OpenShell CLI cannot read such a file either.
    /// </summary>
    public bool IsFor(string issuer, string clientId) =>
        Issuer is { Length: > 0 } && ClientId is { Length: > 0 }
        && string.Equals(OidcDiscovery.Normalize(Issuer), OidcDiscovery.Normalize(issuer), StringComparison.Ordinal)
        && string.Equals(ClientId, clientId, StringComparison.Ordinal);
}

/// <summary>
/// The <c>oidc_token.json</c> cache shared with the OpenShell CLI, in the CLI's format (<c>openshell-bootstrap</c>
/// <c>OidcTokenBundle</c>): <c>access_token</c>, optional <c>refresh_token</c>, optional <c>expires_at</c> in Unix
/// seconds, <c>issuer</c> and <c>client_id</c>.
/// </summary>
internal static class OidcTokenFile
{
    public const string FileName = "oidc_token.json";
    private static readonly TimeSpan ExpiryLeeway = TimeSpan.FromSeconds(10);
    private static readonly ulong MaxUnixSeconds = (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>Reads the cached bundle regardless of expiry; null when absent or without an access token.</summary>
    public static StoredOidcToken? Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new OidcException(OidcFailure.TokenPersist, $"cannot read {FileName}", e);
        }

        OidcTokenBundleDto? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize(text, SdkJsonContext.Default.OidcTokenBundleDto);
        }
        catch (JsonException e)
        {
            throw new OidcException(OidcFailure.TokenPersist, $"invalid JSON in {FileName}", e);
        }

        if (bundle is null || string.IsNullOrEmpty(bundle.AccessToken))
        {
            return null;
        }

        DateTimeOffset? expiry = null;
        if (bundle.ExpiresAt is { } seconds)
        {
            if (seconds > MaxUnixSeconds)
            {
                throw new OidcException(OidcFailure.TokenPersist, $"expires_at in {FileName} is out of range");
            }

            expiry = DateTimeOffset.FromUnixTimeSeconds((long)seconds);
        }

        var tokens = new OidcTokens(
            bundle.AccessToken,
            string.IsNullOrEmpty(bundle.RefreshToken) ? null : bundle.RefreshToken,
            "Bearer",
            expiry);
        return new StoredOidcToken(tokens, NullIfEmpty(bundle.Issuer), NullIfEmpty(bundle.ClientId));
    }

    /// <summary>
    /// Reads the cached tokens issued for this issuer and client id, or null when absent, issued for another client,
    /// or expired (with a 10 s leeway).
    /// </summary>
    public static OidcTokens? ReadValid(string directory, string issuer, string clientId, TimeProvider time)
    {
        if (Read(directory) is not { } stored || !stored.IsFor(issuer, clientId))
        {
            return null;
        }

        return stored.Tokens is { ExpiresAt: { } exp } && time.GetUtcNow() > exp - ExpiryLeeway ? null : stored.Tokens;
    }

    /// <summary>Writes the tokens atomically with owner-only permissions where the OS supports them.</summary>
    public static void Write(string directory, OidcTokens tokens, string issuer, string clientId)
    {
        var bundle = new OidcTokenBundleDto
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            ExpiresAt = tokens.ExpiresAt is { } expiry ? (ulong)Math.Max(expiry.ToUnixTimeSeconds(), 0) : null,
            Issuer = issuer,
            ClientId = clientId,
        };

        var path = Path.Combine(directory, FileName);
        var temp = Path.Combine(directory, $".oidc-token-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                JsonSerializer.Serialize(stream, bundle, SdkJsonContext.Default.OidcTokenBundleDto);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new OidcException(OidcFailure.TokenPersist, $"failed to write {FileName}", e);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of the temporary file.
            }
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
