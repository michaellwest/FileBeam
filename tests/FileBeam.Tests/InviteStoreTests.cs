namespace FileBeam.Tests;

public sealed class InviteStoreTests
{
    // ── Create ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_ReturnsActiveToken_WithCorrectFields()
    {
        var store = new InviteStore();
        var before = DateTimeOffset.UtcNow;
        var token  = store.Create("Alice", "rw", null, "admin");
        var after  = DateTimeOffset.UtcNow;

        Assert.Equal("Alice", token.FriendlyName);
        Assert.Equal("rw",    token.Role);
        Assert.Null(token.ExpiresAt);
        Assert.Equal(0,       token.UseCount);
        Assert.Equal("admin", token.CreatedBy);
        Assert.True(token.IsActive);
        Assert.True(token.CreatedAt >= before && token.CreatedAt <= after);
        Assert.NotEmpty(token.Id);
        Assert.Equal(12, token.Id.Length);
    }

    [Fact]
    public void Create_MultipleTokens_HaveUniqueIds()
    {
        var store = new InviteStore();
        var ids   = Enumerable.Range(0, 50)
                              .Select(_ => store.Create("test", "rw", null, "admin").Id)
                              .ToHashSet();
        Assert.Equal(50, ids.Count);
    }

    [Fact]
    public void Create_WithExpiry_StoresExpiry()
    {
        var store  = new InviteStore();
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var token  = store.Create("Bob", "ro", expiry, "admin");
        Assert.Equal(expiry, token.ExpiresAt);
    }

    // ── GetAll ─────────────────────────────────────────────────────────────────

