using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileBeam.Tests;

/// <summary>
/// Unit tests for <see cref="RouteHandlers.TryValidateSessionCookie"/>.
/// </summary>
public sealed class CookieSessionTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static readonly byte[] TestKey = RandomNumberGenerator.GetBytes(32);

    private static string BuildCookie(
        byte[]          key,
        string          tokenId,
        string          role,
        long?           expiresAtUnix = null)
    {
        var payloadObj  = new { tokenId, role, issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), expiresAt = expiresAtUnix };
        var payloadJson = JsonSerializer.Serialize(payloadObj);
        var payloadB64  = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var sig         = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payloadB64));
        return payloadB64 + "." + Base64UrlEncode(sig);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // ── Valid cookie ───────────────────────────────────────────────────────────

    [Fact]
    public void ValidCookie_NoExpiry_ReturnsTrue()
    {
        var store  = new InviteStore();
        var invite = store.Create("Alice", "rw", null, "admin");
        var cookie = BuildCookie(TestKey, invite.Id, invite.Role);

        var result = RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out var role, out var user);

        Assert.True(result);
        Assert.Equal("rw", role);
        Assert.Equal($"invite:{invite.Id}", user);
    }

    [Fact]
    public void ValidCookie_WithFutureExpiry_ReturnsTrue()
    {
        var store   = new InviteStore();
        var expiry  = DateTimeOffset.UtcNow.AddHours(1);
        var invite  = store.Create("Bob", "ro", expiry, "admin");
        var cookie  = BuildCookie(TestKey, invite.Id, invite.Role, expiry.ToUnixTimeSeconds());

        Assert.True(RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out _, out _));
    }

    [Fact]
    public void ValidCookie_UsesLiveRoleFromStore()
    {
        // Role in cookie is "rw" but store was edited to "ro"
        var store  = new InviteStore();
        var invite = store.Create("Alice", "rw", null, "admin");
        var cookie = BuildCookie(TestKey, invite.Id, "rw");

        store.Edit(invite.Id, null, "ro", null);

        RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out var role, out _);
        Assert.Equal("ro", role);   // live store role wins
    }

    // ── Invalid signature ──────────────────────────────────────────────────────

    [Fact]
    public void TamperedSignature_ReturnsFalse()
    {
        var store  = new InviteStore();
        var invite = store.Create("Alice", "rw", null, "admin");
        var cookie = BuildCookie(TestKey, invite.Id, invite.Role);

        // Flip the last character of the signature segment
        var last = cookie[^1];
        var tampered = cookie[..^1] + (last == 'A' ? 'B' : 'A');

        Assert.False(RouteHandlers.TryValidateSessionCookie(tampered, TestKey, store, out _, out _));
    }

    [Fact]
    public void WrongKey_ReturnsFalse()
    {
        var store      = new InviteStore();
        var invite     = store.Create("Alice", "rw", null, "admin");
        var cookie     = BuildCookie(TestKey, invite.Id, invite.Role);
        var wrongKey   = RandomNumberGenerator.GetBytes(32);

        Assert.False(RouteHandlers.TryValidateSessionCookie(cookie, wrongKey, store, out _, out _));
    }

    // ── Malformed cookie ───────────────────────────────────────────────────────

    [Fact]
    public void NoDot_ReturnsFalse()
    {
        Assert.False(RouteHandlers.TryValidateSessionCookie("nodothere", TestKey, null, out _, out _));
    }

    [Fact]
    public void EmptyString_ReturnsFalse()
    {
        Assert.False(RouteHandlers.TryValidateSessionCookie("", TestKey, null, out _, out _));
    }

    [Fact]
    public void InvalidBase64Signature_ReturnsFalse()
    {
        Assert.False(RouteHandlers.TryValidateSessionCookie("validpayload.!!!notbase64", TestKey, null, out _, out _));
    }

    // ── Revoked / inactive token ───────────────────────────────────────────────

    [Fact]
    public void RevokedToken_ReturnsFalse()
    {
        var store  = new InviteStore();
        var invite = store.Create("Alice", "rw", null, "admin");
        var cookie = BuildCookie(TestKey, invite.Id, invite.Role);

        store.Revoke(invite.Id);

        Assert.False(RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out _, out _));
    }

    // ── Expired cookie / token ─────────────────────────────────────────────────

    [Fact]
    public void CookieLevelExpiry_InPast_ReturnsFalse()
    {
        var store  = new InviteStore();
        var invite = store.Create("Alice", "rw", null, "admin");
        // Build cookie with expiresAt already in the past
        var pastUnix = DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeSeconds();
        var cookie   = BuildCookie(TestKey, invite.Id, invite.Role, pastUnix);

        Assert.False(RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out _, out _));
    }

    [Fact]
    public void InviteStoreExpiry_InPast_ReturnsFalse()
    {
        var store  = new InviteStore();
        var expiry = DateTimeOffset.UtcNow.AddSeconds(-1); // already expired
        var invite = store.Create("Alice", "rw", expiry, "admin");
        // Cookie itself has no expiry (simulate: cookie was issued before expiry was edited)
        var cookie = BuildCookie(TestKey, invite.Id, invite.Role);

        Assert.False(RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out _, out _));
    }

    // ── Missing token in store ─────────────────────────────────────────────────

    [Fact]
    public void MissingTokenInStore_ReturnsFalse()
    {
        var store  = new InviteStore(); // token never added
        var cookie = BuildCookie(TestKey, "doesnotexist12", "rw");

        Assert.False(RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out _, out _));
    }

    // ── No invite store (cookie-only validation) ───────────────────────────────

    [Fact]
    public void NoStore_ValidSignature_ReturnsTrue()
    {
        var cookie = BuildCookie(TestKey, "anyid", "rw");

        var result = RouteHandlers.TryValidateSessionCookie(cookie, TestKey, inviteStore: null, out var role, out var user);

        Assert.True(result);
        Assert.Equal("rw", role);
        Assert.Equal("invite:?", user);
    }

    [Fact]
    public void NoStore_ExpiredCookie_ReturnsFalse()
    {
        var pastUnix = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds();
        var cookie   = BuildCookie(TestKey, "anyid", "rw", pastUnix);

        Assert.False(RouteHandlers.TryValidateSessionCookie(cookie, TestKey, inviteStore: null, out _, out _));
    }
}
