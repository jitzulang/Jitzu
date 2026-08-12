namespace Jitzu.Shell.Core.Commands;

internal sealed class RmTreeNode
{
    private RmTreeNode(string fullPath, bool isDirectory, RmTreeNode? parent)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Parent = parent;
    }

    internal string FullPath { get; }
    internal string Name => Parent is null ? new DirectoryInfo(FullPath).Name : Path.GetFileName(FullPath);
    internal bool IsDirectory { get; }
    internal RmTreeNode? Parent { get; }
    internal List<RmTreeNode> Children { get; } = [];
    internal bool Expanded { get; set; }
    internal bool Selected { get; set; }
    internal int Depth => Parent is null ? 0 : Parent.Depth + 1;

    internal static RmTreeNode Create(string path)
    {
        var root = Build(new DirectoryInfo(path), null);
        root.Expanded = true; // Show the root's contents; nested directories start collapsed.
        return root;
    }

    private static RmTreeNode Build(FileSystemInfo info, RmTreeNode? parent)
    {
        var isDirectory = info is DirectoryInfo;
        var node = new RmTreeNode(info.FullName, isDirectory, parent);
        if (!isDirectory || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return node;

        var entries = ((DirectoryInfo)info).EnumerateFileSystemInfos()
            .OrderByDescending(entry => entry is DirectoryInfo)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            node.Children.Add(Build(entry, node));
        return node;
    }
}

internal sealed class RmTreeSelection
{
    private readonly RmTreeNode _root;
    private int _index;

    internal RmTreeSelection(RmTreeNode root) => _root = root;

    internal IReadOnlyList<RmTreeNode> VisibleNodes => FlattenVisible(_root).ToArray();
    internal RmTreeNode Current => VisibleNodes[Math.Clamp(_index, 0, VisibleNodes.Count - 1)];
    internal int CurrentIndex => _index;
    internal bool HasSelection => Walk(_root).Any(node => node.Selected);

    internal bool IsPartiallySelected(RmTreeNode node) =>
        !node.Selected && node.Children.Any(child => child.Selected || IsPartiallySelected(child));

    internal void Move(int delta)
    {
        _index = Math.Clamp(_index + delta, 0, VisibleNodes.Count - 1);
    }

    internal void Expand()
    {
        if (Current.IsDirectory)
            Current.Expanded = true;
    }

    internal void Collapse()
    {
        if (Current.IsDirectory && Current.Expanded)
        {
            Current.Expanded = false;
            return;
        }

        if (Current.Parent is { } parent)
        {
            var visible = VisibleNodes;
            for (var i = 0; i < visible.Count; i++)
            {
                if (ReferenceEquals(visible[i], parent))
                {
                    _index = i;
                    break;
                }
            }
        }
    }

    internal void Toggle()
    {
        var select = !Current.Selected;
        SetSubtree(Current, select);
        if (!select)
        {
            for (var parent = Current.Parent; parent is not null; parent = parent.Parent)
                parent.Selected = false;
        }
    }

    internal IEnumerable<RmTreeNode> GetDeletionRoots() => DeletionRoots(_root, false);

    private static void SetSubtree(RmTreeNode node, bool selected)
    {
        node.Selected = selected;
        foreach (var child in node.Children)
            SetSubtree(child, selected);
    }

    private static IEnumerable<RmTreeNode> FlattenVisible(RmTreeNode node)
    {
        yield return node;
        if (!node.Expanded)
            yield break;
        foreach (var child in node.Children)
            foreach (var descendant in FlattenVisible(child))
                yield return descendant;
    }

    private static IEnumerable<RmTreeNode> Walk(RmTreeNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Walk(child))
                yield return descendant;
    }

    private static IEnumerable<RmTreeNode> DeletionRoots(RmTreeNode node, bool ancestorSelected)
    {
        if (node.Selected && !ancestorSelected)
        {
            yield return node;
            yield break;
        }

        foreach (var child in node.Children)
            foreach (var selected in DeletionRoots(child, ancestorSelected || node.Selected))
                yield return selected;
    }
}

internal interface IRmInteractiveConsole
{
    bool IsInteractive { get; }
    bool Select(RmTreeSelection selection, string displayPath);
}

internal sealed class SystemRmInteractiveConsole : IRmInteractiveConsole
{
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public bool Select(RmTreeSelection selection, string displayPath)
    {
        Console.Write("\e[?1049h");
        try
        {
            while (true)
            {
                Draw(selection, displayPath);
                var key = Console.ReadKey(true);
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow: selection.Move(-1); break;
                    case ConsoleKey.DownArrow: selection.Move(1); break;
                    case ConsoleKey.LeftArrow: selection.Collapse(); break;
                    case ConsoleKey.RightArrow: selection.Expand(); break;
                    case ConsoleKey.Spacebar: selection.Toggle(); break;
                    case ConsoleKey.Enter when selection.HasSelection: return true;
                    case ConsoleKey.Escape:
                    case ConsoleKey.Q: return false;
                }
            }
        }
        finally
        {
            Console.Write("\e[?1049l");
        }
    }

    private static void Draw(RmTreeSelection selection, string displayPath)
    {
        Console.Clear();
        var width = Math.Max(1, Console.WindowWidth - 1);
        var height = Math.Max(1, Console.WindowHeight - 3);
        Console.WriteLine(Truncate($"Interactive remove: {displayPath}", width));

        var nodes = selection.VisibleNodes;
        var offset = Math.Clamp(selection.CurrentIndex - height + 1, 0, Math.Max(0, nodes.Count - height));
        for (var i = offset; i < nodes.Count && i < offset + height; i++)
        {
            var node = nodes[i];
            var cursor = i == selection.CurrentIndex ? ">" : " ";
            var check = node.Selected ? "[x]" : selection.IsPartiallySelected(node) ? "[-]" : "[ ]";
            var branch = node.IsDirectory ? (node.Expanded ? "▼" : "▶") : " ";
            var suffix = node.IsDirectory ? "/" : "";
            var line = $"{cursor} {check} {new string(' ', node.Depth * 2)}{branch} {node.Name}{suffix}";
            Console.WriteLine(Truncate(line, width));
        }

        Console.Write("↑↓ navigate  ←→ collapse/expand  Space select  Enter delete  Esc/q cancel");
    }

    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..width];
}
