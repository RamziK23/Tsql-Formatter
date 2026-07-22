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

                if (prevIsComma)            sb.Append(' ');   // "18, 2"
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
            InExprNode    i => EmitIn(i, engine, indent),
            BetweenExprNode bt => EmitBetween(bt, engine, indent),
            LikeExprNode  lk => EmitLike(lk, engine, indent),
            IsNullExprNode isn => EmitIsNull(isn, engine, indent),
            SubQueryNode  sq => EmitSubQuery(sq, engine, indent),
            FunctionCallNode fn => EmitFunction(fn, engine, indent),
            CaseExprNode  ce => EmitCase(ce, engine, indent),
            InValueGroupNode grp => string.Join(", ", grp.Values.Select(v => EmitExpr(v, engine, indent))),
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
        string text;
        if (l.Token.Type == TokenType.StringLiteral && l.Token.Value.Contains('\n'))
        {
            // Rule 2.13: dynamic SQL string. Content starts on the next line at +1 tab;
            // the closing quote sits at the declaring line's indent.
            text = ReindentDynamicSql(l.Token.Value, indent);
        }
        else
        {
            text = l.Token.Type == TokenType.Keyword
                ? l.Token.Value.ToLowerInvariant()
                : l.Token.Value;
        }
        return l.TrailingComment != null ? $"{text}{Tabs(2)}{l.TrailingComment}" : text;
    }

    /// <summary>
    /// Rule 2.13: re-indents a multi-line string literal (dynamic SQL).
    /// The literal value still includes its surrounding single quotes.
    /// Inner content keeps its RELATIVE indentation but the whole block is shifted so
    /// its shallowest line sits at indent+1 tabs; the closing-quote line aligns to indent.
    /// </summary>
    private static string ReindentDynamicSql(string raw, int indent)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= 1) return raw;

        // Determine the minimum leading-whitespace width among non-empty inner lines.
        // The last line is a "bare closing quote" line if, after trimming, only the
        // closing single-quote remains (the literal value includes its quotes).
        bool lastIsClose = lines[lines.Length - 1].Trim() == "'";
        int lastContentIdx = lastIsClose ? lines.Length - 2 : lines.Length - 1;
        int minWidth = int.MaxValue;
        for (int i = 1; i <= lastContentIdx; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            int w = LeadingWidth(lines[i]);
            if (w < minWidth) minWidth = w;
        }
        if (minWidth == int.MaxValue) minWidth = 0;

        var baseIndent = Tabs(indent + 1);
        var sb = new System.Text.StringBuilder();
        // The last line is the "closing" line only if it carries just the closing quote
        // (i.e. it is effectively empty after the leading whitespace). For intermediate
        // concatenated literals like '\n... from ' the last line is real content.
        bool lastIsBareClose = lines[lines.Length - 1].Trim() == "'";

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (i == 0)
            {
                sb.Append(line.TrimEnd());                 // opening quote line
            }
            else if (i == lines.Length - 1 && lastIsBareClose)
            {
                sb.Append('\n');
                sb.Append(Tabs(indent) + line.TrimStart()); // closing quote aligned to indent
            }
            else if (line.Trim().Length == 0)
            {
                sb.Append('\n');                            // keep blank lines blank
            }
            else
            {
                // Strip the common minimum prefix, keep the rest (relative nesting),
                // then prepend the base indent.
                string stripped = StripWidth(line, minWidth);
                sb.Append('\n');
                sb.Append(baseIndent + stripped);
            }
        }
        return sb.ToString();
    }

    // Treats a tab as 4 columns for measuring leading whitespace width.
    private static int LeadingWidth(string line)
    {
        int w = 0;
        foreach (char c in line)
        {
            if (c == '\t') w += 4;
            else if (c == ' ') w += 1;
            else break;
        }
        return w;
    }

    // Removes `width` columns of leading whitespace (tab = 4 cols), preserving the remainder.
    private static string StripWidth(string line, int width)
    {
        int consumed = 0, idx = 0;
        while (idx < line.Length && consumed < width)
        {
            if (line[idx] == '\t') consumed += 4;
            else if (line[idx] == ' ') consumed += 1;
            else break;
            idx++;
        }
        // Convert any remaining leading spaces (4 spaces -> 1 tab) for consistency
        var rest = line.Substring(idx);
        return rest;
    }

    private static string EmitColumnRef(ColumnRefNode c)
    {
        var text = string.Join("", c.Parts.Select(p =>
            p.Type == TokenType.Keyword ? p.Value.ToLowerInvariant() : p.Value));
        return c.TrailingComment != null ? $"{text}{Tabs(2)}{c.TrailingComment}" : text;
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

    private static string EmitBetween(BetweenExprNode bt, FormatterEngine engine, int indent)
        => $"{EmitExpr(bt.Left, engine, indent)} between {EmitExpr(bt.Low, engine, indent)} and {EmitExpr(bt.High, engine, indent)}";

    private static string EmitLike(LikeExprNode lk, FormatterEngine engine, int indent)
        => $"{EmitExpr(lk.Left, engine, indent)} like {EmitExpr(lk.Pattern, engine, indent)}";

    private static string EmitIsNull(IsNullExprNode isn, FormatterEngine engine, int indent)
        => $"{EmitExpr(isn.Left, engine, indent)} is{(isn.IsNotNull ? " not" : "")} null";

    public static string EmitSubQuery(SubQueryNode sq, FormatterEngine engine, int indent)
    {
        var inner = engine.Format(sq.Select, indent + 1);
        return $"(\n{inner}\n{Tabs(indent)})";
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
            return $"exists {EmitExpr(fn.Arguments[0], engine, indent)}";

        var fnName = fn.IsKeywordFunction ? fn.Name.ToLowerInvariant() : fn.Name;
        var overStr = fn.OverClause != null
            ? $" over ({EmitExpr(fn.OverClause, engine, indent)})"
            : "";

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
                string cStr = EmitExpr(c, engine, indent + 1);
                return idx == 0 ? cStr : $"\n{Tabs(indent + 2)}{cStr}";
            });
            string condStr = string.Join($" AND", conditions);
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
                string prefix = cond.LogicalOp != null ? $"{cond.LogicalOp} " : "";
                string expr   = EmitExpr(cond.Expression, engine, indent);
                string cmt    = cond.TrailingComment != null ? $" {cond.TrailingComment}" : "";
                lines.Add($"{Tabs(indent)}{prefix}{expr}{cmt}");
            }
            else if (c is OrGroupNode og)
            {
                lines.Add(EmitOrGroup(og, engine, indent));
            }
            else
            {
                lines.Add($"{Tabs(indent)}{EmitExpr(c, engine, indent)}");
            }
        }
        return string.Join("\n", lines);
    }

    private static string EmitOrGroup(OrGroupNode og, FormatterEngine engine, int indent)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"{Tabs(indent)}(\n");
        foreach (var c in og.Conditions)
        {
            if (c is ConditionNode cond)
            {
                string prefix = cond.LogicalOp != null ? $"{cond.LogicalOp} " : "";
                sb.AppendLine($"{Tabs(indent + 1)}{prefix}{EmitExpr(cond.Expression, engine, indent + 1)}");
            }
        }
        sb.Append($"{Tabs(indent)})");
        return sb.ToString();
    }

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

        if (join.Conditions.Count == 0) return sb.ToString();

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

    // ─── OR-group conditions (rule 2.6) ─────────────────────────────────────

    public static bool HasOrConditions(List<AstNode> conditions)
        => conditions.OfType<ConditionNode>().Any(c => c.LogicalOp == "or");

    private static List<List<ConditionNode>> SplitByOr(List<AstNode> conditions)
    {
        var groups  = new List<List<ConditionNode>>();
        var current = new List<ConditionNode>();
        foreach (var node in conditions)
        {
            if (node is ConditionNode cn)
            {
                if (cn.LogicalOp == "or" && current.Count > 0)
                {
                    groups.Add(current);
                    current = new List<ConditionNode>();
                    current.Add(new ConditionNode { Expression = cn.Expression });
                }
                else current.Add(cn);
            }
        }
        if (current.Count > 0) groups.Add(current);
        return groups;
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
        sb.Append("(\n");
        for (int i = 0; i < cg.Conditions.Count; i++)
        {
            if (cg.Conditions[i] is ConditionNode cn)
            {
                string prefix = cn.LogicalOp != null ? $"{cn.LogicalOp} " : "";
                sb.Append($"{t1}{prefix}{EmitExpr(cn.Expression, engine, indent + 1)}\n");
            }
        }
        sb.Append($"{t0})");
        return sb.ToString();
    }

    public static string EmitOrGroupConditions(List<AstNode> conditions, FormatterEngine engine, int indent)
    {
        var groups    = SplitByOr(conditions);
        bool allAtomic = groups.All(g => g.Count == 1);
        var sb  = new System.Text.StringBuilder();
        string t0 = Tabs(indent);
        string t1 = Tabs(indent + 1);
        string t2 = Tabs(indent + 2);

        sb.Append($"{t0}(\n");
        for (int i = 0; i < groups.Count; i++)
        {
            var group   = groups[i];
            bool isFirst = i == 0;

            if (allAtomic)
            {
                string prefix = isFirst ? "" : "or ";
                sb.Append($"{t1}{prefix}{EmitExpr(group[0].Expression, engine, indent + 1)}\n");
            }
            else if (group.Count == 1)
            {
                // 'or condition' on a single line (consistent with EmitConditionGroup)
                string prefix = isFirst ? "" : "or ";
                sb.Append($"{t1}{prefix}{EmitExpr(group[0].Expression, engine, indent + 1)}\n");
            }
            else
            {
                if (!isFirst) sb.Append($"{t1}or\n");
                sb.Append($"{t1}(\n");
                for (int j = 0; j < group.Count; j++)
                {
                    string andPrefix = j == 0 ? "" : "and ";
                    sb.Append($"{t2}{andPrefix}{EmitExpr(group[j].Expression, engine, indent + 2)}\n");
                }
                sb.Append($"{t1})\n");
            }
        }
        sb.Append($"{t0})");
        return sb.ToString();
    }

}

}
