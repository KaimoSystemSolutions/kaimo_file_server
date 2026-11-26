using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Web.Components.Search;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Search;

/// <summary>
/// <see cref="HomeLabels.DisplayPath(string)"/> labels a path inside the home-folder share for
/// views that are not search hits (backup restore, backup changes, client devices): the first
/// segment, a home named by its owner's GUID, is shown as the owner's username.
/// </summary>
public class HomeLabelsPathTests
{
    private readonly User _bob = new(Guid.NewGuid(), "Bob Example", "bob", "hash", "nt");

    private HomeLabels Labels() => HomeLabels.FromUsers([_bob]);

    [Fact]
    public void A_home_folder_itself_is_shown_as_its_owner()
        => Assert.Equal("bob", Labels().DisplayPath(_bob.Id.ToString("D")));

    [Fact]
    public void A_path_inside_a_home_keeps_everything_below_the_home()
        => Assert.Equal("bob/Docs/a.txt", Labels().DisplayPath($"{_bob.Id:D}/Docs/a.txt"));

    [Fact]
    public void The_guid_is_matched_case_insensitively()
        => Assert.Equal("bob/Docs", Labels().DisplayPath($"{_bob.Id.ToString("D").ToUpperInvariant()}/Docs"));

    [Fact]
    public void Backslashes_and_surrounding_slashes_are_tolerated()
        => Assert.Equal("bob/Docs", Labels().DisplayPath($"/{_bob.Id:D}\\Docs/"));

    [Theory]
    [InlineData("")]
    [InlineData("Shared/notes.txt")]
    [InlineData("00000000-0000-0000-0000-000000000001/x")]
    public void A_path_whose_first_segment_names_no_user_is_left_as_it_is(string path)
        => Assert.Equal(path, Labels().DisplayPath(path));

    [Fact]
    public void Only_the_first_segment_is_labelled()
        => Assert.Equal($"bob/{_bob.Id:D}", Labels().DisplayPath($"{_bob.Id:D}/{_bob.Id:D}"));

    [Fact]
    public void No_labels_never_change_a_path()
        => Assert.Equal($"{_bob.Id:D}/Docs", HomeLabels.None.DisplayPath($"{_bob.Id:D}/Docs"));

    // ── ResolvePath: a typed path naming a home by its owner's username ──

    [Theory]
    [InlineData("bob")]
    [InlineData("BOB")]
    public void A_username_in_the_first_segment_is_resolved_to_the_home_folder(string username)
        => Assert.Equal(_bob.Id.ToString("D"), Labels().ResolvePath(username));

    [Fact]
    public void A_resolved_path_keeps_everything_below_the_home()
        => Assert.Equal($"{_bob.Id:D}/Docs/a.txt", Labels().ResolvePath("bob/Docs/a.txt"));

    [Fact]
    public void Only_the_first_segment_is_resolved()
        => Assert.Equal($"{_bob.Id:D}/bob", Labels().ResolvePath("bob/bob"));

    [Theory]
    [InlineData("")]
    [InlineData("alice/Docs")]
    public void A_path_naming_no_user_is_not_resolved(string path)
        => Assert.Equal(path, Labels().ResolvePath(path));

    [Fact]
    public void A_path_that_already_names_the_home_folder_is_kept()
        => Assert.Equal($"{_bob.Id:D}/Docs", Labels().ResolvePath($"{_bob.Id:D}/Docs"));

    // ── FirstSegment: the home a share-relative path lies in ──

    [Theory]
    [InlineData("a/b/c", "a")]
    [InlineData("/a\\b/", "a")]
    [InlineData("a", "a")]
    [InlineData("", "")]
    public void The_first_segment_is_taken_from_the_normalized_path(string path, string expected)
        => Assert.Equal(expected, HomeLabels.FirstSegment(path));
}
