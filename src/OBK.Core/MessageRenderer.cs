using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Text.RegularExpressions;

namespace OBK.Core;

public sealed record TextPart(string Text, bool Bold, bool Italic, bool Underline, bool Heading, string? Link = null);

public static partial class MessageRenderer
{
    public static List<TextPart> Render(string source)
    {
        // A parser only: no browsing context, resource loader or script engine. Links are data for the viewer.
        using var document = new HtmlParser().ParseDocument(source);
        var parts = new List<TextPart>();
        void Add(string text, bool bold, bool italic, bool underline, bool heading, string? link = null)
        {
            if (text.Length != 0) parts.Add(new(text, bold, italic, underline, heading, link));
        }
        void AddText(string text, bool bold, bool italic, bool underline, bool heading, string? link)
        {
            if (link is not null) { Add(text, bold, italic, underline, heading, link); return; }
            var position = 0;
            foreach (Match match in BareUrl().Matches(text))
            {
                var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'', '"');
                if (NormalizeLink(url) is not { } target) continue;
                Add(text[position..match.Index], bold, italic, underline, heading);
                Add(url, bold, italic, underline, heading, target);
                position = match.Index + url.Length;
            }
            Add(text[position..], bold, italic, underline, heading);
        }
        void Newline()
        {
            if (parts.Count != 0 && !parts[^1].Text.EndsWith('\n')) Add("\n", false, false, false, false);
        }
        void Visit(INode node, bool bold = false, bool italic = false, bool underline = false, bool heading = false,
            bool pre = false, string? link = null)
        {
            if (node is IText text)
            {
                AddText(pre ? text.Data : Regex.Replace(text.Data, @"\s+", " "), bold, italic, underline, heading, link);
                return;
            }
            var element = node as IElement;
            var tag = element?.LocalName;
            if (tag is "script" or "style" or "iframe" or "object" or "head") return;
            var block = tag is "p" or "div" or "ul" or "ol" or "li" or "tr" or "h1" or "h2" or "h3" or "blockquote" or "pre";
            if (block) Newline();
            bold |= tag is "b" or "strong";
            italic |= tag is "i" or "em";
            underline |= tag == "u";
            heading |= tag is "h1" or "h2" or "h3";
            pre |= tag == "pre";
            var count = parts.Count;
            if (tag == "a" && NormalizeLink(element!.GetAttribute("href")) is { } target) link = target;
            if (tag == "br") Add("\n", bold, italic, underline, heading);
            if (tag == "li") Add("- ", bold, italic, underline, heading);
            if (tag is "td" or "th") Add("\t", bold, italic, underline, heading);
            if (tag == "img") Add(element!.GetAttribute("alt") is { Length: > 0 } alt ? $"[Image: {alt}]" :
                "[Image not loaded]", bold, italic, underline, heading, link);
            foreach (var child in node.ChildNodes) Visit(child, bold, italic, underline, heading, pre, link);
            if (tag == "a" && link is not null && !parts.Skip(count).Any(p => p.Link is not null && p.Text.Trim().Length > 0))
                Add(element!.GetAttribute("href")!.Trim(), bold, italic, underline, heading, link);
            if (block) Newline();
        }
        Visit(document.Body!);
        return parts;
    }

    /// <summary>
    /// Returns an absolute http(s) or mailto destination, or null. Scheme-less "www." links are treated as
    /// HTTPS instead of being resolved relative to the school's Bakaláři site.
    /// </summary>
    public static string? NormalizeLink(string? href)
    {
        var value = href?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme == Uri.UriSchemeMailto) return value;
        return (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Length > 0
            ? uri.AbsoluteUri : null;
    }

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();
}
