using XilousProbe.Uia;

namespace XilousProbe.Tests;

public class TextAnchorsTests
{
    /// <summary>A stand-in for an automation element: type, name, id and children.</summary>
    private sealed record Node(string Type, string Name, string Id, params Node[] Kids)
    {
        public static Node Box(params Node[] kids) => new("Group", "", "", kids);
    }

    private static List<ElementStep> Collect(Node root, string ownName = "", Func<bool>? outOfTime = null, List<Node>? read = null) =>
        TextAnchors.Collect(
            root,
            n => n.Kids,
            n => { read?.Add(n); return new ElementStep(n.Type, n.Name, n.Id); },
            [ownName],
            outOfTime ?? (() => false));

    private static string Line(List<ElementStep> steps) => string.Join(", ", steps);

    [Fact]
    public void Reads_names_in_reading_order_through_unnamed_containers()
    {
        // The Activity list in the Fieldwire client: a Paper with a header row and a list.
        var panel = Node.Box(
            Node.Box(
                new Node("Text", "Activity · 3", ""),
                new Node("Button", "Reset place", "", new Node("Text", "Reset place", "")),
                new Node("Button", "Close activity list", "")),
            Node.Box(Node.Box(new Node("Text", "Update Hardware Schedule", ""))));

        Assert.Equal(
            "Text \"Activity · 3\", Button \"Reset place\", Button \"Close activity list\", Text \"Update Hardware Schedule\"",
            Line(Collect(panel)));
    }

    [Fact]
    public void Skips_text_that_repeats_a_name_already_said()
    {
        // A button's label repeated on the text inside it, and the element's own name.
        var save = new Node("Button", "Save", "", new Node("Text", "Save", ""));
        Assert.Empty(Collect(save, ownName: "Save"));

        var row = Node.Box(
            new Node("ListItem", "Door 104, hinge set", "", new Node("Text", "Door 104", ""), new Node("Text", "hinge set", "")),
            new Node("Text", "Door 105", ""));
        Assert.Equal("ListItem \"Door 104, hinge set\", Text \"Door 105\"", Line(Collect(row)));
    }

    [Fact]
    public void Compares_names_with_line_breaks_and_spacing_flattened()
    {
        var box = Node.Box(new Node("Button", "Reset\n  place", ""), new Node("Text", "Reset place", ""));
        Assert.Single(Collect(box));
    }

    [Fact]
    public void Reports_an_unnamed_node_by_its_automation_id_and_skips_bare_punctuation()
    {
        var bar = Node.Box(
            new Node("Edit", "", "SearchBox"),
            new Node("Text", "·", ""),
            new Node("Text", " — ", "divider"),
            new Node("Edit", "", "SearchBox"));

        var found = Collect(bar);
        Assert.Equal("Edit #SearchBox, Text #divider", Line(found));
        Assert.Equal("", found[1].Name);   // the stray "—" is not reported as its name
    }

    [Fact]
    public void Leaves_room_for_text_when_every_wrapper_has_an_id()
    {
        // Google's results: a dozen id'd wrappers before the first line of text.
        var wrappers = Enumerable.Range(1, 12).Select(i => new Node("Group", "", $"w{i}")).ToList();
        wrappers.Add(new Node("Hyperlink", "Accessibility help", ""));

        var found = Collect(Node.Box([.. wrappers]));

        Assert.Equal("Group #w1, Group #w2, Group #w3, Hyperlink \"Accessibility help\"", Line(found));
    }

    [Fact]
    public void Reports_nothing_for_an_element_with_no_text()
    {
        Assert.Empty(Collect(Node.Box(Node.Box(), new Node("Image", "", ""))));
    }

    [Fact]
    public void Stops_at_the_item_cap_without_reading_further()
    {
        var kids = Enumerable.Range(1, 50).Select(i => new Node("Text", $"row {i}", "")).ToArray();
        var read = new List<Node>();

        var found = Collect(Node.Box(kids), read: read);

        Assert.Equal(TextAnchors.MaxItems, found.Count);
        Assert.Equal("Text \"row 1\"", found[0].ToString());
        Assert.Equal(TextAnchors.MaxItems, read.Count);
    }

    [Fact]
    public void Stops_after_reading_the_most_elements_one_scan_may()
    {
        // A huge page with its only text at the very end: the scan gives up first.
        var blanks = Enumerable.Range(0, TextAnchors.MaxVisited + 50).Select(_ => Node.Box()).ToList();
        blanks.Add(new Node("Text", "far away", ""));
        var read = new List<Node>();

        Assert.Empty(Collect(Node.Box([.. blanks]), read: read));
        Assert.Equal(TextAnchors.MaxVisited, read.Count);
    }

    [Fact]
    public void Stops_at_the_depth_limit()
    {
        var deep = new Node("Text", "bottom", "");
        for (var i = 0; i < TextAnchors.MaxDepth + 5; i++) deep = Node.Box(deep);
        Assert.Empty(Collect(deep));

        var shallow = new Node("Text", "bottom", "");
        for (var i = 0; i < TextAnchors.MaxDepth - 1; i++) shallow = Node.Box(shallow);
        Assert.Single(Collect(shallow));
    }

    [Fact]
    public void Keeps_what_it_found_when_time_runs_out()
    {
        var box = Node.Box(new Node("Text", "first", ""), new Node("Text", "second", ""), new Node("Text", "third", ""));
        var checks = 0;

        var found = Collect(box, outOfTime: () => ++checks > 2);

        Assert.Equal("Text \"first\", Text \"second\"", Line(found));
    }

    [Fact]
    public void Disposes_the_child_walks_it_leaves_unfinished()
    {
        var open = 0;
        IEnumerable<int> Kids(int depth)
        {
            open++;
            try { for (var i = 0; i < 100; i++) yield return depth + 1; }
            finally { open--; }
        }

        var found = TextAnchors.Collect(0, Kids, d => new ElementStep("Text", $"item {d}", ""), [], () => false);

        Assert.Equal(TextAnchors.MaxItems, found.Count);
        Assert.Equal(0, open);
    }
}
