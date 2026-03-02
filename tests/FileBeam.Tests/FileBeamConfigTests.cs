namespace FileBeam.Tests;

public class FileBeamConfigTests
{
    // ── ToCliCommand ──────────────────────────────────────────────────────────

    [Fact]
    public void ToCliCommand_DefaultsOnly_ContainsDownloadFlag()
    {
        var cmd = FileBeamConfig.ToCliCommand(
            download: @"C:\files", upload: @"C:\files", port: 8080,
            adminUsername: null, invitesFile: null,
            readOnly: false, perSender: false,
            maxFileSize: 0, maxUploadBytes: 0, maxUploadTotal: 0, maxUploadSize: null,
            tlsCert: null, tlsKey: null,
            shareTtl: 3600, auditLog: null, auditLogMaxSize: 0,
            rateLimit: 60, logLevel: "info");

        Assert.StartsWith("filebeam.exe", cmd);
        Assert.Contains("--download", cmd);
        Assert.Contains(@"C:\files", cmd);
    }

    [Fact]
    public void ToCliCommand_DefaultPort_DoesNotIncludePortFlag()
    {
        var cmd = FileBeamConfig.ToCliCommand(
            download: "/srv", upload: "/srv", port: 8080,
            adminUsername: null, invitesFile: null,
            readOnly: false, perSender: false,
            maxFileSize: 0, maxUploadBytes: 0, maxUploadTotal: 0, maxUploadSize: null,
            tlsCert: null, tlsKey: null,
            shareTtl: 3600, auditLog: null, auditLogMaxSize: 0,
            rateLimit: 60, logLevel: "info");

        Assert.DoesNotContain("--port", cmd);
    }

    [Fact]
    public void ToCliCommand_NonDefaultPort_IncludesPortFlag()
    {
        var cmd = FileBeamConfig.ToCliCommand(
            download: "/srv", upload: "/srv", port: 9090,
            adminUsername: null, invitesFile: null,
            readOnly: false, perSender: false,
            maxFileSize: 0, maxUploadBytes: 0, maxUploadTotal: 0, maxUploadSize: null,
            tlsCert: null, tlsKey: null,
            shareTtl: 3600, auditLog: null, auditLogMaxSize: 0,
            rateLimit: 60, logLevel: "info");

        Assert.Contains("--port 9090", cmd);
    }

    [Fact]
    public void ToCliCommand_SameUploadAndDownload_DoesNotIncludeUploadFlag()
    {
        var cmd = FileBeamConfig.ToCliCommand(
            download: "/srv", upload: "/srv", port: 8080,
            adminUsername: null, invitesFile: null,
            readOnly: false, perSender: false,
            maxFileSize: 0, maxUploadBytes: 0, maxUploadTotal: 0, maxUploadSize: null,
            tlsCert: null, tlsKey: null,
            shareTtl: 3600, auditLog: null, auditLogMaxSize: 0,
            rateLimit: 60, logLevel: "info");

        Assert.DoesNotContain("--upload", cmd);
    }

    [Fact]
    public void ToCliCommand_DifferentUpload_IncludesUploadFlag()
    {
        var cmd = FileBeamConfig.ToCliCommand(
            download: "/srv", upload: "/drop", port: 8080,
            adminUsername: null, invitesFile: null,
            readOnly: false, perSender: false,
            maxFileSize: 0, maxUploadBytes: 0, maxUploadTotal: 0, maxUploadSize: null,
            tlsCert: null, tlsKey: null,
            shareTtl: 3600, auditLog: null, auditLogMaxSize: 0,
            rateLimit: 60, logLevel: "info");

        Assert.Contains("--upload", cmd);
        Assert.Contains("/drop", cmd);
    }

