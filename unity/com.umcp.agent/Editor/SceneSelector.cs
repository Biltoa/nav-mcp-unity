#nullable disable
using System;
using System.Collections.Generic;

namespace Umcp.Agent
{
    /// <summary>
    /// The selector grammar, parsed once and shared by both sides.
    ///
    /// This file is compiled into the Unity package *and* linked into the daemon, because both
    /// evaluate the same selectors: the Editor against live GameObjects, the daemon against its
    /// mirror. Two parsers would drift, and a selector that means different things on the two
    /// paths would make `source: "mirror"` a lie.
    ///
    /// It therefore references no Unity type and no daemon type — only the grammar.
    ///
    ///   /Root            a scene root named Root
    ///   /Root/Child      a direct child
    ///   //Button         any descendant named Button, at any depth
    ///   //Canvas//Button Buttons under any Canvas
    ///   //*              everything
    ///
    /// Predicates chain and all must hold:
    ///   [active] [inactive] [root] [leaf]
    ///   [has:Rigidbody] [missing:Collider] [tag:Player] [layer:Water]
    ///   [name*:Enemy] [name^:UI_] [name$:_LOD0]
    /// </summary>
    public static class SceneSelector
    {
        public const string ErrSyntax = "E_SELECTOR_SYNTAX";
        public const string ErrPredicate = "E_SELECTOR_PREDICATE";

        public static readonly string[] KnownPredicates =
        {
            "active", "inactive", "root", "leaf",
            "has", "missing", "tag", "layer", "name*", "name^", "name$"
        };

        /// <summary>Predicates that need an argument.</summary>
        public static bool NeedsArgument(string key)
        {
            switch (key)
            {
                case "has":
                case "missing":
                case "tag":
                case "layer":
                case "name*":
                case "name^":
                case "name$":
                    return true;
                default:
                    return false;
            }
        }

        public sealed class Predicate
        {
            public string Key;
            public string Arg;
            public override string ToString() { return Arg == null ? "[" + Key + "]" : "[" + Key + ":" + Arg + "]"; }
        }

        public sealed class Step
        {
            public string Name = "*";
            /// <summary>Reached by "//" rather than "/", so it matches at any depth.</summary>
            public bool Descendant;
            public readonly List<Predicate> Predicates = new List<Predicate>();
        }

        /// <summary>Raised on a malformed selector. Each side converts it to its own error shape.</summary>
        public sealed class ParseException : Exception
        {
            public readonly string Code;
            public readonly string Value;
            public readonly string[] DidYouMean;
            public readonly string Hint;

            public ParseException(string code, string message, string value, string[] didYouMean, string hint)
                : base(message)
            {
                Code = code; Value = value; DidYouMean = didYouMean; Hint = hint;
            }
        }

        public static List<Step> Parse(string selector)
        {
            if (string.IsNullOrEmpty(selector)) selector = "//*";
            if (selector[0] != '/') selector = "//" + selector;

            var steps = new List<Step>();
            int i = 0;
            while (i < selector.Length)
            {
                bool descendant = false;
                if (selector[i] == '/')
                {
                    i++;
                    if (i < selector.Length && selector[i] == '/') { descendant = true; i++; }
                }
                if (i >= selector.Length) break;

                int start = i;
                while (i < selector.Length && selector[i] != '/' && selector[i] != '[') i++;

                var step = new Step { Descendant = descendant };
                var name = selector.Substring(start, i - start);
                step.Name = name.Length == 0 ? "*" : name;

                while (i < selector.Length && selector[i] == '[')
                {
                    int close = selector.IndexOf(']', i);
                    if (close < 0)
                        throw new ParseException(ErrSyntax, "Unclosed predicate in selector.", selector, null,
                            "Predicates look like [active] or [has:Rigidbody].");
                    step.Predicates.Add(ParsePredicate(selector.Substring(i + 1, close - i - 1)));
                    i = close + 1;
                }
                steps.Add(step);
            }

            if (steps.Count == 0)
                throw new ParseException(ErrSyntax, "Empty selector.", selector, null,
                    "For example: //Canvas//Button[active]");
            return steps;
        }

        static Predicate ParsePredicate(string body)
        {
            int colon = body.IndexOf(':');
            var key = (colon < 0 ? body : body.Substring(0, colon)).Trim();
            var arg = colon < 0 ? null : body.Substring(colon + 1).Trim();

            if (Array.IndexOf(KnownPredicates, key) < 0)
                throw new ParseException(ErrPredicate, "Unknown predicate '" + key + "'.", body,
                    Closest(key, KnownPredicates),
                    "Predicates: [active] [inactive] [root] [leaf] [has:T] [missing:T] [tag:T] [layer:L] [name*:s] [name^:s] [name$:s]");

            if (NeedsArgument(key) && string.IsNullOrEmpty(arg))
                throw new ParseException(ErrPredicate,
                    "Predicate '" + key + "' needs a value, e.g. [" + key + ":Rigidbody].", body, null, null);

            return new Predicate { Key = key, Arg = arg };
        }

        /// <summary>
        /// Which predicates and fields the daemon's mirror can answer without asking the Editor.
        /// A query that needs anything outside these sets has to go live, and says so.
        /// </summary>
        public static bool IsMirrorServiceablePredicate(string key)
        {
            // Every predicate is answerable from the mirrored node record: names, active state,
            // parentage, tag, layer and the component *type* list are all mirrored. Component
            // property *values* are not, which is a field concern rather than a predicate one.
            return Array.IndexOf(KnownPredicates, key) >= 0;
        }

        public static readonly string[] MirrorServiceableFields =
        {
            "id", "name", "path", "active", "activeInHierarchy", "tag", "layer",
            "parent", "childCount", "components"
        };

        public static bool IsMirrorServiceableField(string field)
        {
            return Array.IndexOf(MirrorServiceableFields, field) >= 0;
        }

        static string[] Closest(string needle, string[] haystack)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(needle)) return null;
            var n = needle.ToLowerInvariant();
            foreach (var candidate in haystack)
                if (Distance(n, candidate.ToLowerInvariant()) <= 2) result.Add(candidate);
            return result.Count == 0 ? null : result.ToArray();
        }

        static int Distance(string a, string b)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int min = cur[j - 1] + 1;
                    if (prev[j] + 1 < min) min = prev[j] + 1;
                    if (prev[j - 1] + cost < min) min = prev[j - 1] + cost;
                    cur[j] = min;
                }
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }
}
