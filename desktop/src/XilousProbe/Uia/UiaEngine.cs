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

    /// <summary>Re-reads the selection, for Copy after the app has changed under it.</summary>
    public Task<ElementInfo?> RefreshSelectedAsync() => Run(() =>
        _selected is null ? null : Describe(_selected, detailed: true));

    public void ClearSelection() => _queue.Add(() => _selected = null);

    private FlaUI.Core.ITreeWalker Walker => _automation!.TreeWalkerFactory.GetControlViewWalker();

    private bool IsDesktop(AutomationElement el)
    {
        try { return el.Equals(_automation!.GetDesktop()); }
        catch { return false; }
    }

    private AutomationElement? SafeFromPoint(int x, int y)
    {
        try
        {
            var el = _automation!.FromPoint(new DPoint(x, y));
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

    private ElementInfo Describe(AutomationElement el, bool detailed)
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

        return info with
        {
            HelpText = Prop(() => el.Properties.HelpText.ValueOrDefault),
            WindowTitle = Prop(() => window.Properties.Name.ValueOrDefault),
            WindowBounds = RectOf(window),
            Ancestors = ancestors.Select(StepOf).ToList(),
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

    private static T Try<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }

    public void Dispose() => _queue.CompleteAdding();
}
