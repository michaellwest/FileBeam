using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

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

        var result = RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out var role, out var user, out _);

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

        Assert.True(RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out _, out _, out _));
    }

    [Fact]
    public void ValidCookie_UsesLiveRoleFromStore()
    {
        // Role in cookie is "rw" but store was edited to "ro"
        var store  = new InviteStore();
        var invite = store.Create("Alice", "rw", null, "admin");
        var cookie = BuildCookie(TestKey, invite.Id, "rw");

        store.Edit(invite.Id, null, "ro", null);

        RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out var role, out _, out _);
        Assert.Equal("ro", role);   // live store role wins
    }

    // ── Invalid signature ──────────────────────────────────────────────────────

    [Fact]
    public void TamperedSignature_ReturnsFalse()
    {
        var store  = new InviteStore();
        var invite = store.Create("Alice", "rw", null, "admin");
        var cookie = BuildCookie(TestKey, invite.Id, invite.Role);

        // Flip the first character of the signature segment (avoids base64 padding-bit edge case on last char)
        var dot     = cookie.LastIndexOf('.');
        var sigFirst = cookie[dot + 1];
        var tampered = cookie[..(dot + 1)] + (sigFirst == 'A' ? 'B' : 'A') + cookie[(dot + 2)..];

        Assert.False(RouteHandlers.TryValidateSessionCookie(tampered, TestKey, store, out _, out _, out _));
    }

    [Fact]
    public void WrongKey_ReturnsFalse()
    {
        var store      = new InviteStore();
        var invite     = store.Create("Alice", "rw", null, "admin");
        var cookie     = BuildCookie(TestKey, invite.Id, invite.Role);
        var wrongKey   = RandomNumberGenerator.GetBytes(32);

        Assert.False(RouteHandlers.TryValidateSessionCookie(cookie, wrongKey, store, out _, out _, out _));
    }

    // ── Malformed cookie ───────────────────────────────────────────────────────

    [Fact]
    public void NoDot_ReturnsFalse()
    {
        Assert.False(RouteHandlers.TryValidateSessionCookie("nodothere", TestKey, null, out _, out _, out _));
    }

    [Fact]
    public void EmptyString_ReturnsFalse()
    {
        Assert.False(RouteHandlers.TryValidateSessionCookie("", TestKey, null, out _, out _, out _));
    }

    [Fact]
    public void InvalidBase64Signature_ReturnsFalse()
    {
        Assert.False(RouteHandlers.TryValidateSessionCookie("validpayload.!!!notbase64", TestKey, null, out _, out _, out _));
    }

    // ── Revoked / inactive token ───────────────────────────────────────────────

    [Fact]
    public void RevokedToken_ReturnsFalse()
    {
        var store  = new InviteStore();
        var invite = store.Create("Alice", "rw", null, "admin");
        var cookie = BuildCookie(TestKey, invite.Id, invite.Role);

        store.Revoke(invite.Id);

        Assert.False(RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out _, out _, out _));
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

        Assert.False(RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out _, out _, out _));
    }

    [Fact]
    public void InviteStoreExpiry_InPast_ReturnsFalse()
    {
        var store  = new InviteStore();
        var expiry = DateTimeOffset.UtcNow.AddSeconds(-1); // already expired
        var invite = store.Create("Alice", "rw", expiry, "admin");
        // Cookie itself has no expiry (simulate: cookie was issued before expiry was edited)
        var cookie = BuildCookie(TestKey, invite.Id, invite.Role);

        Assert.False(RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out _, out _, out _));
    }

    // ── Missing token in store ─────────────────────────────────────────────────

    [Fact]
    public void MissingTokenInStore_ReturnsFalse()
    {
        var store  = new InviteStore(); // token never added
        var cookie = BuildCookie(TestKey, "doesnotexist12", "rw");

        Assert.False(RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out _, out _, out _));
    }

    // ── No invite store (cookie-only validation) ───────────────────────────────

    [Fact]
    public void NoStore_ValidSignature_ReturnsTrue()
    {
        var cookie = BuildCookie(TestKey, "anyid", "rw");

        var result = RouteHandlers.TryValidateSessionCookie(cookie, TestKey, inviteStore: null, out var role, out var user, out _);

        Assert.True(result);
        Assert.Equal("rw", role);
        Assert.Equal("invite:?", user);
    }

    [Fact]
    public void NoStore_ExpiredCookie_ReturnsFalse()
    {
        var pastUnix = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds();
        var cookie   = BuildCookie(TestKey, "anyid", "rw", pastUnix);

        Assert.False(RouteHandlers.TryValidateSessionCookie(cookie, TestKey, inviteStore: null, out _, out _, out _));
    }

    // ── JoinWithInvite — session stays valid after cap ─────────────────────────

    [Fact]
    public void SingleUseInvite_SessionRemainsValidAfterRedemption()
    {
        // Regression: previously TryRedeem set IsActive=false on the last use, which
        // immediately invalidated the session cookie it had just issued.
        var store  = new InviteStore();
        var invite = store.Create("Alice", "rw", null, "admin", joinMaxUses: 1);
        var cookie = BuildCookie(TestKey, invite.Id, invite.Role);

        // Simulate what happens after /join: UseCount is now 1, cap is reached.
        store.TryRedeem(invite.Id);

        // The session cookie issued during that redemption must still be valid.
        Assert.True(RouteHandlers.TryValidateSessionCookie(cookie, TestKey, store, out _, out _, out _));
    }

    [Fact]
    public void JoinWithInvite_RefreshWithExistingSession_DoesNotIncrementUseCount()
    {
        var tmpDir  = Directory.CreateTempSubdirectory("fb_join_").FullName;
        try
        {
            var store    = new InviteStore();
            var invite   = store.Create("Bob", "rw", null, "admin", joinMaxUses: 1);
            var watcher  = new FileWatcher(tmpDir);
            var handlers = new RouteHandlers(tmpDir, tmpDir, watcher,
                inviteStore: store, sessionKey: TestKey);

            // First visit: no cookie → TryRedeem → UseCount becomes 1
            var ctx1 = new DefaultHttpContext();
            ctx1.Response.Body = new MemoryStream();
            handlers.JoinWithInvite(ctx1, invite.Id);

            store.TryGet(invite.Id, out var afterFirst);
            Assert.Equal(1, afterFirst!.UseCount);

            // Build the session cookie that was set during the first visit
            var cookie = BuildCookie(TestKey, invite.Id, invite.Role);

            // Second visit: same session cookie → should skip TryRedeem
            var ctx2 = new DefaultHttpContext();
            ctx2.Response.Body = new MemoryStream();
            ctx2.Request.Headers["Cookie"] = $"fb.session={cookie}";
            handlers.JoinWithInvite(ctx2, invite.Id);

            store.TryGet(invite.Id, out var afterRefresh);
            Assert.Equal(1, afterRefresh!.UseCount); // unchanged
            watcher.Dispose();
        }
        finally { Directory.Delete(tmpDir, recursive: true); }
    }

    [Fact]
    public void JoinWithInvite_SessionForDifferentToken_StillCallsTryRedeem()
    {
        var tmpDir = Directory.CreateTempSubdirectory("fb_join2_").FullName;
        try
        {
            var store    = new InviteStore();
            var inviteA  = store.Create("Alice", "rw", null, "admin");
            var inviteB  = store.Create("Bob",   "rw", null, "admin");
            var watcher  = new FileWatcher(tmpDir);
            var handlers = new RouteHandlers(tmpDir, tmpDir, watcher,
                inviteStore: store, sessionKey: TestKey);

            // Cookie is for invite A, but we're joining with invite B
            var cookieForA = BuildCookie(TestKey, inviteA.Id, inviteA.Role);

            var ctx = new DefaultHttpContext();
            ctx.Response.Body = new MemoryStream();
            ctx.Request.Headers["Cookie"] = $"fb.session={cookieForA}";
            handlers.JoinWithInvite(ctx, inviteB.Id);

            store.TryGet(inviteB.Id, out var b);
            Assert.Equal(1, b!.UseCount); // TryRedeem was called for B
            watcher.Dispose();
        }
        finally { Directory.Delete(tmpDir, recursive: true); }
    }
}
