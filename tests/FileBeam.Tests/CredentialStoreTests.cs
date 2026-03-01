namespace FileBeam.Tests;

public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _tempDir;

    public CredentialStoreTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("fb_creds_").FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteFile(string content)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return path;
    }

    // ── LoadFile ──────────────────────────────────────────────────────────────

    [Fact]
    public void LoadFile_MissingFile_ReturnsEmptyDictionary()
    {
        var result = CredentialStore.LoadFile(Path.Combine(_tempDir, "nonexistent.txt"));
        Assert.Empty(result);
    }

    [Fact]
    public void LoadFile_EmptyFile_ReturnsEmptyDictionary()
    {
        var path = WriteFile("");
        Assert.Empty(CredentialStore.LoadFile(path));
    }

    [Fact]
    public void LoadFile_ValidEntries_ParsesCorrectly()
    {
        var path = WriteFile("alice:secret\nbob:hunter2\n");
        var creds = CredentialStore.LoadFile(path);

        Assert.Equal(2, creds.Count);
        Assert.Equal("secret",  creds["alice"].Password);
        Assert.Equal("hunter2", creds["bob"].Password);
    }

    [Fact]
    public void LoadFile_NoRole_DefaultsToRw()
    {
        var path = WriteFile("alice:secret\n");
        Assert.Equal("rw", CredentialStore.LoadFile(path)["alice"].Role);
    }

    [Fact]
    public void LoadFile_AdminRole_Parsed()
    {
        var path = WriteFile("alice:secret:admin\n");
        var cred = CredentialStore.LoadFile(path)["alice"];
        Assert.Equal("secret", cred.Password);
        Assert.Equal("admin",  cred.Role);
    }

    [Fact]
    public void LoadFile_RoRole_Parsed()
    {
        var path = WriteFile("carol:pass:ro\n");
        var cred = CredentialStore.LoadFile(path)["carol"];
        Assert.Equal("pass", cred.Password);
        Assert.Equal("ro",   cred.Role);
    }

    [Fact]
    public void LoadFile_WoRole_Parsed()
    {
        var path = WriteFile("dave:pass:wo\n");
        var cred = CredentialStore.LoadFile(path)["dave"];
        Assert.Equal("pass", cred.Password);
        Assert.Equal("wo",   cred.Role);
    }

    [Fact]
    public void LoadFile_RoleIsCaseInsensitive()
    {
        var path = WriteFile("alice:secret:ADMIN\n");
        Assert.Equal("admin", CredentialStore.LoadFile(path)["alice"].Role);
    }

    [Fact]
    public void LoadFile_PasswordWithColons_NoRole_UsesFullRest()
    {
        // Last segment "extra" is not a role keyword → whole rest is the password.
        var path = WriteFile("alice:pass:word:extra\n");
        var cred = CredentialStore.LoadFile(path)["alice"];
        Assert.Equal("pass:word:extra", cred.Password);
        Assert.Equal("rw",              cred.Role);
    }

    [Fact]
    public void LoadFile_PasswordWithColons_TrailingRole_DetectsRole()
    {
        // Last segment is a valid role — strip it and use the rest as password.
        var path = WriteFile("alice:pass:word:admin\n");
        var cred = CredentialStore.LoadFile(path)["alice"];
        Assert.Equal("pass:word", cred.Password);
        Assert.Equal("admin",     cred.Role);
    }

    [Fact]
    public void LoadFile_CommentLines_AreSkipped()
    {
        var path = WriteFile("# this is a comment\nalice:secret\n# another comment\n");
        var creds = CredentialStore.LoadFile(path);

        Assert.Single(creds);
        Assert.True(creds.ContainsKey("alice"));
    }

    [Fact]
    public void LoadFile_BlankLines_AreSkipped()
    {
        var path = WriteFile("\n\nalice:secret\n\n\n");
        Assert.Single(CredentialStore.LoadFile(path));
    }

    [Fact]
    public void LoadFile_DuplicateUsername_LastEntryWins()
    {
        var path = WriteFile("alice:first\nalice:second\n");
        var creds = CredentialStore.LoadFile(path);

        Assert.Single(creds);
        Assert.Equal("second", creds["alice"].Password);
    }

    [Fact]
    public void LoadFile_MalformedLine_NoColon_IsSkipped()
    {
        var path = WriteFile("nocolon\nalice:secret\n");
        var creds = CredentialStore.LoadFile(path);

        Assert.Single(creds);
        Assert.True(creds.ContainsKey("alice"));
    }

    [Fact]
    public void LoadFile_EmptyUsername_IsSkipped()
    {
        var path = WriteFile(":password\nalice:secret\n");
        var creds = CredentialStore.LoadFile(path);

        Assert.Single(creds);
        Assert.True(creds.ContainsKey("alice"));
    }

    [Fact]
    public void LoadFile_EmptyPassword_IsSkipped()
    {
        var path = WriteFile("alice:\nbob:secret\n");
        var creds = CredentialStore.LoadFile(path);

        Assert.Single(creds);
        Assert.True(creds.ContainsKey("bob"));
    }

    [Fact]
    public void LoadFile_EmptyPasswordAfterRoleStrip_IsSkipped()
    {
        // "alice::admin" → after stripping :admin role, password is "" → skip
        var path = WriteFile("alice::admin\nbob:secret\n");
        var creds = CredentialStore.LoadFile(path);

        Assert.Single(creds);
        Assert.True(creds.ContainsKey("bob"));
    }

    [Fact]
    public void LoadFile_WhitespaceAroundEntry_IsTrimmed()
    {
        var path = WriteFile("  alice:secret  \n");
        var creds = CredentialStore.LoadFile(path);

        Assert.Single(creds);
        Assert.Equal("secret", creds["alice"].Password);
    }

    // ── LoadFileWithDiagnostics ───────────────────────────────────────────────

    [Fact]
    public void LoadFileWithDiagnostics_NoColon_ProducesWarning()
    {
        var path = WriteFile("nocolon\nalice:secret\n");
        var (creds, warnings) = CredentialStore.LoadFileWithDiagnostics(path);

        Assert.Single(creds);
        Assert.Single(warnings);
        Assert.Equal(1,          warnings[0].LineNumber);
        Assert.Equal("nocolon",  warnings[0].LineText);
        Assert.Contains("':'",   warnings[0].Reason);
    }

    [Fact]
    public void LoadFileWithDiagnostics_EmptyUsername_ProducesWarning()
    {
        var path = WriteFile(":password\nalice:secret\n");
        var (_, warnings) = CredentialStore.LoadFileWithDiagnostics(path);

        Assert.Single(warnings);
        Assert.Contains("username", warnings[0].Reason);
    }

    [Fact]
    public void LoadFileWithDiagnostics_EmptyPassword_ProducesWarning()
    {
        var path = WriteFile("alice:\nbob:secret\n");
        var (_, warnings) = CredentialStore.LoadFileWithDiagnostics(path);

        Assert.Single(warnings);
        Assert.Contains("password", warnings[0].Reason);
    }

    [Fact]
    public void LoadFileWithDiagnostics_CommentsAndBlankLines_NoWarnings()
    {
        var path = WriteFile("# comment\n\nalice:secret\n");
        var (creds, warnings) = CredentialStore.LoadFileWithDiagnostics(path);

        Assert.Single(creds);
        Assert.Empty(warnings);
    }

    [Fact]
    public void LoadFileWithDiagnostics_MultipleErrors_CorrectLineNumbers()
    {
        var path = WriteFile("alice:ok\nnocolon\nbob:ok\n:emptyuser\n");
        var (creds, warnings) = CredentialStore.LoadFileWithDiagnostics(path);

        Assert.Equal(2, creds.Count);
        Assert.Equal(2, warnings.Count);
        Assert.Equal(2, warnings[0].LineNumber); // "nocolon"
        Assert.Equal(4, warnings[1].LineNumber); // ":emptyuser"
    }

    [Fact]
    public void LoadFileWithDiagnostics_MissingFile_ReturnsEmptyWithNoWarnings()
    {
        var (creds, warnings) = CredentialStore.LoadFileWithDiagnostics(
            Path.Combine(_tempDir, "missing.txt"));

        Assert.Empty(creds);
        Assert.Empty(warnings);
    }

    // ── VerifyPassword ────────────────────────────────────────────────────────

    [Fact]
    public void VerifyPassword_MatchingPasswords_ReturnsTrue()
    {
        Assert.True(CredentialStore.VerifyPassword("correct", "correct"));
    }

    [Fact]
    public void VerifyPassword_NonMatchingPasswords_ReturnsFalse()
    {
        Assert.False(CredentialStore.VerifyPassword("wrong", "correct"));
    }

    [Fact]
    public void VerifyPassword_EmptyStrings_ReturnsTrue()
    {
        Assert.True(CredentialStore.VerifyPassword("", ""));
    }

    [Fact]
    public void VerifyPassword_CaseSensitive()
    {
        Assert.False(CredentialStore.VerifyPassword("Secret", "secret"));
    }
}