    [Fact]
    public void GetAll_Empty_ReturnsEmptyList()
    {
        var store = new InviteStore();
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void GetAll_ReturnsAllTokens()
    {
        var store = new InviteStore();
        store.Create("Alice", "rw", null, "admin");
        store.Create("Bob",   "ro", null, "admin");
        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public void GetAll_OrderedNewestFirst()
    {
        var store = new InviteStore();
        var t1 = store.Create("First",  "rw", null, "admin");
        var t2 = store.Create("Second", "rw", null, "admin");
        var all = store.GetAll();
        Assert.Equal(t2.Id, all[0].Id);
        Assert.Equal(t1.Id, all[1].Id);
    }

    // ── TryGet ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TryGet_ExistingId_ReturnsTrue()
    {
        var store = new InviteStore();
        var token = store.Create("Alice", "rw", null, "admin");
        Assert.True(store.TryGet(token.Id, out var found));
        Assert.Equal(token.Id, found!.Id);
    }

    [Fact]
    public void TryGet_MissingId_ReturnsFalse()
    {
        var store = new InviteStore();
        Assert.False(store.TryGet("doesnotexist", out _));
    }

    // ── Revoke ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Revoke_ExistingToken_SetsIsActiveFalse()
    {
        var store = new InviteStore();
        var token = store.Create("Alice", "rw", null, "admin");
        Assert.True(store.Revoke(token.Id));
        store.TryGet(token.Id, out var updated);
        Assert.False(updated!.IsActive);
    }

    [Fact]
    public void Revoke_MissingToken_ReturnsFalse()
    {
        var store = new InviteStore();
        Assert.False(store.Revoke("missing"));
    }

    // ── Edit ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Edit_UpdatesFriendlyName()
    {
        var store = new InviteStore();
        var token = store.Create("Old Name", "rw", null, "admin");
        Assert.True(store.Edit(token.Id, friendlyName: "New Name", role: null, expiresAt: null));
        store.TryGet(token.Id, out var updated);
        Assert.Equal("New Name", updated!.FriendlyName);
    }

    [Fact]
    public void Edit_UpdatesRole()
    {
        var store = new InviteStore();
        var token = store.Create("Alice", "rw", null, "admin");
        store.Edit(token.Id, friendlyName: null, role: "ro", expiresAt: null);
        store.TryGet(token.Id, out var updated);
        Assert.Equal("ro", updated!.Role);
    }

    [Fact]
    public void Edit_UpdatesExpiry()
    {
        var store  = new InviteStore();
        var token  = store.Create("Alice", "rw", null, "admin");
        var newExp = DateTimeOffset.UtcNow.AddDays(7);
        store.Edit(token.Id, friendlyName: null, role: null, expiresAt: newExp);
        store.TryGet(token.Id, out var updated);
        Assert.Equal(newExp, updated!.ExpiresAt);
    }

    [Fact]
    public void Edit_ClearExpiry_RemovesExpiry()
    {
        var store  = new InviteStore();
        var expiry = DateTimeOffset.UtcNow.AddDays(1);
        var token  = store.Create("Alice", "rw", expiry, "admin");
        store.Edit(token.Id, friendlyName: null, role: null, expiresAt: null, clearExpiry: true);
        store.TryGet(token.Id, out var updated);
        Assert.Null(updated!.ExpiresAt);
    }

    [Fact]
    public void Edit_MissingToken_ReturnsFalse()
    {
        var store = new InviteStore();
        Assert.False(store.Edit("missing", null, null, null));
    }

    // ── TryRedeem ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryRedeem_ValidToken_ReturnsTokenAndIncrementsUseCount()
    {
        var store  = new InviteStore();
        var token  = store.Create("Alice", "rw", null, "admin");
        var result = store.TryRedeem(token.Id);
        Assert.NotNull(result);
        Assert.Equal(1, result!.UseCount);
    }

    [Fact]
    public void TryRedeem_MultipleRedemptions_IncrementsCorrectly()
    {
        var store = new InviteStore();
        var token = store.Create("Alice", "rw", null, "admin");
        store.TryRedeem(token.Id);
        store.TryRedeem(token.Id);
        var result = store.TryRedeem(token.Id);
        Assert.Equal(3, result!.UseCount);
    }

    [Fact]
    public void TryRedeem_InactiveToken_ReturnsNull()
    {
        var store = new InviteStore();
        var token = store.Create("Alice", "rw", null, "admin");
        store.Revoke(token.Id);
        Assert.Null(store.TryRedeem(token.Id));
    }

    [Fact]
    public void TryRedeem_ExpiredToken_ReturnsNull()
    {
        var store  = new InviteStore();
        var expiry = DateTimeOffset.UtcNow.AddSeconds(-1); // already expired
        var token  = store.Create("Alice", "rw", expiry, "admin");
        Assert.Null(store.TryRedeem(token.Id));
    }

    [Fact]
    public void TryRedeem_MissingToken_ReturnsNull()
    {
        var store = new InviteStore();
        Assert.Null(store.TryRedeem("doesnotexist"));
    }

    [Fact]
    public void TryRedeem_NotYetExpiredToken_Succeeds()
    {
        var store  = new InviteStore();
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var token  = store.Create("Alice", "rw", expiry, "admin");
        Assert.NotNull(store.TryRedeem(token.Id));
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    [Fact]
    public void Persistence_SaveAndLoad_RoundTrips()
    {
        var path = Path.GetTempFileName();
        try
        {
            var store1 = new InviteStore(path);
            var t1     = store1.Create("Alice", "rw", null, "admin");
            var t2     = store1.Create("Bob",   "ro", DateTimeOffset.UtcNow.AddHours(1), "admin");
            store1.TryRedeem(t1.Id);

            var store2 = new InviteStore(path);
            Assert.Equal(2, store2.GetAll().Count);

            Assert.True(store2.TryGet(t1.Id, out var r1));
            Assert.Equal("Alice", r1!.FriendlyName);
            Assert.Equal(1, r1.UseCount);

            Assert.True(store2.TryGet(t2.Id, out var r2));
            Assert.Equal("Bob", r2!.FriendlyName);
            Assert.Equal("ro",  r2.Role);
            Assert.NotNull(r2.ExpiresAt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Persistence_RevokedToken_PersistedAsInactive()
    {
        var path = Path.GetTempFileName();
        try
        {
            var store1 = new InviteStore(path);
            var token  = store1.Create("Alice", "rw", null, "admin");
            store1.Revoke(token.Id);

            var store2 = new InviteStore(path);
            store2.TryGet(token.Id, out var loaded);
            Assert.False(loaded!.IsActive);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NoPersistence_WorksWithoutFile()
    {
        var store = new InviteStore(filePath: null);
        var token = store.Create("Alice", "rw", null, "admin");
        Assert.NotNull(store.TryRedeem(token.Id));
    }
}
