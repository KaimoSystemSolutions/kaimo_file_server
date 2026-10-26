using Kaimo_File_Server.Infrastructure.Notifications;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>Choosing the transport security moves a well-known port along; a custom port is kept.</summary>
public sealed class SmtpSettingsPortTests
{
    [Theory]
    [InlineData(587, SmtpSecurity.SslOnConnect, 465)]
    [InlineData(465, SmtpSecurity.StartTls, 587)]
    [InlineData(587, SmtpSecurity.None, 25)]
    [InlineData(25, SmtpSecurity.StartTls, 587)]
    [InlineData(465, SmtpSecurity.Auto, 587)]
    [InlineData(25, SmtpSecurity.Auto, 587)]
    public void ApplySecurityDefaultPort_SwitchesWellKnownPort(int port, SmtpSecurity security, int expected)
    {
        var settings = new SmtpSettings { Port = port, Security = security };

        settings.ApplySecurityDefaultPort();

        Assert.Equal(expected, settings.Port);
    }

    [Theory]
    [InlineData(SmtpSecurity.SslOnConnect)]
    [InlineData(SmtpSecurity.None)]
    [InlineData(SmtpSecurity.Auto)]
    public void ApplySecurityDefaultPort_KeepsCustomPort(SmtpSecurity security)
    {
        var settings = new SmtpSettings { Port = 2525, Security = security };

        settings.ApplySecurityDefaultPort();

        Assert.Equal(2525, settings.Port);
    }

    [Fact]
    public void SettingPort_DoesNotChangeSecurity()
    {
        var settings = new SmtpSettings { Security = SmtpSecurity.StartTls };

        settings.Port = 465;
        settings.Normalize();

        Assert.Equal(SmtpSecurity.StartTls, settings.Security);
    }
}
