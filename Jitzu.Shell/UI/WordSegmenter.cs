using System.Buffers;

namespace Jitzu.Shell.UI;

internal enum SegmentKind
{
    Word,
    Separator,
    Space
}

internal readonly record struct Segment(int Start, int End, SegmentKind Kind);

internal sealed class WordSegmenter
{
    private static readonly SearchValues<char> Separators = SearchValues.Create("/\\:@=,;|&?#");
    private static readonly SearchValues<char> HostSeparators = SearchValues.Create(":.,");
    private static readonly SearchValues<char> UserInfoSeparators = SearchValues.Create(":");
    private static readonly SearchValues<char> AuthorityTerminators = SearchValues.Create("/?#");
    private static readonly SearchValues<char> HostTerminators = SearchValues.Create(":/\\");
    private static readonly SearchValues<char> NotUserName = SearchValues.Create(":/\\");
    private static readonly SearchValues<char> Quotes = SearchValues.Create("\"'");
    private static readonly SearchValues<char> SchemeCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+.-");

    private static readonly SegmentKind[] ForwardOrder = [SegmentKind.Word, SegmentKind.Separator, SegmentKind.Space];

    public int PreviousBoundary(ReadOnlySpan<char> text, int cursor)
    {
        if (cursor <= 0)
            return 0;

        var segments = Segment(text[..cursor]);
        var index = segments.Count - 1;

        while (index >= 0 && segments[index].Kind is SegmentKind.Space)
            index--;

        while (index >= 0 && segments[index].Kind is SegmentKind.Separator)
            index--;

        return index >= 0 && segments[index].Kind is SegmentKind.Word
            ? segments[index].Start
            : segments[index + 1].Start;
    }

    public int NextBoundary(ReadOnlySpan<char> text, int cursor)
    {
        var segments = Segment(text);
        var index = segments.FindIndex(segment => segment.End > cursor);
        if (index < 0)
            return text.Length;

        var position = cursor;
        foreach (var kind in ForwardOrder)
        {
            if (index >= segments.Count || segments[index].Kind != kind)
                continue;

            position = segments[index].End;
            index++;
        }

        return position;
    }

    public List<Segment> Segment(ReadOnlySpan<char> text)
    {
        var segments = new List<Segment>();
        var position = 0;

        while (position < text.Length)
        {
            var isSpace = char.IsWhiteSpace(text[position]);
            var end = position + 1;
            while (end < text.Length && char.IsWhiteSpace(text[end]) == isSpace)
                end++;

            if (isSpace)
                Add(segments, position, end, SegmentKind.Space);
            else
                SegmentChunk(text[position..end], position, segments);

            position = end;
        }

        return segments;
    }

    private void SegmentChunk(ReadOnlySpan<char> chunk, int offset, List<Segment> segments)
    {
        var bodyStart = chunk.IndexOfAnyExcept(Quotes);
        if (bodyStart < 0)
        {
            Add(segments, offset, offset + chunk.Length, SegmentKind.Separator);
            return;
        }

        var bodyEnd = chunk.LastIndexOfAnyExcept(Quotes) + 1;
        Add(segments, offset, offset + bodyStart, SegmentKind.Separator);
        SegmentBody(chunk[bodyStart..bodyEnd], offset + bodyStart, segments);
        Add(segments, offset + bodyEnd, offset + chunk.Length, SegmentKind.Separator);
    }

    private void SegmentBody(ReadOnlySpan<char> body, int offset, List<Segment> segments)
    {
        if (TryOption(body, offset, segments)
            || TryWindowsRoot(body, offset, segments)
            || TryUri(body, offset, segments)
            || TryUserAtHost(body, offset, segments))
            return;

        Split(body, offset, Separators, segments);
    }

    private bool TryOption(ReadOnlySpan<char> body, int offset, List<Segment> segments)
    {
        if (body[0] != '-')
            return false;

        var equals = body.IndexOf('=');
        if (equals < 0)
            return false;

        Add(segments, offset, offset + equals, SegmentKind.Word);
        Add(segments, offset + equals, offset + equals + 1, SegmentKind.Separator);
        SegmentChunk(body[(equals + 1)..], offset + equals + 1, segments);
        return true;
    }

    private static bool TryWindowsRoot(ReadOnlySpan<char> body, int offset, List<Segment> segments)
    {
        if (body.Length < 3 || !char.IsAsciiLetter(body[0]) || body[1] != ':' || body[2] is not ('\\' or '/'))
            return false;

        Add(segments, offset, offset + 3, SegmentKind.Word);
        Split(body[3..], offset + 3, Separators, segments);
        return true;
    }

    private static bool TryUri(ReadOnlySpan<char> body, int offset, List<Segment> segments)
    {
        var marker = body.IndexOf("://".AsSpan());
        if (marker <= 0 || !IsScheme(body[..marker]))
            return false;

        Add(segments, offset, offset + marker, SegmentKind.Word);
        Add(segments, offset + marker, offset + marker + 3, SegmentKind.Separator);

        var rest = body[(marker + 3)..];
        var restOffset = offset + marker + 3;
        var authorityLength = rest.IndexOfAny(AuthorityTerminators);
        if (authorityLength < 0)
            authorityLength = rest.Length;

        var authority = rest[..authorityLength];
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            Split(authority[..at], restOffset, UserInfoSeparators, segments);
            Add(segments, restOffset + at, restOffset + at + 1, SegmentKind.Separator);
            Split(authority[(at + 1)..], restOffset + at + 1, HostSeparators, segments);
        }
        else
        {
            Split(authority, restOffset, HostSeparators, segments);
        }

        Split(rest[authorityLength..], restOffset + authorityLength, Separators, segments);
        return true;
    }

    private static bool TryUserAtHost(ReadOnlySpan<char> body, int offset, List<Segment> segments)
    {
        var at = body.IndexOf('@');
        if (at <= 0 || body[..at].IndexOfAny(NotUserName) >= 0)
            return false;

        Add(segments, offset, offset + at, SegmentKind.Word);
        Add(segments, offset + at, offset + at + 1, SegmentKind.Separator);

        var rest = body[(at + 1)..];
        var restOffset = offset + at + 1;
        var hostLength = rest.IndexOfAny(HostTerminators);
        if (hostLength < 0)
            hostLength = rest.Length;

        Split(rest[..hostLength], restOffset, HostSeparators, segments);
        Split(rest[hostLength..], restOffset + hostLength, Separators, segments);
        return true;
    }

    private static bool IsScheme(ReadOnlySpan<char> candidate) =>
        char.IsAsciiLetter(candidate[0]) && candidate.IndexOfAnyExcept(SchemeCharacters) < 0;

    private static void Split(ReadOnlySpan<char> text, int offset, SearchValues<char> separators, List<Segment> segments)
    {
        var position = 0;
        while (position < text.Length)
        {
            var isSeparator = separators.Contains(text[position]);
            var end = position + 1;
            while (end < text.Length && separators.Contains(text[end]) == isSeparator)
                end++;

            Add(segments, offset + position, offset + end, isSeparator ? SegmentKind.Separator : SegmentKind.Word);
            position = end;
        }
    }

    private static void Add(List<Segment> segments, int start, int end, SegmentKind kind)
    {
        if (start >= end)
            return;

        if (kind is SegmentKind.Separator
            && segments.Count > 0
            && segments[^1] is { Kind: SegmentKind.Separator } last
            && last.End == start)
        {
            segments[^1] = last with { End = end };
            return;
        }

        segments.Add(new Segment(start, end, kind));
    }
}
