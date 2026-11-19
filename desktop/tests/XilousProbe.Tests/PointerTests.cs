using XilousProbe.Overlay;
using XilousProbe.Uia;

namespace XilousProbe.Tests;

public class PointerFormatterTests
{
    private static ElementInfo SaveButton() => new()
    {
        ControlType = "Button",
        Name = "Save",
        AutomationId = "SaveButton",
        ClassName = "Button",
        FrameworkId = "WPF",
        Bounds = new PxRect(1520, 348, 88, 32),
        ProcessId = 4312,
        ProcessName = "Fieldwire.exe",
        WindowTitle = "Projects",
        WindowBounds = new PxRect(1400, 300, 1200, 800),
        Ancestors = [new("Window", "Projects", ""), new("Pane", "Toolbar", "")],
        Index = 3,
        SiblingCount = 5,
        Previous = new("Button", "Open", ""),
        Next = new("Button", "Close", ""),
        IsEnabled = true,
        IsKeyboardFocusable = true,
        Patterns = ["Invoke"],
    };

    [Fact]
    public void Formats_a_full_pointer()
    {
        var expected = string.Join(Environment.NewLine,
            "```",
            "# app: Fieldwire.exe (pid 4312), window \"Projects\"",
            "# framework: WPF",
            "# element: Button \"Save\" (automationId: SaveButton, class: Button)",
            "# path: Window \"Projects\" > Pane \"Toolbar\" > Button \"Save\"",
            "# position: child 3 of 5 in Pane \"Toolbar\"",
            "#   after Button \"Open\", before Button \"Close\"",
            "# bounds: 120,48 88x32 in window, screen 1520,348 88x32 (physical px)",
            "# state: enabled, focusable · patterns: Invoke",
            "```");
        Assert.Equal(expected, PointerFormatter.Format(SaveButton()));
    }

    [Fact]
    public void Names_the_saved_screenshot_last()
    {
        var path = @"C:\Users\me\AppData\Local\XilousProbe\shots\20261119-130502-123-Button-Save.png";
        var lines = PointerFormatter.Format(SaveButton(), path).Split(Environment.NewLine);

        Assert.Equal($"# screenshot: {path}", lines[^2]);
        Assert.Equal("```", lines[^1]);
        Assert.DoesNotContain("# screenshot:", PointerFormatter.Format(SaveButton()));
    }

    [Fact]
    public void Leaves_out_what_is_unknown()
    {
        var bare = new ElementInfo { ControlType = "Pane", ProcessId = 7, ProcessName = "", FrameworkId = "" };
        var text = PointerFormatter.Format(bare);
        Assert.Contains("# app: unknown (pid 7)", text);
        Assert.Contains("# framework: unknown", text);
        Assert.Contains("# element: Pane", text);
        Assert.DoesNotContain("# path:", text);
        Assert.DoesNotContain("# position:", text);
        Assert.DoesNotContain("# bounds:", text);
        Assert.DoesNotContain("# contains:", text);
        Assert.DoesNotContain("# nearby:", text);
    }

    private static ElementInfo Panel() => new()
    {
        ControlType = "Group",
        ClassName = "MuiPaper-root MuiPaper-outlined",
        FrameworkId = "Chrome",
        ProcessId = 4000,
        ProcessName = "Fieldwire Client.exe",
        Ancestors = [new("Window", "Fieldwire Client", ""), new("Document", "Fieldwire Client", ""), new("Group", "", "")],
    };

    [Fact]
    public void Says_what_the_element_contains_right_after_naming_it()
    {
        var panel = Panel() with
        {
            Contents = [new("Text", "Activity · 3", ""), new("Button", "Reset place", ""), new("Edit", "", "jobFilter")],
        };

        var lines = PointerFormatter.Format(panel).Split(Environment.NewLine);

        var at = Array.FindIndex(lines, l => l.StartsWith("# element:"));
        Assert.Equal("# contains: Text \"Activity · 3\", Button \"Reset place\", Edit #jobFilter", lines[at + 1]);
        Assert.DoesNotContain("# nearby:", string.Join("\n", lines));
    }

