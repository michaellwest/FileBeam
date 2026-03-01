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
        Assert.Equal("secret",  creds["alice"]);
        Assert.Equal("hunter2", creds["bob"]);
    }

    [Fact]
    public void LoadFile_PasswordWithColons_UsesFirstColonOnly()
    {
        // Password itself contains a colon — everything after the first colon is the password.
        var path = WriteFile("alice:pass:word:extra\n");
        var creds = CredentialStore.LoadFile(path);

        Assert.Single(creds);
        Assert.Equal("pass:word:extra", creds["alice"]);
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
        var creds = CredentialStore.LoadFile(path);

        Assert.Single(creds);
    }

    [Fact]
    public void LoadFile_DuplicateUsername_LastEntryWins()
    {
        var path = WriteFile("alice:first\nalice:second\n");
        var creds = CredentialStore.LoadFile(path);

        Assert.Single(creds);
        Assert.Equal("second", creds["alice"]);
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
    public void LoadFile_WhitespaceAroundEntry_IsTrimmed()
    {
        var path = WriteFile("  alice:secret  \n");
        var creds = CredentialStore.LoadFile(path);

        // The whole line is trimmed, but only the outer whitespace.
        // "alice:secret" is the result after trim.
        Assert.Single(creds);
        Assert.Equal("secret", creds["alice"]);
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
