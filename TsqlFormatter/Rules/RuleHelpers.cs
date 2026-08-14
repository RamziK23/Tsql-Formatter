using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

internal static class RuleHelpers
{
    public const string Tab = "\t";

    public static string Tabs(int n) => new string('\t', n);

    /// <summary>
    /// Rule 2.15: joins type/identifier tokens with smart spacing.
    /// Keywords are lowercased. No space before '(' or ')' or ','; one space after ','.
    /// Examples: [varchar, (, 50, )] -> "varchar(50)";  [decimal, (, 18, ,, 2, )] -> "decimal(18, 2)".
    /// </summary>
    public static string EmitTypeTokens(System.Collections.Generic.IEnumerable<Token> tokens)
    {
        var sb = new System.Text.StringBuilder();
        Token? prev = null;
        foreach (var t in tokens)
        {
            string val = t.Type == TokenType.Keyword ? t.Value.ToLowerInvariant() : t.Value;

            if (prev != null)
            {
                bool noSpaceBefore =
                    t.Type == TokenType.LeftParen  ||
                    t.Type == TokenType.RightParen ||
                    t.Type == TokenType.Comma;
                bool prevOpensGroup = prev.Type == TokenType.LeftParen;
                bool prevIsComma    = prev.Type == TokenType.Comma;
                // "declare @t table (id int)": TABLE is a keyword, not a function —
                // keep the conventional space before its column-list paren.
                bool prevIsTableKeyword = prev.Type == TokenType.Keyword
                    && prev.Value.Equals("TABLE", StringComparison.OrdinalIgnoreCase);

                if (prevIsComma)            sb.Append(' ');   // "18, 2"
                else if (t.Type == TokenType.LeftParen && prevIsTableKeyword) sb.Append(' ');
                else if (noSpaceBefore)     { }               // no space before ( ) ,
                else if (prevOpensGroup)    { }               // no space right after (
                else                        sb.Append(' ');   // normal token gap
            }
            sb.Append(val);
            prev = t;
        }
        return sb.ToString();
    }

    // ─── Expression emitter ─────────────────────────────────────────────────

    public static string EmitExpr(AstNode node, FormatterEngine engine, int indent = 0)
    {
        return node switch
        {
            LiteralNode   l => EmitLiteral(l, indent),
            ColumnRefNode c => EmitColumnRef(c),
            BinaryExprNode b => EmitBinary(b, engine, indent),
            ParenExprNode p => $"({EmitExpr(p.Inner, engine, indent)})",
            ConditionGroupNode cg => EmitConditionGroup(cg, engine, indent),
            OrderByItemNode ob => EmitExpr(ob.Expression, engine, indent) + (ob.Direction != null ? " " + ob.Direction : ""),
            // A list item's own comments are rendered by the clause emitter (they need the
            // clause's indent); reached from elsewhere, only the expression matters.
            ListItemNode li => EmitExpr(li.Expression, engine, indent),
            InExprNode    i => EmitIn(i, engine, indent),
            BetweenExprNode bt => EmitBetween(bt, engine, indent),
            LikeExprNode  lk => EmitLike(lk, engine, indent),
            IsNullExprNode isn => EmitIsNull(isn, engine, indent),
            SubQueryNode  sq => EmitSubQuery(sq, engine, indent),
            WindowSpecNode ws => EmitOver(ws, engine, indent),
            FunctionCallNode fn => EmitFunction(fn, engine, indent),
            CaseExprNode  ce => EmitCase(ce, engine, indent),
            InValueGroupNode grp => grp.LeadingBlockComment
                                    + string.Join(", ", grp.Values.Select(v => EmitExpr(v, engine, indent))),
            NotExprNode n => $"not {EmitExpr(n.Inner, engine, indent)}",
            RawTokensNode rt => EmitRawTokens(rt.Tokens),
            _ => engine.Format(node, indent)
        };
    }

