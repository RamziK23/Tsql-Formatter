using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

/// <summary>
/// Rule 2.8: BEGIN ... END block formatting.
///   begin
///
///       [statements indented +1 tab]
///
///   end
/// </summary>
public sealed class BeginEndRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is BeginEndNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var b    = (BeginEndNode)node;
        var tabs = RuleHelpers.Tabs(indent);
        var sb   = new StringBuilder();
        // "begin try" … "end try" / "begin catch" … "end catch"; empty for a plain block.
        var label = b.Label != null ? " " + b.Label : "";

        sb.Append($"{tabs}begin{label}\n");

        if (b.Body.Count > 0)
        {
            sb.Append("\n"); // empty line after begin

            string? pendingComment = null;
            string pendingSep = "\n";
            var rendered = new System.Collections.Generic.List<string>();

            foreach (var stmt in b.Body)
            {
                var text = engine.Format(stmt, indent + 1);
                if (string.IsNullOrWhiteSpace(text)) continue;
                var formatted = text.TrimEnd();

                // Standalone comment attaches to the following statement (single newline
                // unless the source had a blank line after the comment).
                if (stmt is RawTokensNode raw && IsCommentOnly(raw))
                {
                    string sep = raw.BlankAfter ? "\n\n" : "\n";
                    pendingComment = pendingComment == null ? formatted : pendingComment + pendingSep + formatted;
                    pendingSep = sep;
                    continue;
                }
                if (pendingComment != null)
                {
                    formatted = pendingComment + pendingSep + formatted;
                    pendingComment = null;
                    pendingSep = "\n";
                }
                // The ';' the author wrote goes back where it was, before any trailing comment.
                if (stmt.TrailingSemicolon) formatted += ";";
                // A same-line trailing comment sticks to the statement's last line.
                if (stmt.StatementTrailingComment != null)
                    formatted = AppendTrailing(formatted, stmt.StatementTrailingComment);
                rendered.Add(formatted);
            }
            if (pendingComment != null) rendered.Add(pendingComment);

            foreach (var r in rendered)
                sb.Append(r + "\n\n");
            // The last \n\n already provides the blank line before end
        }

        sb.Append($"{tabs}end{label}");
        return sb.ToString();
    }

    /// <summary>Appends a trailing comment to the last line of already-formatted text.</summary>
    private static string AppendTrailing(string text, string comment)
    {
        int nl = text.LastIndexOf('\n');
        var suffix = RuleHelpers.TrailingCommentSuffix(comment);
        return nl < 0 ? text + suffix : text.Substring(0, nl + 1) + text.Substring(nl + 1) + suffix;
    }

    private static bool IsCommentOnly(RawTokensNode raw)
    {
        if (raw.Tokens.Count == 0) return false;
        return raw.Tokens.All(t =>
            t.Type == TokenType.LineComment || t.Type == TokenType.BlockComment
            || t.Type == TokenType.Whitespace || t.Type == TokenType.Newline);
    }
}

}
