using System.IO;
using XilousProbe.Capture;

namespace XilousProbe.Tests;

public sealed class ShotStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xilous-shots-" + Guid.NewGuid().ToString("N"));
    private ShotStore Store => new(_dir);
    private static readonly DateTime Taken = new(2026, 11, 19, 13, 5, 2, 123);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Saves_the_png_under_a_name_that_sorts_by_time_and_says_what_it_shows()
    {
        var path = Store.Save([1, 2, 3], "Button Save", Taken);

        Assert.Equal(Path.Combine(_dir, "20261119-130502-123-Button-Save.png"), path);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void Never_overwrites_a_shot_taken_in_the_same_millisecond()
    {
        var first = Store.Save([1], "Group", Taken);
        var second = Store.Save([2], "Group", Taken);

        Assert.NotEqual(first, second);
        Assert.EndsWith("-Group-2.png", second);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(first));
    }

    [Fact]
    public void Keeps_only_the_newest_and_leaves_other_files_alone()
    {
        Directory.CreateDirectory(_dir);
        var mine = Path.Combine(_dir, "notes.png");
        File.WriteAllBytes(mine, [9]);

        for (var i = 0; i < ShotStore.Keep + 5; i++)
            Store.Save([1], "Group", Taken.AddSeconds(i));

        var shots = Directory.GetFiles(_dir, "2026*.png").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(ShotStore.Keep, shots.Count);
        Assert.Equal("20261119-130507-123-Group.png", shots[0]);   // the five oldest went
        Assert.True(File.Exists(mine));
    }

    [Theory]
    [InlineData("Button Save", "Button-Save")]
    [InlineData("Group ", "Group")]
    [InlineData("Text \"Activity · 3\"", "Text-Activity-3")]
    [InlineData("Hyperlink C:\\temp\\a/b?.png", "Hyperlink-C-temp-a-b-png")]
    [InlineData("··· ", "element")]
    public void Slugs_labels_into_safe_file_name_parts(string label, string expected) =>
        Assert.Equal(expected, ShotStore.Slug(label));

    [Fact]
    public void Slugs_stay_short()
    {
        Assert.True(ShotStore.Slug(new string('a', 200)).Length <= 40);
    }
}
