using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Renci.SshNet.Common;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The keyboard-interactive handler of password-mode SFTP connections must hand the stored
/// password to exactly one hidden prompt, never to a username, OTP or follow-up round.
/// </summary>
public sealed class SftpPasswordPromptTests
{
    [Fact]
    public void SingleHiddenPrompt_ReceivesPassword()
    {
        var prompt = new AuthenticationPrompt(0, isEchoed: false, "Password: ");

        bool answered = SftpStorageConnectionProvider.TryAnswerPasswordPrompt([prompt], "secret", alreadyAnswered: false);

        Assert.True(answered);
        Assert.Equal("secret", prompt.Response);
    }

    [Fact]
    public void FollowUpRound_DoesNotReceivePasswordAgain()
    {
        // e.g. PAM asks for an OTP after the password was accepted.
        var otp = new AuthenticationPrompt(0, isEchoed: false, "Verification code: ");

        bool answered = SftpStorageConnectionProvider.TryAnswerPasswordPrompt([otp], "secret", alreadyAnswered: true);

        Assert.False(answered);
        Assert.Null(otp.Response);
    }

    [Fact]
    public void SeveralPrompts_ReceiveNothing()
    {
        var password = new AuthenticationPrompt(0, isEchoed: false, "Password: ");
        var otp = new AuthenticationPrompt(1, isEchoed: false, "Verification code: ");

        bool answered = SftpStorageConnectionProvider.TryAnswerPasswordPrompt([password, otp], "secret", alreadyAnswered: false);

        Assert.False(answered);
        Assert.Null(password.Response);
        Assert.Null(otp.Response);
    }

    [Fact]
    public void EchoedPrompt_ReceivesNothing()
    {
        // A visible prompt asks for something other than a secret (e.g. a username).
        var visible = new AuthenticationPrompt(0, isEchoed: true, "Login: ");

        bool answered = SftpStorageConnectionProvider.TryAnswerPasswordPrompt([visible], "secret", alreadyAnswered: false);

        Assert.False(answered);
        Assert.Null(visible.Response);
    }

    [Fact]
    public void EmptyInformationRound_IsNotCountedAsAnswer()
    {
        bool answered = SftpStorageConnectionProvider.TryAnswerPasswordPrompt([], "secret", alreadyAnswered: false);

        Assert.False(answered);
    }
}
