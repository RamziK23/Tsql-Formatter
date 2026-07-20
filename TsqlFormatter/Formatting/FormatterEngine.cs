using System.Collections.Generic;
using System.Linq;
using TsqlFormatter.Core;
using TsqlFormatter.Rules;

namespace TsqlFormatter.Formatting
{

public sealed class FormatterEngine
{
    private readonly List<IFormatterRule> _rules;

    public FormatterEngine(IEnumerable<IFormatterRule> rules)
    {
        _rules = rules.ToList();
    }

    /// <summary>
    /// Single entry point used by all CLI modes. Tries to format the source as a partial
    /// selection (fragment) first; if it isn't a recognizable fragment, formats it as a
    /// full script. This lets users format just a WHERE clause, column list, or JOIN chain.
    /// </summary>
    public static string FormatSource(string source)
    {
        var engine = FormatterFactory.Create();

        // Attempt fragment parse on a fresh token stream.
        var fragment = new Parser(new Lexer(source).Tokenize()).ParseFragment();
        if (fragment != null)
            return engine.Format(fragment).TrimEnd() + "\n";

        // Fall back to full-statement parsing.
        var script = new Parser(new Lexer(source).Tokenize()).Parse();
        return engine.FormatScript(script);
    }

    public string Format(AstNode node, int indent = 0)
    {
        foreach (var rule in _rules)
            if (rule.CanHandle(node))
                return rule.Format(node, this, indent);

        return $"/* unhandled: {node.GetType().Name} */";
    }

    /// <summary>True if the node is a standalone comment (a RawTokensNode of only comment tokens).</summary>
    private static bool IsCommentOnly(AstNode node)
    {
        if (node is not RawTokensNode raw) return false;
        if (raw.Tokens.Count == 0) return false;
        return raw.Tokens.All(t =>
            t.Type == TokenType.LineComment || t.Type == TokenType.BlockComment
            || t.Type == TokenType.Whitespace || t.Type == TokenType.Newline);
    }

    public string FormatScript(ScriptNode script)
    {
        var parts = new List<string>();
        bool isFirst = true;
        string? pendingComment = null;  // a standalone comment awaiting the next statement
        string pendingSep = "\n";       // separator between the pending comment and next statement

        foreach (var stmt in script.Statements)
        {
            var text = Format(stmt);
            if (string.IsNullOrWhiteSpace(text)) { continue; }

            var formatted = text.TrimEnd();

            // A standalone comment attaches to the FOLLOWING statement with a single newline
            // (no blank line between the comment and what it annotates).
            if (IsCommentOnly(stmt))
            {
                var raw = (RawTokensNode)stmt;
                string sep = raw.BlankAfter ? "\n\n" : "\n";
                if (pendingComment == null)
                {
                    pendingComment = formatted;
                    pendingSep = sep;
                }
                else
                {
                    pendingComment = pendingComment + pendingSep + formatted;
                    pendingSep = sep;
                }
                continue;
            }

            if (pendingComment != null)
            {
                formatted = pendingComment + pendingSep + formatted;
                pendingComment = null;
            }

            // Restore leading semicolon before the first statement (e.g. ;with ...)
            if (isFirst && script.HasLeadingSemicolon)
                formatted = ";" + formatted;

            parts.Add(formatted);
            isFirst = false;
        }

        // A trailing comment with no following statement stands on its own.
        if (pendingComment != null) parts.Add(pendingComment);

        // Ensure the script ends with exactly one GO if the last real node was a GoSeparatorNode
        // (trailing GO(s) in the source collapse to one)
        var lastMeaningful = script.Statements.LastOrDefault(s => s is not GoSeparatorNode);
        bool trailingGo = script.Statements.Count > 0
            && script.Statements[^1] is GoSeparatorNode;

        // Remove any trailing GO entries from parts (they were emitted as "GO")
        // and re-add exactly one if the source had trailing GOs
        while (parts.Count > 0 && parts[^1] == "GO")
            parts.RemoveAt(parts.Count - 1);

        if (trailingGo)
            parts.Add("GO");

        return string.Join("\n\n", parts) + "\n";
    }
}

}
