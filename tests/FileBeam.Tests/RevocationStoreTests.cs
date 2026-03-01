namespace FileBeam.Tests;

public sealed class RevocationStoreTests
{
    [Fact]
    public void IsUserRevoked_UnknownUser_ReturnsFalse()
    {
        var store = new RevocationStore();
        Assert.False(store.IsUserRevoked("alice"));
    }

    [Fact]
    public void RevokeUser_ThenIsRevoked_ReturnsTrue()
    {
        var store = new RevocationStore();
        store.RevokeUser("alice");
        Assert.True(store.IsUserRevoked("alice"));
    }

    [Fact]
    public void RevokeUser_IsCaseInsensitive()
    {
        var store = new RevocationStore();
        store.RevokeUser("Alice");
        Assert.True(store.IsUserRevoked("alice"));
        Assert.True(store.IsUserRevoked("ALICE"));
    }

    [Fact]
    public void UnrevokeUser_RemovesFromStore()
    {
        var store = new RevocationStore();
        store.RevokeUser("bob");
        store.UnrevokeUser("bob");
        Assert.False(store.IsUserRevoked("bob"));
    }

    [Fact]
    public void RevokedUsers_ReturnsAllBannedUsernames()
    {
        var store = new RevocationStore();
        store.RevokeUser("alice");
        store.RevokeUser("bob");
        Assert.Contains("alice", store.RevokedUsers);
        Assert.Contains("bob",   store.RevokedUsers);
    }

    [Fact]
    public void IsIpRevoked_UnknownIp_ReturnsFalse()
    {
        var store = new RevocationStore();
        Assert.False(store.IsIpRevoked("1.2.3.4"));
    }

    [Fact]
    public void RevokeIp_ThenIsRevoked_ReturnsTrue()
    {
        var store = new RevocationStore();
        store.RevokeIp("192.168.1.100");
        Assert.True(store.IsIpRevoked("192.168.1.100"));
    }

    [Fact]
    public void UnrevokeIp_RemovesFromStore()
    {
        var store = new RevocationStore();
        store.RevokeIp("10.0.0.1");
        store.UnrevokeIp("10.0.0.1");
        Assert.False(store.IsIpRevoked("10.0.0.1"));
    }

    [Fact]
    public void RevokedIps_ReturnsAllBannedIps()
    {
        var store = new RevocationStore();
        store.RevokeIp("1.1.1.1");
        store.RevokeIp("2.2.2.2");
        Assert.Contains("1.1.1.1", store.RevokedIps);
        Assert.Contains("2.2.2.2", store.RevokedIps);
    }
}
