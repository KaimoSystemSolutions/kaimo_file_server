using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.Search;
using Kaimo_File_Server.Web.Services.Files;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Search;

/// <summary>
/// Home folders are named by their owner's GUID; a search lists them by the owner's username
/// instead, as folder hits. The candidates are exactly the folders the browser lists at the root
/// of the home-folder share (same ACL-filtered listing), and every path inside a home is shown
/// with the username, so a home has one name everywhere.
/// </summary>
public class HomeFolderSearchTests
{
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IShareRepository> _shares = new();
    private readonly Mock<IFileService> _files = new();
    private readonly Mock<IManagementAuthService> _auth = new();
    private readonly ShareDefinition _homes = new("users", "/data/users", isEnabled: true) { IsUserHomes = true };
    private readonly User _alice = new(Guid.NewGuid(), "Alice Example", "alice", "hash", "nt");
    private readonly User _malik = new(Guid.NewGuid(), "Malik", "malik", "hash", "nt");
    private readonly UserContext _admin = new(new User(Guid.NewGuid(), "Admin", "admin.ali", "hash", "nt"), [], [], []);
    private readonly HomeFolderSearch _sut;

    private static readonly List<(string?, string)> Global = [(null, "")];

    public HomeFolderSearchTests()
    {
        _shares.Setup(r => r.GetByNameAsync("users")).ReturnsAsync(_homes);
        _users.Setup(r => r.GetAllAsync()).ReturnsAsync([_alice, _malik, _admin.User]);
        _auth.Setup(a => a.HasAnyPermissionAsync(_admin, ManagementPermission.AccessHomeContents)).ReturnsAsync(true);
        ListHomes(_alice, _malik, _admin.User);
        var factory = new Mock<IFileServiceFactory>();
        factory.Setup(f => f.CreateForShare(_homes)).Returns(_files.Object);
        _sut = new HomeFolderSearch(_users.Object, _shares.Object, factory.Object, _auth.Object);
    }

    private void ListHomes(params User[] owners)
        => _files.Setup(s => s.ListAsync("", _admin)).ReturnsAsync(owners
            .Select(u => new FileMetadata
            {
                Name = u.Id.ToString("D"), Path = u.Id.ToString("D"), IsDirectory = true,
                ModifiedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            })
            .ToList());

    private static FileDocument Hit(string share, string path) => new()
    {
        Id = path, FileName = path.Split('/')[^1], ShareName = share, AbsolutePath = "", SharePath = path,
        FileType = "file",
    };

