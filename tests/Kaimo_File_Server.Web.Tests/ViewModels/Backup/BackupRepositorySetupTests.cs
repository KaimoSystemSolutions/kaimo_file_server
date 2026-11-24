using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Web.Components.ViewModels.Backup;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.Backup;

/// <summary>
/// Adding a repository starts with the intent "create new" or "connect existing". The setup
/// section offers the step that matches intent and probe result, and never offers to
/// initialize a location the administrator wanted to connect (typo in the path) or to connect
/// a repository they wanted to create.
/// </summary>
public class BackupRepositorySetupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Untested_AsksForATest(bool connectExisting)
        => Assert.Equal(BackupRepositorySetupStep.Test, BackupUi.SetupStep(connectExisting, null));

    [Fact]
    public void NewIntent_EmptyLocation_Initializes()
        => Assert.Equal(BackupRepositorySetupStep.Initialize, BackupUi.SetupStep(false, BackupRepositoryProbe.Empty));

    [Fact]
    public void ExistingIntent_ExistingRepository_Connects()
        => Assert.Equal(BackupRepositorySetupStep.Connect, BackupUi.SetupStep(true, BackupRepositoryProbe.Existing));

    [Fact]
    public void ExistingIntent_EmptyLocation_ReportsNothingToConnect_InsteadOfInitializing()
        => Assert.Equal(BackupRepositorySetupStep.NothingToConnect, BackupUi.SetupStep(true, BackupRepositoryProbe.Empty));

    [Fact]
    public void NewIntent_ExistingRepository_ReportsAlreadyExists_InsteadOfConnecting()
        => Assert.Equal(BackupRepositorySetupStep.AlreadyExists, BackupUi.SetupStep(false, BackupRepositoryProbe.Existing));

    [Fact]
    public void NewForm_DefaultsToCreatingANewRepository()
        => Assert.False(new BackupRepositoryForm().ConnectExisting);

    [Fact]
    public void RepositoryDialogKeys_ExistInBothLanguages()
    {
        var markup = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/Kaimo_File_Server.Web/Components/Pages/Backup/Components/BackupRepositories.razor"));
        var used = Regex.Matches(markup, "\"(Web_\\w+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        var en = Keys("Resources.resx");
        var de = Keys("Resources.de.resx");

        Assert.Contains("Web_Backup_Repos_Intent_Existing", used);
        Assert.DoesNotContain(used, k => !en.Contains(k) || !de.Contains(k));
    }

    /// <summary>The password asked for is restic's, not the six-character kit code.</summary>
    [Theory]
    [InlineData("Resources.resx", "restic password")]
    [InlineData("Resources.de.resx", "restic-Passwort")]
    public void ExistingPasswordLabel_SaysResticPassword(string file, string expected)
        => Assert.Equal(expected, Values(file)["Web_Backup_Repos_ExistingPassword"]);

    private static Dictionary<string, string> Values(string file)
        => XDocument.Load(Path.Combine(FindRepoRoot(), "src/Kaimo_File_Server.Core/Language", file))
            .Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty);

    private static HashSet<string> Keys(string file) => Values(file).Keys.ToHashSet();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kaimo_File_Server.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
