using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace Maki.Sources.Custom.Inference;

/// <summary>
/// CSS selectors for a repeated element, built from the element's own tag and classes and the
/// nearest ancestors that tell its list apart from the rest of the page.
/// </summary>
public static partial class SelectorBuilder
{
    /// <summary>
    /// Classes that are safe to write unescaped and look like names a theme chose. Utility classes
    /// with <c>:</c> or <c>/</c> (Tailwind's <c>md:flex</c>, <c>w-1/2</c>) are skipped.
    /// </summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]{0,40}$")]
    private static partial Regex SafeClass();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,60}$")]
    private static partial Regex SafeId();

    /// <summary>
    /// The nearest element at or above <paramref name="start"/> that repeats: its parent holds at
    /// least one more child of the same tag and classes. That is the list item.
    /// </summary>
    public static IElement? RepeatedAncestor(IElement start, IElement? stopAt = null)
    {
        for (var e = start; e?.ParentElement is { } parent && e != stopAt; e = parent)
        {
            if (parent.TagName is "BODY" or "HTML")
            {
                return null;
            }

            var signature = Signature(e);
            if (parent.Children.Count(c => Signature(c) == signature) >= 2)
            {
                return e;
            }
        }

        return null;
    }

    public static string Signature(IElement e) =>
        e.LocalName + "." + string.Join(".", e.ClassList.Order(StringComparer.Ordinal));

    /// <summary>
    /// A selector that matches <paramref name="item"/> and its same-signature siblings and, as far
    /// as a few ancestors can make it, nothing else on the page. Null when no such selector exists.
    /// </summary>
    public static string? ForList(IElement item)
    {
        var self = Simple(item, idAllowed: false);
        if (item.LocalName == "a")
        {
            self += "[href]";
        }

        var siblings = item.ParentElement?.Children.Where(c => Signature(c) == Signature(item)).ToHashSet() ?? [item];
        var selector = self;
        var ancestor = item.ParentElement;
        for (var depth = 0; depth < 4 && ancestor is not null; depth++, ancestor = ancestor.ParentElement)
        {
            var matched = Matches(item.Owner!, selector);
            if (matched.Count == siblings.Count && siblings.All(matched.Contains))
            {
                return selector;
            }

            if (ancestor.TagName is "BODY" or "HTML")
            {
                break;
            }

            var part = Simple(ancestor, idAllowed: true);
            selector = depth == 0 ? $"{part} > {self}" : $"{part} {selector}";
            if (part.StartsWith('#'))
            {
                // An id is unique on the page; nothing further up makes it more precise.
                var final = Matches(item.Owner!, selector);
                return final.Contains(item) ? selector : null;
            }
        }

        var last = Matches(item.Owner!, selector);
        return last.Contains(item) && last.Count <= siblings.Count * 2 ? selector : null;
    }

    /// <summary>The selector for one element under its list item: tag plus a class when one is distinctive.</summary>
    public static string Relative(IElement element) => Simple(element, idAllowed: false);

    private static string Simple(IElement e, bool idAllowed)
    {
        if (idAllowed && e.Id is { } id && SafeId().IsMatch(id))
        {
            return "#" + id;
        }

        var classes = e.ClassList.Where(c => SafeClass().IsMatch(c)).Take(2).ToList();
        return e.LocalName + string.Concat(classes.Select(c => "." + c));
    }

    private static HashSet<IElement> Matches(IDocument doc, string selector)
    {
        try
        {
            return doc.QuerySelectorAll(selector).ToHashSet();
        }
        catch (DomException)
        {
            return [];
        }
    }
}
