using System.Text;
using Kaimo_File_Server.Core.Security;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// TOTP (RFC 6238) and its helpers: the RFC test vectors, the ±1 step window, replay
/// protection through the last used step, code normalization and the base32 secret encoding.
/// </summary>
public class TotpTests
{
    // RFC 6238 appendix B, SHA-1 seed. The RFC lists 8-digit codes; a 6-digit code is the
    // same truncated value modulo 10^6, i.e. its last six digits.
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Compute_MatchesRfc6238Vectors(long unixSeconds, string expected)
    {
        Assert.Equal(expected, Totp.Compute(RfcSecret, unixSeconds / Totp.StepSeconds));
    }

    [Fact]
    public void Verify_CurrentStep_ReturnsThatStep()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        long step = Totp.StepAt(now);

        Assert.Equal(step, Totp.Verify(RfcSecret, "050471", now, lastUsedStep: 0));
    }

    [Fact]
    public void Verify_AcceptsOneStepOfDriftInEitherDirection()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        long step = Totp.StepAt(now);

        Assert.Equal(step - 1, Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step - 1), now, 0));
        Assert.Equal(step + 1, Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step + 1), now, 0));
    }

    [Fact]
    public void Verify_RejectsCodesOutsideTheWindow()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        long step = Totp.StepAt(now);

        Assert.Null(Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step - 2), now, 0));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step + 2), now, 0));
    }

    [Fact]
    public void Verify_RejectsAStepThatWasAlreadyUsed()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        long step = Totp.StepAt(now);
        string code = Totp.Compute(RfcSecret, step);

        Assert.Null(Totp.Verify(RfcSecret, code, now, lastUsedStep: step));
        // An earlier step must not be accepted after a later one was used either.
        Assert.Null(Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step - 1), now, lastUsedStep: step));
    }

    [Fact]
    public void Verify_NextStepIsStillAcceptedAfterTheCurrentOneWasUsed()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        long step = Totp.StepAt(now);

        Assert.Equal(step + 1, Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step + 1), now, lastUsedStep: step));
    }

    [Theory]
    [InlineData("050 471")]
    [InlineData(" 050471 ")]
    [InlineData("050-471")]
    public void Verify_IgnoresSpacesAndDashes(string input)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        Assert.NotNull(Totp.Verify(RfcSecret, input, now, 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("05047")]
    [InlineData("0504712")]
    [InlineData("05047a")]
    [InlineData("٠٥٠٤٧١")] // Arabic-Indic digits: char.IsDigit says yes, but they are not ASCII digits.
    public void Verify_RejectsMalformedCodes(string? input)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        Assert.Null(Totp.Verify(RfcSecret, input, now, 0));
    }

    [Fact]
    public void NewSecret_Is160BitsAndRandom()
    {
        var a = Totp.NewSecret();
        var b = Totp.NewSecret();

        Assert.Equal(20, a.Length);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_EncodesRfc4648VectorsWithoutPadding(string input, string expected)
    {
        Assert.Equal(expected, Base32.Encode(Encoding.ASCII.GetBytes(input)));
    }

    [Fact]
    public void ProvisioningUri_ContainsIssuerAccountAndSecret()
    {
        var uri = Totp.ProvisioningUri(Encoding.ASCII.GetBytes("foobar"), "Kaimo File Server", "erika");

        Assert.Equal(
            "otpauth://totp/Kaimo%20File%20Server:erika?secret=MZXW6YTBOI&issuer=Kaimo%20File%20Server&algorithm=SHA1&digits=6&period=30",
            uri);
    }
}
