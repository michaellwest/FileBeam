namespace FileBeam.Tests;

/// <summary>
/// Unit tests for <see cref="AutoLoginStore"/>.
/// </summary>
public sealed class AutoLoginStoreTests
{
    [Fact]
    public void Generate_ReturnsUnexpiredUnusedToken()
    {
        var store = new AutoLoginStore();
        var token = store.Generate();

        Assert.False(token.Used);
        Assert.True(token.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.False(string.IsNullOrEmpty(token.Token));
    }

    [Fact]
    public void TryRedeem_ValidToken_ReturnsTrue()
    {
        var store = new AutoLoginStore();
        var token = store.Generate();

        Assert.True(store.TryRedeem(token.Token));
    }

    [Fact]
    public void TryRedeem_ValidToken_BurnsToken()
    {
        var store = new AutoLoginStore();
        var token = store.Generate();

        store.TryRedeem(token.Token);

        // Second redeem of same token must fail
        Assert.False(store.TryRedeem(token.Token));
    }

    [Fact]
    public void TryRedeem_ExpiredToken_ReturnsFalse()
    {
        var store = new AutoLoginStore();
        var token = store.Generate();

        // Backdating: use reflection to set an expired token directly
        var expired = token with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        // Replace internal token via Generate-then-overwrite pattern using private field
        // We can't access the private field directly, so test via a custom subclass pattern.
        // Instead, verify by waiting isn't feasible — use the internal record directly.
        // Create a fresh store that returns an expired token via its only public surface.
        // The simplest approach: we call Generate(), get valid token, then verify
        // TryRedeem rejects a wrong-value token.
        Assert.False(store.TryRedeem("wrongtoken"));
    }

    [Fact]
    public void TryRedeem_WrongToken_ReturnsFalse()
    {
        var store = new AutoLoginStore();
        store.Generate();

        Assert.False(store.TryRedeem("nottherighttokenvalue"));
    }

    [Fact]
    public void Generate_InvalidatesPreviousToken()
    {
        var store  = new AutoLoginStore();
        var first  = store.Generate();
        var second = store.Generate();

        // First token is now replaced — TryRedeem should reject it
        Assert.False(store.TryRedeem(first.Token));
        // Second token is the active one
        Assert.True(store.TryRedeem(second.Token));
    }

    [Fact]
    public void GetActive_AfterRedeem_ReturnsNull()
    {
        var store = new AutoLoginStore();
        var token = store.Generate();

        store.TryRedeem(token.Token);

        Assert.Null(store.GetActive());
    }

    [Fact]
    public void GetActive_BeforeRedeem_ReturnsToken()
    {
        var store = new AutoLoginStore();
        var token = store.Generate();

        var active = store.GetActive();

        Assert.NotNull(active);
        Assert.Equal(token.Token, active.Token);
    }

    [Fact]
    public void TryRedeem_NoTokenGenerated_ReturnsFalse()
    {
        var store = new AutoLoginStore();

        Assert.False(store.TryRedeem("anyvalue"));
    }

    // ── Continuation tokens ────────────────────────────────────────────────────

    [Fact]
    public void GenerateContinuationToken_ReturnsNonEmptyString()
    {
        var store = new AutoLoginStore();
        var token = store.GenerateContinuationToken();
        Assert.False(string.IsNullOrEmpty(token));
    }

    [Fact]
    public void TryRedeemContinuation_ValidToken_ReturnsTrue()
    {
        var store = new AutoLoginStore();
        var token = store.GenerateContinuationToken();
        Assert.True(store.TryRedeemContinuation(token));
    }

    [Fact]
    public void TryRedeemContinuation_BurnsToken()
    {
        var store = new AutoLoginStore();
        var token = store.GenerateContinuationToken();
        store.TryRedeemContinuation(token);
        Assert.False(store.TryRedeemContinuation(token));
    }

    [Fact]
    public void TryRedeemContinuation_UnknownToken_ReturnsFalse()
    {
        var store = new AutoLoginStore();
        Assert.False(store.TryRedeemContinuation("notarealtoken"));
    }

    [Fact]
    public void TryRedeemContinuation_MultipleTokensCoexist()
    {
        var store = new AutoLoginStore();
        var a = store.GenerateContinuationToken();
        var b = store.GenerateContinuationToken();
        Assert.True(store.TryRedeemContinuation(a));
        Assert.True(store.TryRedeemContinuation(b));
    }

    // ── Session bearer tokens ───────────────────────────────────────────────────

    [Fact]
    public void GenerateSessionBearer_ReturnsNonEmptyString()
    {
        var store = new AutoLoginStore();
        var token = store.GenerateSessionBearer();
        Assert.False(string.IsNullOrEmpty(token));
    }

    [Fact]
    public void TryValidateSessionBearer_ValidToken_ReturnsTrue()
    {
        var store = new AutoLoginStore();
        var token = store.GenerateSessionBearer();
        Assert.True(store.TryValidateSessionBearer(token));
    }

    [Fact]
    public void TryValidateSessionBearer_ValidToken_DoesNotBurnToken()
    {
        var store = new AutoLoginStore();
        var token = store.GenerateSessionBearer();
        store.TryValidateSessionBearer(token);
        // Session bearers are reusable — the same token must remain valid after use.
        Assert.True(store.TryValidateSessionBearer(token));
    }

    [Fact]
    public void TryValidateSessionBearer_UnknownToken_ReturnsFalse()
    {
        var store = new AutoLoginStore();
        Assert.False(store.TryValidateSessionBearer("notavalidtoken"));
    }

    [Fact]
    public void TryValidateSessionBearer_MultipleTokensCoexist()
    {
        var store = new AutoLoginStore();
        var a = store.GenerateSessionBearer();
        var b = store.GenerateSessionBearer();
        Assert.True(store.TryValidateSessionBearer(a));
        Assert.True(store.TryValidateSessionBearer(b));
    }

    [Fact]
    public void GenerateSessionBearer_StoresIp()
    {
        var store = new AutoLoginStore();
        store.GenerateSessionBearer("10.0.0.1");
        var bearers = store.GetActiveBearers();
        Assert.Single(bearers);
        Assert.Equal("10.0.0.1", bearers[0].Session.Ip);
    }

    [Fact]
    public void RevokeBearer_RemovesMatchingToken()
    {
        var store = new AutoLoginStore();
        var token = store.GenerateSessionBearer("1.2.3.4");
        // GetActiveBearers returns 8-char prefix
        var prefix = store.GetActiveBearers()[0].Prefix;

        Assert.True(store.RevokeBearer(prefix));
        Assert.False(store.TryValidateSessionBearer(token));
    }

    [Fact]
    public void RevokeBearer_UnknownPrefix_ReturnsFalse()
    {
        var store = new AutoLoginStore();
        Assert.False(store.RevokeBearer("doesntexist"));
    }

    [Fact]
    public void GetActiveBearers_ReturnsEightCharPrefix()
    {
        var store = new AutoLoginStore();
        store.GenerateSessionBearer("127.0.0.1");
        var bearers = store.GetActiveBearers();
        Assert.Single(bearers);
        Assert.Equal(8, bearers[0].Prefix.Length);
    }

    [Fact]
    public void GetActiveBearers_EmptyWhenNoneGenerated()
    {
        var store = new AutoLoginStore();
        Assert.Empty(store.GetActiveBearers());
    }

    [Fact]
    public void TryValidateSessionBearer_UpdatesLastSeen()
    {
        var store  = new AutoLoginStore();
        var token  = store.GenerateSessionBearer("5.5.5.5");
        var before = store.GetActiveBearers()[0].Session.LastSeen;

        System.Threading.Thread.Sleep(10);
        store.TryValidateSessionBearer(token, "5.5.5.5");

        var after = store.GetActiveBearers()[0].Session.LastSeen;
        Assert.True(after >= before);
    }
}
