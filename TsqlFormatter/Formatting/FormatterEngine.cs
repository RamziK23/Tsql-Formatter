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
        try
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
        catch (ParseException)
        {
            // Graceful degradation: couldn't parse reliably, so return the input unchanged
            // rather than emit desynchronized/broken SQL.
            return source;
        }
    }

    public string Format(AstNode node, int indent = 0)
    {
        foreach (var rule in _rules)
            if (rule.CanHandle(node))
                return rule.Format(node, this, indent);

        return $"/* unhandled: {node.GetType().Name} */";
    }

    /// <summary>Appends a trailing -- comment to the last line of already-formatted text,
    /// using the two-tab gap convention shared with column/where trailing comments.</summary>
    private static string AppendTrailingComment(string text, string comment)
    {
        int nl = text.LastIndexOf('\n');
        return nl < 0
            ? text + "\t\t" + comment
            : text.Substring(0, nl + 1) + text.Substring(nl + 1) + "\t\t" + comment;
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
        // Each part carries whether a blank line should precede it, so the original
        // blank-line structure between statements is preserved instead of forced.
        var parts = new List<(string text, bool blank)>();
        bool isFirst = true;
        bool prevWasGo = false;
        string? pendingComment = null;  // a standalone comment awaiting the next statement
        string pendingSep = "\n";       // separator between the pending comment and next statement
        bool pendingBlank = false;      // blank line before the pending comment block

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
                    pendingBlank = raw.BlankBefore;
                }
                else
                {
                    pendingComment = pendingComment + pendingSep + formatted;
                    pendingSep = sep;
                }
                continue;
            }

            // A same-line trailing -- comment sticks to the statement's last line.
            if (stmt.StatementTrailingComment != null)
                formatted = AppendTrailingComment(formatted, stmt.StatementTrailingComment);

            bool blankBefore;
            if (pendingComment != null)
            {
                formatted = pendingComment + pendingSep + formatted;
                blankBefore = pendingBlank;
                pendingComment = null;
            }
            else
            {
                blankBefore = stmt.BlankLineBefore;
            }

            // GO batch separators always sit on their own blank-line-separated line.
            if (prevWasGo || stmt is GoSeparatorNode) blankBefore = true;

            // Restore leading semicolon before the first statement (e.g. ;with ...)
            if (isFirst && script.HasLeadingSemicolon)
                formatted = ";" + formatted;

            parts.Add((formatted, blankBefore));
            isFirst = false;
            prevWasGo = stmt is GoSeparatorNode;
        }

        // A trailing comment with no following statement stands on its own.
        if (pendingComment != null) parts.Add((pendingComment, prevWasGo || pendingBlank));

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) sb.Append(parts[i].blank ? "\n\n" : "\n");
            sb.Append(parts[i].text);
        }
        return sb.ToString() + "\n";
    }
}

}
