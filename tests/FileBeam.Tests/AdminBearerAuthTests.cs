using System.Text;

namespace FileBeam.Tests;

/// <summary>
/// Tests for <see cref="AdminAuth"/> — admin Basic Auth, Bearer token auth, and admin password resolution.
/// </summary>
public sealed class AdminBearerAuthTests
{
    // ── AdminAuth.TryAdminBasicAuth ────────────────────────────────────────────

    [Fact]
    public void AdminBasicAuth_CorrectCredentials_ReturnsTrue()
    {
        var header = BasicAuthHeader("admin", "secret");
        var result = AdminAuth.TryAdminBasicAuth(header, "admin", "secret", out var user);
        Assert.True(result);
        Assert.Equal("admin", user);
    }

    [Fact]
    public void AdminBasicAuth_WrongPassword_ReturnsFalse()
    {
        var header = BasicAuthHeader("admin", "wrong");
        Assert.False(AdminAuth.TryAdminBasicAuth(header, "admin", "secret", out _));
    }

    [Fact]
    public void AdminBasicAuth_WrongUsername_ReturnsFalse()
    {
        var header = BasicAuthHeader("other", "secret");
        Assert.False(AdminAuth.TryAdminBasicAuth(header, "admin", "secret", out _));
    }

    [Fact]
    public void AdminBasicAuth_EmptyHeader_ReturnsFalse()
    {
        Assert.False(AdminAuth.TryAdminBasicAuth("", "admin", "secret", out _));
    }

    [Fact]
    public void AdminBasicAuth_BearerHeader_ReturnsFalse()
    {
        Assert.False(AdminAuth.TryAdminBasicAuth("Bearer sometoken", "admin", "secret", out _));
    }

    [Fact]
    public void AdminBasicAuth_MalformedBase64_ReturnsFalse()
    {
        Assert.False(AdminAuth.TryAdminBasicAuth("Basic !!!notbase64", "admin", "secret", out _));
    }

    [Fact]
    public void AdminBasicAuth_CustomAdminUsername_Works()
    {
        var header = BasicAuthHeader("sysop", "mypass");
        var result = AdminAuth.TryAdminBasicAuth(header, "sysop", "mypass", out var user);
        Assert.True(result);
        Assert.Equal("sysop", user);
    }

    [Fact]
    public void AdminBasicAuth_UsernameCaseSensitive_ReturnsFalse()
    {
        var header = BasicAuthHeader("Admin", "secret");
        Assert.False(AdminAuth.TryAdminBasicAuth(header, "admin", "secret", out _));
    }

    // ── AdminAuth.TryBearerAuth ────────────────────────────────────────────────

    [Fact]
    public void BearerAuth_ActiveToken_ReturnsTrue()
    {
        var store  = new InviteStore();
        var invite = store.Create("Alice", "rw", null, "admin");

        var result = AdminAuth.TryBearerAuth($"Bearer {invite.Id}", store, out var role, out var user);

        Assert.True(result);
        Assert.Equal("rw", role);
        Assert.Equal("invite:Alice", user);
    }

    [Fact]
    public void BearerAuth_DoesNotIncrementJoinUseCount()
    {
        var store  = new InviteStore();
        var invite = store.Create("Bob", "ro", null, "admin");

        AdminAuth.TryBearerAuth($"Bearer {invite.Id}", store, out _, out _);
        AdminAuth.TryBearerAuth($"Bearer {invite.Id}", store, out _, out _);

        store.TryGet(invite.Id, out var updated);
        Assert.Equal(0, updated!.UseCount);        // join count unchanged
        Assert.Equal(2, updated!.BearerUseCount);  // Bearer counter incremented
    }

    [Fact]
    public void BearerAuth_RevokedToken_ReturnsFalse()
    {
        var store  = new InviteStore();
        var invite = store.Create("Alice", "rw", null, "admin");
        store.Revoke(invite.Id);

        Assert.False(AdminAuth.TryBearerAuth($"Bearer {invite.Id}", store, out _, out _));
    }

