using System.Collections.Concurrent;
using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using DPoint = System.Drawing.Point;

namespace XilousProbe.Uia;

/// <summary>
/// All UI Automation work, on one dedicated thread. UIA calls cross into other
/// processes and can take tens of milliseconds, or hang on an app that is not
/// responding, so none of it runs on the UI thread. Hover requests are coalesced:
/// only the latest cursor position is ever looked up.
/// </summary>
public sealed class UiaEngine : IDisposable
{
    private const int MaxAncestors = 14;
    private const int MaxSiblingScan = 500;

    private readonly Thread _thread;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly ConcurrentDictionary<uint, string> _processNames = new();
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private UIA3Automation? _automation;
    private AutomationElement? _selected;

    private int _hoverX, _hoverY;
    private int _hoverQueued;

    /// <summary>The element under the latest hover position, or null over nothing (or over our own windows).</summary>
    public event Action<ElementInfo?>? Hovered;

    public UiaEngine()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "UI Automation" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Run()
    {
        _automation = new UIA3Automation();
        // Don't let a hung app stall the pointer for the default 20 seconds.
        _automation.ConnectionTimeout = TimeSpan.FromMilliseconds(1500);
        _automation.TransactionTimeout = TimeSpan.FromMilliseconds(1500);
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { Debug.WriteLine($"UIA work failed: {ex.Message}"); }
        }
        _automation.Dispose();
    }

    private Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>
    /// The shortest time between two hover lookups. Each one is a hit test inside the target app;
    /// in a Chromium or Electron app that is a trip through its accessibility tree, and asking
    /// back to back while the cursor sweeps slows the app itself. So the hover follows the cursor
    /// loosely: at most this often, always for wherever the cursor is by then, never for the
    /// points it passed on the way.
    /// </summary>
    private static readonly TimeSpan HoverInterval = TimeSpan.FromMilliseconds(70);
    private readonly Stopwatch _sinceHover = Stopwatch.StartNew();
    private FlaUI.Core.CacheRequest? _hoverCache;

    /// <summary>Asks for the element under a screen point. Only the latest request is served.</summary>
    public void RequestHover(int x, int y)
    {
        Volatile.Write(ref _hoverX, x);
        Volatile.Write(ref _hoverY, y);
        if (Interlocked.Exchange(ref _hoverQueued, 1) == 1) return;
        _queue.Add(() =>
        {
            var wait = HoverInterval - _sinceHover.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            // Read the position only now, after the wait, so the lookup is for where the cursor is.
            Interlocked.Exchange(ref _hoverQueued, 0);
            int hx = Volatile.Read(ref _hoverX), hy = Volatile.Read(ref _hoverY);
            _sinceHover.Restart();
            Hovered?.Invoke(HoverAt(hx, hy));
        });
    }

    /// <summary>
    /// The hover readout in one round trip: the element and the handful of properties the label
    /// shows come back together from the hit test, instead of one cross-process call per property.
    /// </summary>
    private ElementInfo? HoverAt(int x, int y)
    {
        var a = _automation!;
        if (_hoverCache is null)
        {
            var lib = a.PropertyLibrary.Element;
            _hoverCache = new FlaUI.Core.CacheRequest
            {
                TreeScope = FlaUI.Core.Definitions.TreeScope.Element,
                // Cached values only: the hover never keeps a live reference into the target app.
                AutomationElementMode = FlaUI.Core.Definitions.AutomationElementMode.None,
                // The raw view, which the click's hit test shares (see SafeFromPoint).
                TreeFilter = FlaUI.Core.Conditions.TrueCondition.Default,
            };
            foreach (var p in new[] { lib.ControlType, lib.Name, lib.AutomationId, lib.ClassName, lib.FrameworkId, lib.BoundingRectangle, lib.ProcessId })
                _hoverCache.Add(p);
        }
        try
        {
            using (_hoverCache.Activate())
            {
                var el = a.FromPoint(new DPoint(x, y));
                if (el is null) return null;
                uint pid = (uint)el.Properties.ProcessId.ValueOrDefault;
                if (pid == _ownPid || pid == 0) return null;
                return Describe(el, detailed: false);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TimeoutException or UnauthorizedAccessException
                                       or FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>Selects the element under a point and returns its full description.</summary>
    public Task<ElementInfo?> SelectAtAsync(int x, int y) => Run(() =>
    {
        _selected = SafeFromPoint(x, y);
        return _selected is null ? null : Describe(_selected, detailed: true);
    });

    /// <summary>Moves the selection to its parent. Stays put at the top-level window.</summary>
    public Task<ElementInfo?> SelectParentAsync() => Run(() =>
    {
        if (_selected is null) return null;
        var parent = Walker.GetParent(_selected);
        if (parent is not null && !IsDesktop(parent)) _selected = parent;
        return Describe(_selected, detailed: true);
    });

    /// <summary>
    /// Re-reads the selection for Copy, after the app may have changed under it, along with
    /// the text it contains (which only Copy needs, so selecting never waits for it).
    /// </summary>
    public Task<ElementInfo?> RefreshSelectedAsync() => Run(() =>
        _selected is null ? null : Describe(_selected, detailed: true, anchors: true));

    public void ClearSelection() => _queue.Add(() => _selected = null);

    private FlaUI.Core.ITreeWalker Walker => _automation!.TreeWalkerFactory.GetControlViewWalker();

    private bool IsDesktop(AutomationElement el)
    {
        try { return el.Equals(_automation!.GetDesktop()); }
        catch { return false; }
    }

    /// <summary>
    /// The click's hit test, under a cache request with the hover's tree filter (the raw view),
    /// so a click selects exactly what the hover outlined. A plain ElementFromPoint answers in
    /// UIA's control view, which leaves out every element that is not a control. In a Chromium
    /// or Electron app that includes plain containers such as a popover panel, and a click on
    /// one lands on whatever control lies under it, often the whole page.
    /// </summary>
    private FlaUI.Core.CacheRequest? _selectCache;

    private AutomationElement? SafeFromPoint(int x, int y)
    {
        _selectCache ??= new FlaUI.Core.CacheRequest
        {
            TreeScope = FlaUI.Core.Definitions.TreeScope.Element,
            // A live reference, unlike the hover's: Parent, Copy and the details read through it.
            AutomationElementMode = FlaUI.Core.Definitions.AutomationElementMode.Full,
            TreeFilter = FlaUI.Core.Conditions.TrueCondition.Default,
        };
        try
        {
            AutomationElement? el;
            // Only the hit test runs under the request. While one is active FlaUI reads every
            // property from the cache, so everything after it reads live from the app instead.
            using (_selectCache.Activate())
                el = _automation!.FromPoint(new DPoint(x, y));
            if (el is null) return null;
            uint pid = (uint)el.Properties.ProcessId.ValueOrDefault;
            if (pid == _ownPid || pid == 0) return null;
            return el;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TimeoutException or UnauthorizedAccessException
                                       or FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            return null;
        }
    }

    private string ProcessName(uint pid) => _processNames.GetOrAdd(pid, p =>
    {
        try
        {
            using var proc = Process.GetProcessById((int)p);
            return proc.ProcessName + ".exe";
        }
        catch { return ""; }
    });

    private static string Prop(Func<string?> read)
    {
        try { return read() ?? ""; } catch { return ""; }
    }

    private static PxRect RectOf(AutomationElement el)
    {
        try
        {
            var r = el.Properties.BoundingRectangle.ValueOrDefault;
            return new PxRect(r.X, r.Y, r.Width, r.Height);
        }
        catch { return default; }
    }

    private static string TypeName(AutomationElement el)
    {
        try
        {
            var t = el.Properties.ControlType.ValueOrDefault;
            return t == ControlType.Unknown ? "Custom" : t.ToString();
        }
        catch { return "Custom"; }
    }

    private static ElementStep StepOf(AutomationElement el) =>
        new(TypeName(el), Prop(() => el.Properties.Name.ValueOrDefault), Prop(() => el.Properties.AutomationId.ValueOrDefault));

    private ElementInfo Describe(AutomationElement el, bool detailed, bool anchors = false)
    {
        uint pid = 0;
        try { pid = (uint)el.Properties.ProcessId.ValueOrDefault; } catch { }

        var info = new ElementInfo
        {
            ControlType = TypeName(el),
            Name = Prop(() => el.Properties.Name.ValueOrDefault),
            AutomationId = Prop(() => el.Properties.AutomationId.ValueOrDefault),
            ClassName = Prop(() => el.Properties.ClassName.ValueOrDefault),
            FrameworkId = Prop(() => el.Properties.FrameworkId.ValueOrDefault),
            Bounds = RectOf(el),
            ProcessId = pid,
            ProcessName = ProcessName(pid),
        };
        if (!detailed) return info;

        // Ancestry up to (not including) the desktop. The last one found is the top-level window.
        var ancestors = new List<AutomationElement>();
        var walker = Walker;
        try
        {
            var cur = walker.GetParent(el);
            while (cur is not null && !IsDesktop(cur) && ancestors.Count < MaxAncestors)
            {
                ancestors.Add(cur);
                cur = walker.GetParent(cur);
            }
        }
        catch { /* the app went away mid-walk; report what was read */ }
        ancestors.Reverse();

        var window = ancestors.Count > 0 ? ancestors[0] : el;
        var (index, count, prev, next) = ancestors.Count > 0 ? Siblings(walker, el) : (0, 0, null, null);

        var patterns = new List<string>();
        try
        {
            foreach (var p in el.GetSupportedPatterns())
                patterns.Add(p.Name.Replace("Pattern", ""));
        }
        catch { }

        string value = "";
        try { value = el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault ?? ""; } catch { }

        var steps = ancestors.Select(StepOf).ToList();
        var (contents, nearby, nearbyAt) = anchors ? Anchors(el, info.Name, ancestors) : ([], [], -1);

        return info with
        {
            HelpText = Prop(() => el.Properties.HelpText.ValueOrDefault),
            WindowTitle = Prop(() => window.Properties.Name.ValueOrDefault),
            WindowBounds = RectOf(window),
            Ancestors = steps,
            Contents = contents,
            Nearby = nearby,
            NearbyIn = nearbyAt >= 0 ? steps[nearbyAt] : null,
            NearbyLevels = nearbyAt >= 0 ? ancestors.Count - nearbyAt : 0,
            Index = index,
            SiblingCount = count,
            Previous = prev,
            Next = next,
            IsEnabled = Try(() => el.Properties.IsEnabled.ValueOrDefault, true),
            IsKeyboardFocusable = Try(() => el.Properties.IsKeyboardFocusable.ValueOrDefault, false),
            IsOffscreen = Try(() => el.Properties.IsOffscreen.ValueOrDefault, false),
            Patterns = patterns.Where(p => p is not ("LegacyIAccessible" or "ScrollItem")).ToList(),
            Value = value,
            IsTopLevel = ancestors.Count == 0,
        };
    }

    private (int, int, ElementStep?, ElementStep?) Siblings(FlaUI.Core.ITreeWalker walker, AutomationElement el)
    {
        try
        {
            var parent = walker.GetParent(el);
            if (parent is null) return (0, 0, null, null);
            int i = 0, found = 0;
            AutomationElement? before = null, prevSeen = null, after = null;
            for (var c = walker.GetFirstChild(parent); c is not null && i < MaxSiblingScan; c = walker.GetNextSibling(c))
            {
                i++;
                if (found == 0 && c.Equals(el)) { found = i; before = prevSeen; }
                else if (found > 0 && after is null) after = c;
                prevSeen = c;
            }
            return found == 0 ? (0, 0, null, null) : (found, i, before is null ? null : StepOf(before), after is null ? null : StepOf(after));
        }
        catch { return (0, 0, null, null); }
    }

    /// <summary>How long Copy spends reading the text in and around the selection, at most.</summary>
    private static readonly TimeSpan AnchorBudget = TimeSpan.FromMilliseconds(600);

    /// <summary>How many ancestors the nearby text is looked for in, nearest first.</summary>
    private const int MaxNearbyLevels = 3;

    /// <summary>
    /// The anchors walk brings back each child's type, name and id with the step that finds
    /// it, one cross-process call per element instead of four.
    /// </summary>
    private FlaUI.Core.CacheRequest? _anchorCache;

    /// <summary>
    /// The text inside the selection, or, when it has none, the text in the nearest of its
    /// ancestors that has some (below the window, at most <see cref="MaxNearbyLevels"/> up).
    /// Read through the raw view, the one the click's hit test uses, so plain containers
    /// such as a Chromium div are walked into rather than skipped.
    /// </summary>
    /// <returns>The contents, the nearby text, and the index in <paramref name="ancestors"/> it came from (-1 for none).</returns>
    private (List<ElementStep>, List<ElementStep>, int) Anchors(AutomationElement el, string ownName, List<AutomationElement> ancestors)
    {
        if (_anchorCache is null)
        {
            var lib = _automation!.PropertyLibrary.Element;
            _anchorCache = new FlaUI.Core.CacheRequest
            {
                TreeScope = FlaUI.Core.Definitions.TreeScope.Element,
                // A live reference: the walk steps on from each element it is handed.
                AutomationElementMode = FlaUI.Core.Definitions.AutomationElementMode.Full,
                TreeFilter = FlaUI.Core.Conditions.TrueCondition.Default,
            };
            foreach (var p in new[] { lib.ControlType, lib.Name, lib.AutomationId })
                _anchorCache.Add(p);
        }

        var raw = _automation!.TreeWalkerFactory.GetRawViewWalker();
        var clock = Stopwatch.StartNew();
        bool OutOfTime() => clock.Elapsed > AnchorBudget;
        List<ElementStep> Scan(AutomationElement root)
        {
            try
            {
                using (_anchorCache.Activate())
                    return TextAnchors.Collect(root, n => ChildrenOf(raw, n), StepOf, [ownName], OutOfTime);
            }
            catch { return []; }   // the app went away mid-walk
        }

        var contents = Scan(el);
        if (contents.Count > 0) return (contents, [], -1);

        // Never index 0, the top-level window (or the highest ancestor read): its text is the
        // whole app's, not this element's surroundings.
        for (int i = ancestors.Count - 1; i >= 1 && i >= ancestors.Count - MaxNearbyLevels && !OutOfTime(); i--)
        {
            var nearby = Scan(ancestors[i]);
            if (nearby.Count > 0) return ([], nearby, i);
        }
        return ([], [], -1);
    }

    /// <summary>A node's children, one step at a time, ending early (not failing) if the app stops answering.</summary>
    private static IEnumerable<AutomationElement> ChildrenOf(FlaUI.Core.ITreeWalker walker, AutomationElement el)
    {
        var child = Nav(() => walker.GetFirstChild(el));
        while (child is not null)
        {
            yield return child;
            var current = child;
            child = Nav(() => walker.GetNextSibling(current));
        }
    }

    private static AutomationElement? Nav(Func<AutomationElement?> step)
    {
        try { return step(); } catch { return null; }
    }

    private static T Try<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }

    public void Dispose() => _queue.CompleteAdding();
}
