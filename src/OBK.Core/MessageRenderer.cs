using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Text.RegularExpressions;

namespace OBK.Core;

public sealed record TextPart(string Text, bool Bold, bool Italic, bool Underline, bool Heading);

public static class MessageRenderer
{
    public static List<TextPart> Render(string source)
    {
        // A parser only: no browsing context, resource loader, script engine or clickable links.
        using var document = new HtmlParser().ParseDocument(source);
        var parts = new List<TextPart>();
        void Add(string text, bool bold, bool italic, bool underline, bool heading)
        {
            if (text.Length != 0) parts.Add(new(text, bold, italic, underline, heading));
        }
        void Newline()
        {
            if (parts.Count != 0 && !parts[^1].Text.EndsWith('\n')) Add("\n", false, false, false, false);
        }
        void Visit(INode node, bool bold = false, bool italic = false, bool underline = false, bool heading = false, bool pre = false)
        {
            if (node is IText text)
            {
                Add(pre ? text.Data : Regex.Replace(text.Data, @"\s+", " "), bold, italic, underline, heading);
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
            if (tag == "br") Add("\n", bold, italic, underline, heading);
            if (tag == "li") Add("- ", bold, italic, underline, heading);
            if (tag is "td" or "th") Add("\t", bold, italic, underline, heading);
            if (tag == "img") Add(element!.GetAttribute("alt") is { Length: > 0 } alt ? $"[Image: {alt}]" :
                "[Image not loaded]", bold, italic, underline, heading);
            foreach (var child in node.ChildNodes) Visit(child, bold, italic, underline, heading, pre);
            if (tag == "a" && element!.GetAttribute("href") is { } href &&
                (href.StartsWith("https://") || href.StartsWith("http://") || href.StartsWith("mailto:")))
                Add($" ({href})", bold, italic, underline, heading);
            if (block) Newline();
        }
        Visit(document.Body!);
        return parts;
    }
}
