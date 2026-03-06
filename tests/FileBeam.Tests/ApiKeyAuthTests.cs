namespace FileBeam.Tests;

/// <summary>
/// Tests for X-API-Key authentication — the invite-ID-based header alias for Authorization: Bearer.
/// </summary>
public sealed class ApiKeyAuthTests
{
    private static InviteStore CreateStore() => new(filePath: null);

    private static InviteToken CreateActiveInvite(InviteStore store, string role = "rw")
        => store.Create("TestUser", role, expiresAt: null, createdBy: "admin",
                        joinMaxUses: null, bearerMaxUses: null);

    // ── TryBearerAuthenticate (used by X-API-Key path) ───────────────────────

    [Fact]
    public void XApiKey_ValidInviteId_AuthenticatesSuccessfully()
    {
        var store  = CreateStore();
        var invite = CreateActiveInvite(store);

        var result = store.TryBearerAuthenticate(invite.Id);

        Assert.NotNull(result);
        Assert.Equal("TestUser", result.FriendlyName);
        Assert.Equal("rw", result.Role);
    }

    [Fact]
    public void XApiKey_InvalidId_ReturnsNull()
    {
        var store = CreateStore();

        var result = store.TryBearerAuthenticate("nonexistentid");

        Assert.Null(result);
    }

    [Fact]
    public void XApiKey_RevokedInvite_ReturnsNull()
    {
        var store  = CreateStore();
        var invite = CreateActiveInvite(store);
        store.Revoke(invite.Id);

        var result = store.TryBearerAuthenticate(invite.Id);

        Assert.Null(result);
    }

    [Fact]
    public void XApiKey_ExpiredInvite_ReturnsNull()
    {
        var store  = CreateStore();
        var invite = store.Create("ExpiredUser", "rw",
                        expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1),
                        createdBy: "admin",
                        joinMaxUses: null, bearerMaxUses: null);

        var result = store.TryBearerAuthenticate(invite.Id);

        Assert.Null(result);
    }

    [Fact]
    public void XApiKey_BearerMaxUsesExceeded_ReturnsNull()
    {
        var store  = CreateStore();
        var invite = store.Create("LimitedUser", "rw",
                        expiresAt: null, createdBy: "admin",
                        joinMaxUses: null, bearerMaxUses: 1);

        // First use should succeed
        var first = store.TryBearerAuthenticate(invite.Id);
        Assert.NotNull(first);

        // Second use exceeds the cap
        var second = store.TryBearerAuthenticate(invite.Id);
        Assert.Null(second);
    }

    [Fact]
    public void XApiKey_EmptyId_ReturnsNull()
    {
        var store = CreateStore();

        var result = store.TryBearerAuthenticate("");

        Assert.Null(result);
    }

    [Fact]
    public void XApiKey_ValidId_IncrementsUseCount()
    {
        var store  = CreateStore();
        var invite = CreateActiveInvite(store);
        Assert.Equal(0, invite.BearerUseCount);

        var result = store.TryBearerAuthenticate(invite.Id);

        Assert.NotNull(result);
        Assert.Equal(1, result.BearerUseCount);
    }

    [Fact]
    public void XApiKey_AdminRoleInvite_ReturnsCorrectRole()
    {
        var store  = CreateStore();
        var invite = CreateActiveInvite(store, role: "admin");

        var result = store.TryBearerAuthenticate(invite.Id);

        Assert.NotNull(result);
        Assert.Equal("admin", result.Role);
    }
}
