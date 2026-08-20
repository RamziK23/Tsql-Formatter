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
            {
                var frag = engine.Format(fragment).TrimEnd();
                return IsFaithful(source, frag) ? EndLike(source, frag) : source;
            }

            // Fall back to full-statement parsing.
            var script = new Parser(new Lexer(source).Tokenize()).Parse();
            var text = engine.FormatScript(script);

            // When a statement was cut short, only its parsable prefix went through the rules —
            // and a prefix can end on a clause the emitter drops when it is empty (a dangling
            // "where", say). Verify that nothing from the source went missing; if something did,
            // re-format leaving that whole statement verbatim.
            if (script.UnparsedTailGlued && !IsFaithful(source, text))
                text = engine.FormatScript(
                    new Parser(new Lexer(source).Tokenize()).Parse(recoverPartialStatement: false));

            // Last gate: a comment in an unusual place must never cost text. If the output lost a
            // token or a comment, or let a -- comment swallow code that followed it, retry with
            // every comment hoisted above its statement — a coarser but always-safe placement.
            // If even that is not faithful, the source comes back untouched, the same graceful
            // degradation as a parse failure.
            if (IsFaithful(source, text)) return EndLike(source, text);

            var hoisted = engine.FormatScript(
                new Parser(new Lexer(source).Tokenize(), hoistComments: true).Parse());
            return IsFaithful(source, hoisted) ? EndLike(source, hoisted) : source;
        }
        catch (ParseException)
        {
            // Graceful degradation: couldn't parse reliably, so return the input unchanged
            // rather than emit desynchronized/broken SQL.
            return source;
        }
    }

    /// <summary>
    /// Ends the result the way the source ended. A selection that did not finish with a line break
    /// must not gain one — pasted back over the selection it would show up as an added empty line.
    /// </summary>
    private static string EndLike(string source, string result)
    {
        var body = result.TrimEnd('\n', '\r');
        return source.EndsWith("\n") || source.EndsWith("\r") ? body + "\n" : body;
    }

    /// <summary>
    /// True when <paramref name="result"/> still says everything <paramref name="source"/> said:
    /// no token and no comment went missing, and no -- comment ended up with code after it on the
    /// same line (which would comment that code out). Formatting is free to move things around;
    /// it is never free to lose them or to hide them behind a comment.
    /// </summary>
    private static bool IsFaithful(string source, string result) =>
        KeepsEveryToken(source, result)
        && KeepsEveryComment(source, result)
        && NothingHidesBehindLineComment(result);

    /// <summary>True when every comment of the source appears in the result, verbatim.</summary>
    private static bool KeepsEveryComment(string source, string result)
    {
        try
        {
            var have = CommentCounts(result);
            foreach (var kv in CommentCounts(source))
            {
                have.TryGetValue(kv.Key, out int n);
                if (n < kv.Value) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static Dictionary<string, int> CommentCounts(string text)
    {
        var counts = new Dictionary<string, int>();
        foreach (var t in new Lexer(text).Tokenize())
        {
            if (t.Type is not (TokenType.LineComment or TokenType.BlockComment)) continue;
            var key = t.Value.TrimEnd();
            counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
        }
        return counts;
    }

    /// <summary>
    /// True when nothing follows a -- comment on its own line: everything after one, up to the
    /// newline, is commented out, so code placed there would be silently disabled.
    /// </summary>
    private static bool NothingHidesBehindLineComment(string text)
    {
        try
        {
            var tokens = new Lexer(text).Tokenize();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Type != TokenType.LineComment) continue;
                for (int j = i + 1; j < tokens.Count; j++)
                {
                    if (tokens[j].Type == TokenType.Whitespace) continue;
                    if (tokens[j].Type is TokenType.Newline or TokenType.EndOfFile) break;
                    return false;   // code (or another comment) trails the -- comment
                }
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// True when every meaningful token of <paramref name="source"/> — names, literals, variables
    /// and keywords, compared case-insensitively — is still present in <paramref name="result"/>.
    /// Whitespace, punctuation and comments are ignored, and additions are fine (an alias rewrite
    /// introduces "as"): the point is that formatting must never make part of the input vanish.
    /// </summary>
    private static bool KeepsEveryToken(string source, string result)
    {
        try
        {
            var have = MeaningfulTokens(result);
            foreach (var kv in MeaningfulTokens(source))
            {
                have.TryGetValue(kv.Key, out int n);
                if (n < kv.Value) return false;
            }
            return true;
        }
        catch { return false; }   // could not verify — treat as unsafe
    }

    private static Dictionary<string, int> MeaningfulTokens(string text)
    {
        var counts = new Dictionary<string, int>();
        foreach (var t in new Lexer(text).Tokenize())
        {
            if (t.Type is TokenType.Whitespace or TokenType.Newline or TokenType.EndOfFile
                or TokenType.LineComment or TokenType.BlockComment) continue;
            // "left outer join" is normalized to "left join" (rule 2.2), so OUTER is the one
            // word the formatter is meant to drop — it must not count as lost text.
            if (t.Value.Equals("OUTER", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (t.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Variable
                or TokenType.NumberLiteral or TokenType.StringLiteral
                or TokenType.Keyword or TokenType.DeclareKeyword)
            {
                var key = t.Value.ToLowerInvariant();
                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }
        return counts;
    }

    public string Format(AstNode node, int indent = 0)
    {
        foreach (var rule in _rules)
            if (rule.CanHandle(node))
            {
                var text = rule.Format(node, this, indent);
                // Comments the parser could not place inside the statement are printed above it,
                // each on its own line at the statement's indent.
                if (node.HoistedComments.Count > 0)
                {
                    var tabs = new string('\t', indent);
                    text = string.Concat(node.HoistedComments.Select(c => $"{tabs}{c}\n")) + text;
                }
                return text;
            }

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

    /// <summary>True for a BEGIN TRY / BEGIN CATCH block (a labelled BEGIN … END).</summary>
    private static bool IsTryCatchBlock(AstNode node) => node is BeginEndNode { Label: not null };

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
        bool prevWasTryCatch = false;
        string? pendingComment = null;  // a standalone comment awaiting the next statement
        string pendingSep = "\n";       // separator between the pending comment and next statement
        bool pendingBlank = false;      // blank line before the pending comment block

        foreach (var stmt in script.Statements)
        {
            var text = Format(stmt);
            if (string.IsNullOrWhiteSpace(text)) { continue; }

            var formatted = text.TrimEnd();

            // Re-emit the mandatory ';' before a WITH (CTE) statement — T-SQL requires the
            // previous statement to be terminated, so dropping it would break the script.
            if (stmt.LeadingSemicolon)
                formatted = ";" + formatted;

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

            // The ';' the author wrote at the end of the statement goes back where it was:
            // glued to the last token, before any trailing comment.
            if (stmt.TrailingSemicolon) formatted += ";";

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
            // A TRY / CATCH block stands apart from what surrounds it — "begin transaction" above
            // it and the "if @@trancount" below it each get their own blank line.
            if (prevWasTryCatch || IsTryCatchBlock(stmt)) blankBefore = true;

            // Restore leading semicolon before the first statement (e.g. ;with ...)
            if (isFirst && script.HasLeadingSemicolon)
                formatted = ";" + formatted;

            parts.Add((formatted, blankBefore));
            isFirst = false;
            prevWasGo = stmt is GoSeparatorNode;
            prevWasTryCatch = IsTryCatchBlock(stmt);
        }

        // A trailing comment with no following statement stands on its own.
        if (pendingComment != null) parts.Add((pendingComment, prevWasGo || pendingBlank));

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) sb.Append(parts[i].blank ? "\n\n" : "\n");
            sb.Append(parts[i].text);
        }
        // A construct the input cut short is appended exactly as it was written. A tail left over
        // from a statement that was partly formatted already carries the original whitespace that
        // stood before it, so it needs no separator of its own.
        if (script.UnparsedTail != null)
        {
            if (parts.Count > 0 && !script.UnparsedTailGlued)
                sb.Append(script.UnparsedTailBlankBefore ? "\n\n" : "\n");
            sb.Append(script.UnparsedTail.TrimEnd());
        }
        return sb.ToString() + "\n";
    }
}

}
