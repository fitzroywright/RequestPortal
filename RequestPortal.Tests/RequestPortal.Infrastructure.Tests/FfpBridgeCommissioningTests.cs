using FfpManager.Bridge;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class FfpBridgeCommissioningTests
{
    [Theory]
    [InlineData("Server=JMDB01;Database=FFP;Integrated Security=true;Encrypt=true")]
    [InlineData("Server=JMDB01;Database=FFP;Integrated Security=true;Encrypt=true;TrustServerCertificate=true")]
    [InlineData("Server=tcp:JMDB01.fftpj.local,1433;Database=FFP;Integrated Security=true;Encrypt=Strict")]
    public void ApprovedTargetRequiresEncryption(string target) => FfpSqlConnection.ValidateTarget(target);

    [Theory]
    [InlineData("Server=JMDB01;Database=FFPJM;Integrated Security=true")]
    [InlineData("Server=OTHER;Database=FFP;Integrated Security=true")]
    [InlineData("Server=JMDB01;Database=master;Integrated Security=true")]
    [InlineData("Server=JMDB01;Database=FFP;Failover Partner=OTHER")]
    [InlineData("Server=JMDB01;Database=FFP;Encrypt=false")]
    public async Task UnauthorizedTargetsAndInsecureTlsAreRejectedBeforeConnect(string target)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => FfpSqlConnection.OpenAsync(target, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Production", "FFPJM")]
    [InlineData("Rehearsal", "FFP")]
    public void ExplicitTargetAcceptsOnlyItsOwnDatabase(string target, string database)
    {
        FfpSqlConnection.ValidateTarget($"Server=JMDB01;Database={database};Integrated Security=true;Encrypt=true", target);
        string other = database == "FFP" ? "FFPJM" : "FFP";
        Assert.Throws<InvalidOperationException>(() => FfpSqlConnection.ValidateTarget(
            $"Server=JMDB01;Database={other};Integrated Security=true;Encrypt=true", target));
    }

    [Theory]
    [InlineData("Server=OTHER;Database=FFPJM;Integrated Security=true;Encrypt=true")]
    [InlineData("Server=JMDB01;Database=FFPJM;Integrated Security=true;Encrypt=false")]
    [InlineData("Server=JMDB01;Database=FFPJM;User ID=synthetic;Password=synthetic;Encrypt=true")]
    [InlineData("Server=JMDB01;Database=FFPJM;Integrated Security=true;Failover Partner=OTHER;Encrypt=true")]
    [InlineData("Server=JMDB01;Database=FFPJM;Integrated Security=true;Server SPN=MSSQLSvc/OTHER;Encrypt=true")]
    [InlineData("Server=JMDB01;Database=FFPJM;Integrated Security=true;ApplicationIntent=ReadOnly;Encrypt=true")]
    [InlineData("Server=JMDB01;Database=FFPJM;Integrated Security=true;AttachDBFilename=other.mdf;Encrypt=true")]
    public async Task ProductionRejectsUnsafeSettingsBeforeOpening(string connection)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => FfpSqlConnection.OpenAsync(
            connection, "Production", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("production")]
    [InlineData("Other")]
    public void MissingOrUnknownTargetFailsClosed(string target) =>
        Assert.Throws<InvalidOperationException>(() => FfpSqlConnection.ValidateTarget(
            "Server=JMDB01;Database=FFPJM;Integrated Security=true;Encrypt=true", target));

    [Theory]
    [InlineData(@"C:\uploads\bad:name?.pdf", "bad_name_.pdf")]
    [InlineData("NUL.txt", "_NUL.txt")]
    [InlineData("...", "document.bin")]
    public void DocumentNamesWorkOnWindowsAndLinux(string name, string expected) =>
        Assert.Equal(expected, BridgeDocumentPath.SafeFileName(name));

    [Fact]
    public void LinuxDocumentPathIsStoredAsWindowsUnc()
    {
        BridgeOptions options = new() { SupportingDocumentManagerRoot = @"\\nas01.fftpj.local\ffpmanager_data\RequestPortalBridge" };
        Assert.Equal(@"\\nas01.fftpj.local\ffpmanager_data\RequestPortalBridge\ApplicationLetter-1-document.pdf",
            BridgeDocumentPath.ForManager(options, "/mnt/ffpmanager-data/RequestPortalBridge/ApplicationLetter-1-document.pdf"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/mnt/ffpmanager-data/RequestPortalBridge")]
    [InlineData(@"\\nas01.fftpj.local\ffpmanager_data\..\other")]
    public void MissingOrInvalidManagerRootFailsBeforeDocumentWrite(string root)
    {
        Assert.Throws<InvalidOperationException>(() => BridgeDocumentPath.ForManager(new() { SupportingDocumentManagerRoot = root }, "/mnt/document.pdf"));
    }
}
