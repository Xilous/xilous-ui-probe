namespace XilousProbe.Uia;

/// <summary>
/// The searchable text inside an element: the names of what it contains (visible text,
/// button labels, aria-labels) and the automation ids of what has no name. A container
/// such as a panel or a card has no name of its own, and its class is shared by dozens of
/// others, so this is what lets a coding agent search the source for the one the pointer
/// means. Generic over the node type so the rules are tested without a live app.
/// </summary>
public static class TextAnchors
{
    /// <summary>The most anchors one pointer line reports.</summary>
    public const int MaxItems = 8;

    /// <summary>
    /// The most of those that may be automation ids. A page that gives every wrapper an id
    /// (Google's results, for one) would otherwise fill the line before any text is reached.
    /// </summary>
    public const int MaxIds = 3;

    /// <summary>The most elements one scan reads, so a copy of a whole window stays quick.</summary>
    public const int MaxVisited = 400;

    /// <summary>How far below the starting element a scan goes.</summary>
    public const int MaxDepth = 24;

    /// <summary>
    /// The anchors below <paramref name="root"/>, in reading order (depth first, as the tree
    /// lists them). A name is kept only when it says something new: Chromium and WPF repeat a
    /// button's label on the text inside it, so a name already contained in one reported
    /// before (or in <paramref name="known"/>, such as the element's own name) is skipped.
    /// Names with no letter or digit (a "·" separator) are skipped too. A node with no name
    /// but an automation id is reported by its id, up to <see cref="MaxIds"/> of them.
    /// </summary>
    /// <param name="root">Where the scan starts. Its own name is not reported.</param>
    /// <param name="children">The children of a node, in order. Read lazily: a scan that stops early asks for no more.</param>
    /// <param name="read">A node's control type, name and automation id.</param>
    /// <param name="known">Names already reported elsewhere in the pointer.</param>
    /// <param name="outOfTime">Checked before each node; true ends the scan with what it has.</param>
    public static List<ElementStep> Collect<T>(
        T root,
        Func<T, IEnumerable<T>> children,
        Func<T, ElementStep> read,
        IEnumerable<string> known,
        Func<bool> outOfTime)
    {
        var found = new List<ElementStep>();
        var said = known.Select(Normalize).Where(s => s.Length > 0).ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var visited = 0;
        var stack = new Stack<(IEnumerator<T> Kids, int Depth)>();
        stack.Push((children(root).GetEnumerator(), 1));
        try
        {
            while (stack.Count > 0 && found.Count < MaxItems)
            {
                var (kids, depth) = stack.Peek();
                if (!kids.MoveNext())
                {
                    stack.Pop().Kids.Dispose();
                    continue;
                }
                if (++visited > MaxVisited || outOfTime()) break;

                var step = read(kids.Current);
                var name = Normalize(step.Name);
                if (HasWords(name))
                {
                    if (!said.Any(s => s.Contains(name, StringComparison.Ordinal)))
                    {
                        found.Add(step);
                        said.Add(name);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(step.AutomationId) && ids.Count < MaxIds && ids.Add(step.AutomationId))
                {
                    found.Add(step with { Name = "" });   // reported by its id, not by the stray punctuation
                }

                if (depth < MaxDepth) stack.Push((children(kids.Current).GetEnumerator(), depth + 1));
            }
        }
        finally
        {
            foreach (var (kids, _) in stack) kids.Dispose();
        }
        return found;
    }

    /// <summary>Line breaks and runs of spaces as single spaces, trimmed.</summary>
    public static string Normalize(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool HasWords(string s) => s.Any(char.IsLetterOrDigit);
}