    [Fact]
    public void ToCliCommand_ReadOnlyAndPerSender_IncludesFlags()
    {
        var cmd = FileBeamConfig.ToCliCommand(
            download: "/srv", upload: "/srv", port: 8080,
            adminUsername: null, invitesFile: null,
            readOnly: true, perSender: true,
            maxFileSize: 0, maxUploadBytes: 0, maxUploadTotal: 0, maxUploadSize: null,
            tlsCert: null, tlsKey: null,
            shareTtl: 3600, auditLog: null, auditLogMaxSize: 0,
            rateLimit: 60, logLevel: "info");

        Assert.Contains("--readonly", cmd);
        Assert.Contains("--per-sender", cmd);
    }

    [Fact]
    public void ToCliCommand_AdminPasswordNeverIncluded()
    {
        // ToCliCommand never includes --admin-password (security — use env var or key file)
        var cmd = FileBeamConfig.ToCliCommand(
            download: "/srv", upload: "/srv", port: 8080,
            adminUsername: null, invitesFile: null,
            readOnly: false, perSender: false,
            maxFileSize: 0, maxUploadBytes: 0, maxUploadTotal: 0, maxUploadSize: null,
            tlsCert: null, tlsKey: null,
            shareTtl: 3600, auditLog: null, auditLogMaxSize: 0,
            rateLimit: 60, logLevel: "info");

        Assert.DoesNotContain("--admin-password", cmd);
        Assert.DoesNotContain("--password", cmd);
    }

    [Fact]
    public void ToCliCommand_NonDefaultLogLevel_IncludesLogLevelFlag()
    {
        var cmd = FileBeamConfig.ToCliCommand(
            download: "/srv", upload: "/srv", port: 8080,
            adminUsername: null, invitesFile: null,
            readOnly: false, perSender: false,
            maxFileSize: 0, maxUploadBytes: 0, maxUploadTotal: 0, maxUploadSize: null,
            tlsCert: null, tlsKey: null,
            shareTtl: 3600, auditLog: null, auditLogMaxSize: 0,
            rateLimit: 60, logLevel: "debug");

        Assert.Contains("--log-level debug", cmd);
    }

    [Fact]
    public void ToCliCommand_DefaultLogLevel_DoesNotIncludeLogLevelFlag()
    {
        var cmd = FileBeamConfig.ToCliCommand(
            download: "/srv", upload: "/srv", port: 8080,
            adminUsername: null, invitesFile: null,
            readOnly: false, perSender: false,
            maxFileSize: 0, maxUploadBytes: 0, maxUploadTotal: 0, maxUploadSize: null,
            tlsCert: null, tlsKey: null,
            shareTtl: 3600, auditLog: null, auditLogMaxSize: 0,
            rateLimit: 60, logLevel: "info");

        Assert.DoesNotContain("--log-level", cmd);
    }

    [Fact]
    public void ToCliCommand_CustomAdminUsernameAndInvitesFile_IncludesBothFlags()
    {
        var cmd = FileBeamConfig.ToCliCommand(
            download: "/srv", upload: "/srv", port: 8080,
            adminUsername: "sysop", invitesFile: "/etc/invites.json",
            readOnly: false, perSender: false,
            maxFileSize: 0, maxUploadBytes: 0, maxUploadTotal: 0, maxUploadSize: null,
            tlsCert: null, tlsKey: null,
            shareTtl: 3600, auditLog: null, auditLogMaxSize: 0,
            rateLimit: 60, logLevel: "info");

        Assert.Contains("--admin-username", cmd);
        Assert.Contains("sysop", cmd);
        Assert.Contains("--invites-file", cmd);
        Assert.Contains("/etc/invites.json", cmd);
    }

    [Fact]
    public void ToCliCommand_DefaultAdminUsername_DoesNotIncludeAdminUsernameFlag()
    {
        var cmd = FileBeamConfig.ToCliCommand(
            download: "/srv", upload: "/srv", port: 8080,
            adminUsername: null, invitesFile: null,
            readOnly: false, perSender: false,
            maxFileSize: 0, maxUploadBytes: 0, maxUploadTotal: 0, maxUploadSize: null,
            tlsCert: null, tlsKey: null,
            shareTtl: 3600, auditLog: null, auditLogMaxSize: 0,
            rateLimit: 60, logLevel: "info");

        Assert.DoesNotContain("--admin-username", cmd);
    }
}