    [Fact]
    public void BearerAuth_ExpiredToken_ReturnsFalse()
    {
        var store  = new InviteStore();
        var expiry = DateTimeOffset.UtcNow.AddSeconds(-1);
        var invite = store.Create("Alice", "rw", expiry, "admin");

        Assert.False(AdminAuth.TryBearerAuth($"Bearer {invite.Id}", store, out _, out _));
    }

    [Fact]
    public void BearerAuth_UnknownToken_ReturnsFalse()
    {
        var store = new InviteStore();
        Assert.False(AdminAuth.TryBearerAuth("Bearer unknowntoken12345", store, out _, out _));
    }

    [Fact]
    public void BearerAuth_EmptyHeader_ReturnsFalse()
    {
        var store = new InviteStore();
        Assert.False(AdminAuth.TryBearerAuth("", store, out _, out _));
    }

    [Fact]
    public void BearerAuth_BasicAuthHeader_ReturnsFalse()
    {
        var store  = new InviteStore();
        var header = BasicAuthHeader("admin", "pass");
        Assert.False(AdminAuth.TryBearerAuth(header, store, out _, out _));
    }

    [Fact]
    public void BearerAuth_AdminRoleToken_ReturnsAdminRole()
    {
        var store  = new InviteStore();
        var invite = store.Create("SysAdmin", "admin", null, "admin");

        AdminAuth.TryBearerAuth($"Bearer {invite.Id}", store, out var role, out _);

        Assert.Equal("admin", role);
    }

    [Fact]
    public void BearerAuth_UserIsFriendlyName()
    {
        var store  = new InviteStore();
        var invite = store.Create("Charlie", "wo", null, "admin");

        AdminAuth.TryBearerAuth($"Bearer {invite.Id}", store, out _, out var user);

        Assert.Equal("invite:Charlie", user);
    }

    // ── AdminAuth.ResolveAdminPassword ─────────────────────────────────────────

    [Fact]
    public void ResolveAdminPassword_EnvVarTakesPrecedence()
    {
        var dir     = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var keyFile = Path.Combine(dir, "filebeam-admin.key");
        try
        {
            var pass = AdminAuth.ResolveAdminPassword("from-env", "from-cli", keyFile, out var generated);
            Assert.Equal("from-env", pass);
            Assert.False(generated);
            Assert.False(File.Exists(keyFile)); // env var path does not write key file
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveAdminPassword_CliFlagUsedWhenNoEnvVar()
    {
        var dir     = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var keyFile = Path.Combine(dir, "filebeam-admin.key");
        try
        {
            var pass = AdminAuth.ResolveAdminPassword(null, "from-cli", keyFile, out var generated);
            Assert.Equal("from-cli", pass);
            Assert.False(generated);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveAdminPassword_ReadsExistingKeyFile()
    {
        var dir     = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var keyFile = Path.Combine(dir, "filebeam-admin.key");
        File.WriteAllText(keyFile, "from-key-file");
        try
        {
            var pass = AdminAuth.ResolveAdminPassword(null, null, keyFile, out var generated);
            Assert.Equal("from-key-file", pass);
            Assert.False(generated);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveAdminPassword_GeneratesAndWritesKeyFileWhenMissing()
    {
        var dir     = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var keyFile = Path.Combine(dir, "filebeam-admin.key");
        try
        {
            var pass = AdminAuth.ResolveAdminPassword(null, null, keyFile, out var generated);
            Assert.True(generated);
            Assert.NotEmpty(pass);
            Assert.True(File.Exists(keyFile));
            Assert.Equal(pass, File.ReadAllText(keyFile));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveAdminPassword_GeneratedPasswordIsSufficientlyLong()
    {
        var dir     = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var keyFile = Path.Combine(dir, "filebeam-admin.key");
        try
        {
            var pass = AdminAuth.ResolveAdminPassword(null, null, keyFile, out _);
            Assert.True(pass.Length >= 16);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveAdminPassword_KeyFileTrimsWhitespace()
    {
        var dir     = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var keyFile = Path.Combine(dir, "filebeam-admin.key");
        File.WriteAllText(keyFile, "  mypassword  \n");
        try
        {
            var pass = AdminAuth.ResolveAdminPassword(null, null, keyFile, out _);
            Assert.Equal("mypassword", pass);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string BasicAuthHeader(string user, string pass) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
}