    [Fact]
    public async Task A_username_match_is_a_folder_hit_named_like_the_user_that_opens_the_home()
    {
        var result = await _sut.FindAsync("ALIC", _admin, Global);
        var hit = Assert.Single(result.Hits);

        Assert.Equal("alice", hit.FileName);
        Assert.Equal("users", hit.ShareName);
        Assert.Equal(_alice.Id.ToString("D"), hit.SharePath);
        Assert.True(hit.IsDirectory);
        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), hit.Modified);   // date filters see it
        Assert.Equal("alice", result.Labels.DisplayPath(hit));                                // the GUID is not shown
    }

    [Fact]
    public async Task Several_matches_are_sorted_by_username()
    {
        var result = await _sut.FindAsync("li", _admin, Global);

        Assert.Equal(["alice", "malik"], result.Hits.Select(h => h.FileName));   // never the caller's own home
    }

    [Fact]
    public async Task Only_folders_the_browser_lists_are_found()
    {
        // Malik never signed in (no folder) or the listing hides his home: no hit that would 404.
        ListHomes(_alice);

        Assert.Equal(["alice"], (await _sut.FindAsync("li", _admin, Global)).Hits.Select(h => h.FileName));
    }

    [Fact]
    public async Task A_listed_home_is_found_even_when_the_user_has_homes_disabled()
    {
        _alice.HomeDirectoryEnabled = false;   // the folder is kept and the browser still lists it

        Assert.Single((await _sut.FindAsync("alice", _admin, Global)).Hits);
    }

    [Fact]
    public async Task Without_AccessHomeContents_there_are_no_home_hits()
    {
        _auth.Setup(a => a.HasAnyPermissionAsync(_admin, ManagementPermission.AccessHomeContents)).ReturnsAsync(false);

        Assert.Empty((await _sut.FindAsync("alice", _admin, Global)).Hits);
        _files.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()), Times.Never);
    }

    [Fact]
    public async Task A_denied_listing_gives_no_hits()
    {
        _files.Setup(s => s.ListAsync("", _admin)).ThrowsAsync(new UnauthorizedAccessException());

        Assert.Empty((await _sut.FindAsync("alice", _admin, Global)).Hits);
    }

    [Theory]
    [InlineData("users", "")]
    [InlineData("USERS", "/")]
    public async Task The_home_share_root_is_searched_too(string share, string sub)
        => Assert.Single((await _sut.FindAsync("alice", _admin, [(share, sub)])).Hits);

    [Theory]
    [InlineData("projects", "")]
    [InlineData("users", "some-home")]
    public async Task Other_scopes_get_no_home_hits(string share, string sub)
        => Assert.Empty((await _sut.FindAsync("alice", _admin, [(share, sub)])).Hits);

    [Fact]
    public async Task A_scope_outside_the_home_share_loads_no_users()
    {
        var result = await _sut.FindAsync("alice", _admin, [("projects", "")]);

        Assert.Empty(result.Hits);
        _users.Verify(r => r.GetAllAsync(), Times.Never);
        Assert.Equal($"{_alice.Id:D}/a.txt", result.Labels.DisplayPath(Hit("users", $"{_alice.Id:D}/a.txt")));
    }

    [Fact]
    public async Task A_disabled_home_share_gives_no_hits()
    {
        _homes.IsEnabled = false;

        Assert.Empty((await _sut.FindAsync("alice", _admin, Global)).Hits);
    }

    [Fact]
    public async Task Hits_are_capped()
    {
        var many = Enumerable.Range(0, HomeFolderSearch.MaxHits + 3)
            .Select(i => new User(Guid.NewGuid(), $"Team {i}", $"team{i}", "hash", "nt")).ToArray();
        _users.Setup(r => r.GetAllAsync()).ReturnsAsync(many);
        ListHomes(many);

        Assert.Equal(HomeFolderSearch.MaxHits, (await _sut.FindAsync("team", _admin, Global)).Hits.Count);
    }

    [Theory]
    [InlineData("users", "{0}/docs/a.txt", "alice/docs/a.txt")]
    [InlineData("USERS", "{0}", "alice")]
    [InlineData("users", "orphan/a.txt", "orphan/a.txt")]         // no such user: the real name
    [InlineData("projects", "{0}/a.txt", "{0}/a.txt")]            // only the home-folder share
    public async Task Paths_inside_other_homes_show_the_username(string share, string path, string shown)
    {
        // Labels are loaded for any scope that reaches the home-folder share, even inside a home.
        var labels = (await _sut.FindAsync("zzz", _admin, [("users", _alice.Id.ToString("D"))])).Labels;
        var id = _alice.Id.ToString("D");

        Assert.Equal(string.Format(shown, id), labels.DisplayPath(Hit(share, string.Format(path, id))));
    }

    [Fact]
    public void The_results_tree_names_a_home_folder_by_its_username_whatever_hit_created_it()
    {
        var id = _alice.Id.ToString("D");
        var labels = new HomeLabels(new Dictionary<string, string> { [id] = "alice" });

        var root = Assert.Single(SearchResultTree.Build([Hit("users", $"{id}/docs/a.txt")], labels));

        var home = Assert.Single(root.Children);
        Assert.Equal("alice", home.Name);
        Assert.Equal(id, home.SharePath);                                      // navigation keeps the GUID
        Assert.Equal("docs", Assert.Single(home.Children).Name);
    }

    [Fact]
    public void The_name_filter_matches_the_username_in_the_path()
    {
        var id = _alice.Id.ToString("D");
        var labels = new HomeLabels(new Dictionary<string, string> { [id] = "alice" });
        var filter = new SearchResultsFilter("alice", SearchTypeFilter.All, SearchDateFilter.Any, Labels: labels);

        Assert.True(filter.Matches(Hit("users", $"{id}/docs/a.txt"), DateTime.UtcNow));
    }
}
