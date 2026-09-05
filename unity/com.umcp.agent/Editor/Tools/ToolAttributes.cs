using System;

namespace Umcp.Agent
{
    /// <summary>How a failed call should be retried by the daemon.</summary>
    public enum RetryClass
    {
        /// <summary>Safe to repeat. Reads.</summary>
        Read,
        /// <summary>Repeat only with an idempotency key. Mutations.</summary>
        Write,
        /// <summary>May trigger a compile / domain reload. Long timeout.</summary>
        Compile,
        /// <summary>Never retried automatically.</summary>
        None
    }

    /// <summary>Rough cost hint, used for timeout selection and guidance.</summary>
    public enum Cost { Cheap, Moderate, Expensive }

    /// <summary>
    /// The single source of truth for a tool. Everything else — the dispatch table, the JSON
    /// Schema, the daemon-side catalog, the docs — is generated from this by Umcp.ToolGen.
    /// Hand-written switch statements and hand-maintained schemas are how the tool being
    /// replaced ended up with three copies of every tool definition and a regex that silently
    /// dropped some of them.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class UnityToolAttribute : Attribute
    {
        /// <summary>Dotted tool id, e.g. "gameobject.create". Required.</summary>
        public string Id;
        /// <summary>Skill-tree node this tool belongs to (Phase 2). Defaults to the id's first segment.</summary>
        public string Skill;
        /// <summary>One-line description. Required.</summary>
        public string Summary;
        /// <summary>True if the tool changes Editor or asset state.</summary>
        public bool Mutating;
        public RetryClass Retry = RetryClass.Read;
        public Cost Cost = Cost.Cheap;
        /// <summary>Undo group name, or null if the tool cannot participate in undo.</summary>
        public string Undo;
        /// <summary>If the tool cannot participate in undo, say why here.</summary>
        public string NoUndoReason;
    }

    /// <summary>A worked input example. Anthropic measured examples moving complex-parameter
    /// accuracy from 72% to 90%; this is the cheapest accuracy win available.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class ExampleAttribute : Attribute
    {
        public readonly string Json;
        public ExampleAttribute(string json) { Json = json; }
    }

    /// <summary>Parameter documentation, emitted into the generated JSON Schema.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class DocAttribute : Attribute
    {
        public readonly string Text;
        public DocAttribute(string text) { Text = text; }
    }

    /// <summary>Thrown by tools to produce a structured, machine-actionable error.</summary>
    public sealed class UmcpToolException : Exception
    {
        public readonly string Code;
        public readonly string Param;
        public readonly string Value;
        public readonly string[] DidYouMean;
        public readonly string Hint;

        public UmcpToolException(string code, string message, string param = null, string value = null,
                                 string[] didYouMean = null, string hint = null)
            : base(message)
        {
            Code = code; Param = param; Value = value; DidYouMean = didYouMean; Hint = hint;
        }
    }
}