    [Fact]
    public void Says_what_is_around_an_element_with_no_text_and_where_it_was_read()
    {
        var icon = Panel() with
        {
            ControlType = "Image",
            Nearby = [new("Button", "Reset place", ""), new("Button", "Close activity list", "")],
            NearbyIn = new("Group", "", ""),
            NearbyLevels = 1,
        };
        Assert.Contains("# nearby: Button \"Reset place\", Button \"Close activity list\" (in Group, the parent)", PointerFormatter.Format(icon));

        var deeper = icon with { NearbyIn = new("Pane", "Toolbar", ""), NearbyLevels = 3 };
        Assert.Contains("(in Pane \"Toolbar\", 3 levels up)", PointerFormatter.Format(deeper));
    }

    [Fact]
    public void Names_an_unnamed_element_by_automation_id()
    {
        Assert.Equal("Edit #SearchBox", new ElementStep("Edit", "", "SearchBox").ToString());
        Assert.Equal("Edit", new ElementStep("Edit", "  ", "").ToString());
    }

    [Fact]
    public void Clips_long_names_and_flattens_line_breaks()
    {
        var step = new ElementStep("Text", "first line\nsecond line that keeps going and going and going", "");
        var s = step.ToString();
        Assert.DoesNotContain("\n", s);
        Assert.EndsWith("…\"", s);
    }

    [Fact]
    public void Reports_value_only_when_it_says_something_new()
    {
        var same = SaveButton() with { Value = "Save" };
        Assert.DoesNotContain("# value:", PointerFormatter.Format(same));
        var differs = SaveButton() with { ControlType = "Edit", Value = "draft-2" };
        Assert.Contains("# value: draft-2", PointerFormatter.Format(differs));
    }

    [Fact]
    public void Label_reads_identity_then_detail_then_source()
    {
        var (title, detail, source) = PointerFormatter.Label(SaveButton());
        Assert.Equal("Button \"Save\"", title);
        Assert.Equal("#SaveButton  ·  Button  ·  88×32", detail);
        Assert.Equal("Fieldwire.exe  ·  WPF", source);
    }

    [Theory]
    [InlineData("Chrome", "Chromium (Chrome, Edge or Electron)")]
    [InlineData("WinForm", "WinForms")]
    [InlineData("XAML", "WinUI/UWP (XAML)")]
    [InlineData("Qt", "Qt")]
    public void Names_frameworks(string id, string expected) =>
        Assert.Equal(expected, new ElementInfo { FrameworkId = id }.Framework);
}

public class RedlineTests
{
    [Fact]
    public void Measures_the_gap_to_an_element_on_the_right()
    {
        var m = Assert.Single(Redline.Between(new PxRect(0, 0, 100, 40), new PxRect(130, 10, 50, 40)));
        Assert.Equal(new Measure(100, 25, 130, 25), m);
        Assert.Equal(30, m.Length);
        Assert.True(m.IsHorizontal);
    }

    [Fact]
    public void Measures_both_gaps_to_a_diagonal_element()
    {
        var ms = Redline.Between(new PxRect(0, 0, 100, 100), new PxRect(150, 160, 20, 20));
        Assert.Equal(2, ms.Count);
        Assert.Contains(ms, m => m.IsHorizontal && m.Length == 50);
        Assert.Contains(ms, m => !m.IsHorizontal && m.Length == 60);
    }

    [Fact]
    public void Measures_insets_when_one_contains_the_other()
    {
        var ms = Redline.Between(new PxRect(10, 20, 80, 60), new PxRect(0, 0, 100, 100));
        Assert.Equal([10, 10, 20, 20], ms.Select(m => m.Length).ToList());
        // Same answer whichever one is selected.
        Assert.Equal(ms, Redline.Between(new PxRect(0, 0, 100, 100), new PxRect(10, 20, 80, 60)));
    }

    [Fact]
    public void Overlapping_elements_have_no_gap()
    {
        Assert.Empty(Redline.Between(new PxRect(0, 0, 100, 100), new PxRect(50, 50, 100, 100)));
    }
}