    /// <summary>
    /// Joins raw tokens with smart spacing (no space around '(' ')' ',' '.', one space
    /// after ','), lowercasing keywords and function names (identifier immediately before
    /// '('). Whitespace/newline tokens are ignored. Shared by raw statements (RawTokensRule)
    /// and window/OVER specs so both render "order by a.[Id]" rather than "ORDER BY a . [Id]".
    /// </summary>
    public static string EmitRawTokens(IEnumerable<Token> tokens)
    {
        var toks = tokens
            .Where(t => t.Type is not (TokenType.Whitespace or TokenType.Newline))
            .ToList();

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];

            bool isFunction = t.Type == TokenType.Identifier
                && i + 1 < toks.Count && toks[i + 1].Type == TokenType.LeftParen;
            string val = t.Type == TokenType.Keyword || isFunction ? t.Value.ToLowerInvariant() : t.Value;

            if (i > 0)
            {
                var prev = toks[i - 1];
                bool noSpaceBefore = t.Type is TokenType.LeftParen or TokenType.RightParen
                                            or TokenType.Comma or TokenType.Dot;
                if (prev.Type == TokenType.Comma)                              sb.Append(' ');  // "a, b"
                else if (noSpaceBefore)                                        { }               // before ( ) , .
                else if (prev.Type is TokenType.LeftParen or TokenType.Dot)    { }               // after ( or .
                else                                                           sb.Append(' ');   // normal gap
            }
            sb.Append(val);
        }
        return sb.ToString();
    }

    private static string EmitLiteral(LiteralNode l, int indent = 0)
    {
        // String literals (including multi-line dynamic SQL) are emitted VERBATIM: reindenting
        // their content would scramble hand-aligned dynamic SQL and shift -- comments inside the
        // string. Only keyword literals are case-normalized.
        var text = l.Token.Type == TokenType.Keyword ? l.Token.Value.ToLowerInvariant() : l.Token.Value;
        return l.TrailingComment != null ? $"{text}{TrailingCommentSuffix(l.TrailingComment)}" : text;
    }

    /// <summary>
    /// Renders a trailing comment. A /* */ block comment is transparent — it glues directly
    /// to the preceding text with no separator and no tabs. A -- line comment is offset by
    /// two tabs (the established trailing-comment gap).
    /// </summary>
    public static string TrailingCommentSuffix(string comment) =>
        comment.StartsWith("/*") ? comment : $"{Tabs(2)}{comment}";

    /// <summary>
    /// Splits a column's leading comments into two rendered parts: comments that take their own
    /// line above the column (prefixed by <paramref name="linePrefix"/>, ending in a newline) and
    /// comments glued inline before the expression. A -- comment always takes its own line, or the
    /// expression would be commented out; a /* */ comment glues only when the author wrote no line
    /// break after it, so the layout they chose survives.
    /// </summary>
    public static (string lineLead, string blockLead) SplitLeadingComments(
        System.Collections.Generic.List<LeadingComment> comments, string linePrefix)
    {
        var line = new System.Text.StringBuilder();
        var block = new System.Text.StringBuilder();
        foreach (var c in comments)
        {
            if (c.Text.StartsWith("/*") && !c.BreakAfter) block.Append(c.Text).Append(' ');
            else                                          line.Append(linePrefix).Append(c.Text).Append('\n');
        }
        return (line.ToString(), block.ToString());
    }

    public static (string lineLead, string blockLead) SplitLeadingComments(
        System.Collections.Generic.List<string> comments, string linePrefix)
    {
        var line = new System.Text.StringBuilder();
        var block = new System.Text.StringBuilder();
        foreach (var c in comments)
        {
            // A /* */ comment glues inline only while it fits on one line. One that spans lines
            // (a commented-out block of columns, say) keeps its own lines — glued, it would drag
            // the column onto the comment's closing line.
            bool inlineBlock = c.StartsWith("/*") && !c.Contains('\n');
            if (inlineBlock) block.Append(c).Append(' ');
            else             line.Append(linePrefix).Append(c).Append('\n');
        }
        return (line.ToString(), block.ToString());
    }

    private static string EmitColumnRef(ColumnRefNode c)
    {
        var text = string.Join("", c.Parts.Select(p =>
            p.Type == TokenType.Keyword ? p.Value.ToLowerInvariant() : p.Value));
        return c.TrailingComment != null ? $"{text}{TrailingCommentSuffix(c.TrailingComment)}" : text;
    }

    private static string EmitBinary(BinaryExprNode b, FormatterEngine engine, int indent)
    {
        var left  = EmitExpr(b.Left,  engine, indent);
        var right = EmitExpr(b.Right, engine, indent);
        // Keyword operators (e.g. COLLATE) are lowercased like other keywords.
        var op = b.Op.Type == TokenType.Keyword ? b.Op.Value.ToLowerInvariant() : b.Op.Value;
        return $"{left} {op} {right}";
    }

    private static string EmitIn(InExprNode i, FormatterEngine engine, int indent)
    {
        var left = EmitExpr(i.Left, engine, indent);
        var not  = i.Negated ? "not in" : "in";

        // Rule 2.4: IN (subquery) — subquery on the next line, +1 tab, closing paren aligned.
        if (i.SubQuery != null)
        {
            return $"{left} {not} {EmitSubQuery(i.SubQuery, engine, indent)}";
        }

        if (!i.HasComments)
        {
            var vals = string.Join(", ", i.Values.Select(v => EmitExpr(v, engine, indent)));
            return $"{left} {not} ({vals})";
        }

        // Commented IN list — comma before comment, each value/group on its own line
        var inLines = i.Values.Select((v, idx) =>
        {
            bool isLast = idx == i.Values.Count - 1;
            string comma = isLast ? "" : ",";

            if (v is CommentedValueNode cv)
                return $"{Tabs(indent + 1)}{EmitExpr(cv.Value, engine, indent)}{comma}{Tabs(2)}{cv.TrailingComment}";

            if (v is InValueGroupNode grp)
            {
                // Multiple values sharing one comment
                var vals = string.Join(", ", grp.Values.Select(gv => EmitExpr(gv, engine, indent)));
                string leading  = grp.LeadingBlockComment  != null ? grp.LeadingBlockComment  : "";
                string trailing = grp.TrailingLineComment  != null ? $"{Tabs(2)}{grp.TrailingLineComment}" : "";
                return $"{Tabs(indent + 1)}{leading}{vals}{comma}{trailing}";
            }

            return $"{Tabs(indent + 1)}{EmitExpr(v, engine, indent)}{comma}";
        });
        return $"{left} {not} (\n{string.Join("\n", inLines)}\n{Tabs(indent)})";
    }

    /// <summary>
    /// Renders a window function's OVER clause: the keyword on its own line one tab in, each
    /// PARTITION BY / ORDER BY item on a line of its own one tab further, the closing paren back
    /// at the OVER's indent — so a long window spec reads as a list, like every other list here.
    /// </summary>
    private static string EmitOver(AstNode over, FormatterEngine engine, int indent)
    {
        // A spec the parser could not break down stays inline, as before.
        if (over is not WindowSpecNode w) return $" over ({EmitExpr(over, engine, indent)})";

        var t1 = Tabs(indent + 1);
        var t2 = Tabs(indent + 2);
        var t3 = Tabs(indent + 3);
        var sb = new System.Text.StringBuilder();
        sb.Append($"\n{t1}over (");
        AppendWindowList(sb, "partition by", w.PartitionBy, engine, t2, t3, indent + 3);
        AppendWindowList(sb, "order by",     w.OrderBy,     engine, t2, t3, indent + 3);
        // The frame run also holds the whitespace before ')' — only emit it when there is a
        // real clause (ROWS/RANGE …) in there.
        if (w.Frame.Any(t => t.Type is not (TokenType.Whitespace or TokenType.Newline)))
            sb.Append($"\n{t2}{EmitRawTokens(w.Frame)}");
        sb.Append($"\n{t1})");
        return sb.ToString();
    }

    private static void AppendWindowList(System.Text.StringBuilder sb, string keyword,
        List<AstNode> items, FormatterEngine engine, string keywordIndent, string itemIndent, int indent)
    {
        if (items.Count == 0) return;
        sb.Append($"\n{keywordIndent}{keyword}");
        for (int i = 0; i < items.Count; i++)
        {
            sb.Append($"\n{itemIndent}{EmitExpr(items[i], engine, indent)}");
            if (i < items.Count - 1) sb.Append(",");
        }
    }

    private static string EmitBetween(BetweenExprNode bt, FormatterEngine engine, int indent)
        => $"{EmitExpr(bt.Left, engine, indent)} {(bt.Negated ? "not between" : "between")} "
         + $"{EmitExpr(bt.Low, engine, indent)} and {EmitExpr(bt.High, engine, indent)}";

    private static string EmitLike(LikeExprNode lk, FormatterEngine engine, int indent)
        => $"{EmitExpr(lk.Left, engine, indent)} {(lk.Negated ? "not like" : "like")} {EmitExpr(lk.Pattern, engine, indent)}";

    private static string EmitIsNull(IsNullExprNode isn, FormatterEngine engine, int indent)
        => $"{EmitExpr(isn.Left, engine, indent)} is{(isn.IsNotNull ? " not" : "")} null";

    public static string EmitSubQuery(SubQueryNode sq, FormatterEngine engine, int indent)
    {
        // A comment on the same line as '(' stays on that line (trailing the open paren).
        var open = sq.OpenComment != null ? TrailingCommentSuffix(sq.OpenComment) : "";
        var inner = engine.Format(sq.Select, indent + 1);
        // Comments written just before the ')' stay at the bottom of the subquery.
        var close = string.Concat(sq.CloseComments.Select(c => $"{Tabs(indent + 1)}{c}\n"));
        return $"({open}\n{inner}\n{close}{Tabs(indent)})";
    }

    private static string EmitFunction(FunctionCallNode fn, FormatterEngine engine, int indent)
    {
        bool isCast = fn.Name.Equals("CAST", StringComparison.OrdinalIgnoreCase)
                   || fn.Name.Equals("TRY_CAST", StringComparison.OrdinalIgnoreCase)
                   || fn.Name.Equals("CONVERT", StringComparison.OrdinalIgnoreCase)
                   || fn.Name.Equals("TRY_CONVERT", StringComparison.OrdinalIgnoreCase);

        if (isCast)
        {
            bool isConvert = fn.Name.Equals("CONVERT", StringComparison.OrdinalIgnoreCase)
                          || fn.Name.Equals("TRY_CONVERT", StringComparison.OrdinalIgnoreCase);
            if (isConvert)
            {
                var argStrs = fn.Arguments.Select(a => EmitExpr(a, engine, indent)).ToList();
                return $"{fn.Name.ToLowerInvariant()}({string.Join(", ", argStrs)})";
            }
            else
            {
                if (fn.Arguments.Count >= 2)
                {
                    var expr     = EmitExpr(fn.Arguments[0], engine, indent);
                    var dataType = EmitExpr(fn.Arguments[1], engine, indent);
                    return $"{fn.Name.ToLowerInvariant()}({expr} as {dataType})";
                }
            }
        }

        if (fn.Name.Equals("EXISTS", StringComparison.OrdinalIgnoreCase) && fn.Arguments.Count == 1)
            return $"{(fn.Negated ? "not " : "")}exists {EmitExpr(fn.Arguments[0], engine, indent)}";

        // Lowercase keyword functions and unqualified (system) function names — e.g. exp(),
        // newid(). A schema-qualified name (dbo.MyFunc) keeps its case.
        var fnName = (fn.IsKeywordFunction || !fn.Name.Contains('.'))
            ? fn.Name.ToLowerInvariant() : fn.Name;
        var overStr = fn.OverClause != null ? EmitOver(fn.OverClause, engine, indent) : "";

        // Decide whether to break arguments onto their own lines:
        // do so when at least one argument renders as multiline (contains a CASE,
        // a subquery, or a nested function that itself broke). Otherwise keep inline.
        var rendered = fn.Arguments.Select(a => EmitExpr(a, engine, indent + 1)).ToList();
        bool anyMultiline = rendered.Any(r => r.Contains('\n'));
        var quant = fn.SetQuantifier != null ? fn.SetQuantifier + " " : "";

        if (anyMultiline && fn.Arguments.Count > 0)
        {
            var t0 = Tabs(indent);
            var t1 = Tabs(indent + 1);
            var sb = new System.Text.StringBuilder();
            sb.Append($"{fnName}(\n");
            for (int i = 0; i < rendered.Count; i++)
            {
                // DISTINCT/ALL prefixes the first argument.
                var pref = i == 0 ? quant : "";
                sb.Append($"{t1}{pref}{rendered[i]}");
                if (i < rendered.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append($"{t0}){overStr}");
            return sb.ToString();
        }

        var args = string.Join(", ", fn.Arguments.Select(a => EmitExpr(a, engine, indent)));
        return $"{fnName}({quant}{args}){overStr}";
    }

    private static string EmitCase(CaseExprNode ce, FormatterEngine engine, int indent)
    {
        var sb = new System.Text.StringBuilder();
        string baseIndent = Tabs(indent);
        string whenIndent = Tabs(indent + 1);

        sb.Append("case");
        if (ce.InputExpr != null)
            sb.Append($" {EmitExpr(ce.InputExpr, engine, indent)}");

        foreach (var wc in ce.WhenClauses)
        {
            var conditions = wc.Conditions.Select((c, idx) =>
            {
                var cn   = c as ConditionNode;
                string cStr = EmitExpr(cn?.Expression ?? c, engine, indent + 1);
                // Continuation conditions start the next line with a lowercase and/or,
                // rather than trailing "AND" at the end of the previous line.
                string op = cn?.LogicalOp ?? "and";
                return idx == 0 ? cStr : $"\n{Tabs(indent + 2)}{op} {cStr}";
            });
            string condStr = string.Concat(conditions);
            string thenStr = EmitExpr(wc.Then, engine, indent + 1);
            string thenComment = wc.ThenComment != null ? $"{Tabs(2)}{wc.ThenComment}" : "";
            // when and then on separate lines, both at the same indent
            sb.Append($"\n{whenIndent}when {condStr}");
            sb.Append($"\n{whenIndent}then {thenStr}{thenComment}");
        }

        if (ce.ElseExpr != null)
        {
            string elseStr = EmitExpr(ce.ElseExpr, engine, indent + 1);
            string elseComment = ce.ElseComment != null ? $"{Tabs(2)}{ce.ElseComment}" : "";
            sb.Append($"\n{whenIndent}else {elseStr}{elseComment}");
        }

        sb.Append($"\n{baseIndent}end");
        return sb.ToString();
    }

    // ─── Condition list emitter ──────────────────────────────────────────────

    /// <summary>
    /// Emits a list of ConditionNodes with AND/OR operators.
    /// Each condition on its own indented line.
    /// </summary>
    public static string EmitConditions(List<AstNode> conditions, FormatterEngine engine, int indent)
    {
        var lines = new List<string>();
        foreach (var c in conditions)
        {
            if (c is ConditionNode cond)
            {
                // Standalone comments on their own line(s) above the condition.
                foreach (var lc in cond.LeadingComments)
                    lines.Add($"{Tabs(indent)}{lc}");
                string prefix = cond.LogicalOp != null ? $"{cond.LogicalOp} " : "";
                string expr   = EmitExpr(cond.Expression, engine, indent);
                string cmt    = cond.TrailingComment != null ? $" {cond.TrailingComment}" : "";
                lines.Add($"{Tabs(indent)}{prefix}{expr}{cmt}");
            }
            else
            {
                lines.Add($"{Tabs(indent)}{EmitExpr(c, engine, indent)}");
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Emits a condition list whose FIRST condition stays on the keyword's line ("if @i &lt; 1"):
    /// the returned text starts with the first condition (no leading tabs, for appending after
    /// "if ") and every following condition sits on its own line at <paramref name="indent"/> + 1
    /// tab. Returns null when the first condition carries standalone -- comments, which must keep
    /// their own line above the condition — the caller then falls back to the keyword-only layout.
    /// </summary>
    public static string? EmitConditionsInline(List<AstNode> conditions, FormatterEngine engine, int indent)
    {
        if (conditions.Count == 0) return null;
        if (conditions[0] is ConditionNode head && head.LeadingComments.Count > 0) return null;

        var lines = new List<string>();
        foreach (var c in conditions)
        {
            if (c is ConditionNode cond)
            {
                // Standalone comments on their own line(s) above the condition.
                lines.AddRange(cond.LeadingComments);
                string prefix = cond.LogicalOp != null ? $"{cond.LogicalOp} " : "";
                string expr   = EmitExpr(cond.Expression, engine, indent + 1);
                string cmt    = cond.TrailingComment != null ? $" {cond.TrailingComment}" : "";
                lines.Add($"{prefix}{expr}{cmt}");
            }
            else
            {
                lines.Add(EmitExpr(c, engine, indent + 1));
            }
        }
        return lines[0] + string.Concat(lines.Skip(1).Select(l => $"\n{Tabs(indent + 1)}{l}"));
    }

    /// <summary>
    /// Renders the body of an IF / ELSE branch: on the next line at the IF's own indent, with no
    /// blank line and no extra tab. A BEGIN…END block is the same — the blank lines it already
    /// carries inside are what set the executed statements apart from the conditions.
    /// </summary>
    public static string EmitBranchBody(AstNode body, FormatterEngine engine, int indent)
        => $"\n{engine.Format(body, indent)}";

    // ─── Table ref emitter ──────────────────────────────────────────────────

    public static string EmitTableRef(TableRefNode t, FormatterEngine engine, int indent = 0)
    {
        string nameStr;
        if (t.SubQuery != null)
        {
            nameStr = EmitSubQuery(t.SubQuery, engine, indent);
        }
        else if (t.IsOpenQuery && t.FuncArgs != null && t.FuncArgs.Count >= 1)
        {
            // Rule 7: openquery(server, 'remote sql')
            //   openquery(
            //       server_name,
            //       'remote sql'
            //   )
            // Server name on its own line (7.1), remote query at +1 tab (7.2),
            // closing paren back at the opening line's indent (7.4 / 2.14.1.3).
            var t0 = Tabs(indent);
            var t1 = Tabs(indent + 1);
            var args = t.FuncArgs.Select(a => EmitExpr(a, engine, indent + 1)).ToList();
            var sb = new System.Text.StringBuilder();
            sb.Append("openquery(\n");
            for (int i = 0; i < args.Count; i++)
            {
                sb.Append($"{t1}{args[i]}");
                if (i < args.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append($"{t0})");
            nameStr = sb.ToString();
        }
        else if (t.FuncArgs != null)
        {
            // Function-valued table source: func(arg1, arg2)
            var funcName = string.Join("", t.Name.Select(p => p.Value));
            var argsStr  = string.Join(", ", t.FuncArgs.Select(a => EmitExpr(a, engine, indent)));
            nameStr = $"{funcName}({argsStr})";
        }
        else
        {
            nameStr = string.Join("", t.Name.Select(p => p.Value));
        }
        var withAlias = t.Alias != null ? $"{nameStr} as {t.Alias.Value}" : nameStr;
        return t.HintNolock != null ? $"{withAlias} with ({t.HintNolock})" : withAlias;
    }

    // ─── Shared JOIN formatter ───────────────────────────────────────────────

    /// <summary>
    /// Rule 2.2: JOIN at +1 tab, ON at +2 tabs.
    /// First condition always on same line as ON.
    /// Additional conditions on new lines at +2 tabs.
    /// </summary>
    public static string FormatJoin(JoinNode join, FormatterEngine engine, int indent)
    {
        var tabs = Tabs(indent);
        var sb   = new System.Text.StringBuilder();

        // Standalone comments that preceded this join, each on its own line at join indent.
        foreach (var c in join.LeadingComments)
            sb.Append($"\n{tabs}\t{c}");

        sb.Append($"\n{tabs}\t{join.JoinType.ToLowerInvariant()} {EmitTableRef(join.Table, engine, indent + 1)}");
        if (join.TrailingComment != null) sb.Append(TrailingCommentSuffix(join.TrailingComment));

        if (join.Conditions.Count == 0) return sb.ToString();

        // Comments between the join line and ON go above the ON line.
        if (join.Conditions[0] is ConditionNode head)
            foreach (var lc in head.LeadingComments)
                sb.Append($"\n{tabs}\t\t{lc}");

        // First condition always on same line as ON
        sb.Append($"\n{tabs}\t\ton");

        if (join.Conditions[0] is ConditionNode first)
        {
            string firstCmt = first.TrailingComment != null ? $" {first.TrailingComment}" : "";
            sb.Append($" {EmitExpr(first.Expression, engine, indent + 2)}{firstCmt}");
            // Remaining conditions on new lines
            foreach (var c in join.Conditions.Skip(1))
            {
                if (c is ConditionNode rest)
                {
                    // Standalone comments on their own line(s) above the condition.
                    foreach (var lc in rest.LeadingComments)
                        sb.Append($"\n{tabs}\t\t{lc}");
                    string prefix = rest.LogicalOp != null ? $"{rest.LogicalOp} " : "";
                    string cmt    = rest.TrailingComment != null ? $" {rest.TrailingComment}" : "";
                    sb.Append($"\n{tabs}\t\t{prefix}{EmitExpr(rest.Expression, engine, indent + 2)}{cmt}");
                }
            }
        }
        else
        {
            sb.Append($"\n{EmitConditions(join.Conditions, engine, indent + 2)}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders a parenthesised boolean group inline within a condition:
    ///   (
    ///       a = 1
    ///       or b = 2
    ///   )
    /// Indented one level deeper than the enclosing condition.
    /// </summary>
    public static string EmitConditionGroup(ConditionGroupNode cg, FormatterEngine engine, int indent)
    {
        var t0 = Tabs(indent);
        var t1 = Tabs(indent + 1);
        var sb = new System.Text.StringBuilder();
        sb.Append("(");
        // A comment written on the '(' line stays there.
        if (cg.OpenComment != null) sb.Append(TrailingCommentSuffix(cg.OpenComment));
        sb.Append("\n");
        for (int i = 0; i < cg.Conditions.Count; i++)
        {
            if (cg.Conditions[i] is ConditionNode cn)
            {
                // Standalone comments on their own line(s) above the condition.
                foreach (var lc in cn.LeadingComments)
                    sb.Append($"{t1}{lc}\n");
                string prefix = cn.LogicalOp != null ? $"{cn.LogicalOp} " : "";
                string cmt    = cn.TrailingComment != null ? TrailingCommentSuffix(cn.TrailingComment) : "";
                sb.Append($"{t1}{prefix}{EmitExpr(cn.Expression, engine, indent + 1)}{cmt}\n");
            }
        }
        sb.Append($"{t0})");
        return sb.ToString();
    }

}

}
