using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

public sealed class SelectRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is SelectStatementNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var sel = (SelectStatementNode)node;
        var sb  = new System.Text.StringBuilder();
        var tabs = RuleHelpers.Tabs(indent);

        // Comments that preceded the SELECT keyword, each on its own line above the header.
        foreach (var c in sel.LeadingComments)
            sb.Append($"{tabs}{c}\n");

        // ── CTEs ──────────────────────────────────────────────────────────────
        if (sel.CteDefinitions.Count > 0)
        {
            sb.Append($"{tabs}with ");
            for (int i = 0; i < sel.CteDefinitions.Count; i++)
            {
                var cte = (CteDefinitionNode)sel.CteDefinitions[i];
                sb.Append($"{cte.Name.Value} as (\n");
                sb.Append(engine.Format(cte.Body, indent + 1));
                sb.Append($"\n{tabs})");
                if (i < sel.CteDefinitions.Count - 1) sb.Append($",\n{tabs}");
            }
            sb.Append("\n");
        }

        // ── SELECT header ─────────────────────────────────────────────────────
        var header = new System.Text.StringBuilder($"{tabs}select");
        if (sel.IsDistinct) header.Append(" distinct");
        if (sel.TopExpr != null) header.Append($" top {sel.TopExpr}");

        // ── Columns ───────────────────────────────────────────────────────────
        // Every column goes on its own indented line (+1 tab), regardless of count — except in
        // an assignment SELECT ("select @a = 1, @b = 2"), where the first assignment stays on
        // the select line like a declare.
        bool inlineFirst = IsVariableAssignment(sel);
        sb.Append(header);
        for (int i = 0; i < sel.Columns.Count; i++)
        {
            var col     = sel.Columns[i];
            var colStr  = RuleHelpers.EmitExpr(col.Expression, engine, indent + 1);
            var alias   = col.Alias != null ? $" as {col.Alias.Value}" : "";
            var comma   = i < sel.Columns.Count - 1 ? "," : "";
            // Rule 4.1.2: comma goes BEFORE the trailing comment. A -- comment is offset by
            // two tabs; a /* */ block comment glues directly (transparent).
            var comment = col.TrailingComment != null ? RuleHelpers.TrailingCommentSuffix(col.TrailingComment) : "";
            // Leading comments: -- line comments each on their own line above the column;
            // /* */ block comments inline before the expression.
            var (lineLead, blockLead) = RuleHelpers.SplitLeadingComments(col.LeadingComments, $"{tabs}\t");
            if (inlineFirst && i == 0)
                sb.Append($" {blockLead}{colStr}{alias}{comma}{comment}");
            else
                sb.Append($"\n{lineLead}{tabs}\t{blockLead}{colStr}{alias}{comma}{comment}");
        }

        // ── INTO (SELECT ... INTO #tbl) ───────────────────────────────────────
        if (sel.IntoTable != null)
            sb.Append($"\n{tabs}into {sel.IntoTable}");

        // ── FROM ──────────────────────────────────────────────────────────────
        if (sel.FromClauses.Count > 0)
        {
            sb.Append($"\n{tabs}from");
            foreach (var clause in sel.FromClauses)
            {
                if (clause is JoinNode join)
                    sb.Append(RuleHelpers.FormatJoin(join, engine, indent));
                else if (clause is TableRefNode tref)
                {
                    sb.Append($" {RuleHelpers.EmitTableRef(tref, engine, indent)}");
                    // A same-line -- comment stays on the FROM line.
                    if (tref.TrailingComment != null)
                        sb.Append($"{RuleHelpers.Tabs(2)}{tref.TrailingComment}");
                }
            }
        }

        // Standalone comments that trailed the FROM/JOIN block (e.g. before WHERE).
        foreach (var c in sel.PostFromComments)
            sb.Append($"\n{tabs}{c}");

        // ── WHERE ─────────────────────────────────────────────────────────────
        // Every condition on its own indented line, even a single one.
        if (sel.WhereConditions.Count > 0)
        {
            sb.Append($"\n{tabs}where");
            sb.Append($"\n{RuleHelpers.EmitConditions(sel.WhereConditions, engine, indent + 1)}");
        }

        // ── GROUP BY ──────────────────────────────────────────────────────────
        // Every column on its own indented line, regardless of count.
        if (sel.GroupByColumns.Count > 0)
        {
            sb.Append($"\n{tabs}group by");
            for (int gi = 0; gi < sel.GroupByColumns.Count; gi++)
            {
                var (lineLead, blockLead, item) = SplitItemComments(sel.GroupByColumns[gi], $"{tabs}\t");
                sb.Append($"\n{lineLead}{tabs}\t{blockLead}{RuleHelpers.EmitExpr(item, engine, indent + 1)}");
                if (gi < sel.GroupByColumns.Count - 1) sb.Append(",");
            }
        }

        // ── HAVING ────────────────────────────────────────────────────────────
        // Like WHERE: the keyword on its own line, each condition on its own line (+1 tab).
        if (sel.HavingConditions.Count > 0)
        {
            sb.Append($"\n{tabs}having");
            sb.Append($"\n{RuleHelpers.EmitConditions(sel.HavingConditions, engine, indent + 1)}");
        }

        // ── ORDER BY ─────────────────────────────────────────────────────────
        // Every item on its own indented line, regardless of count.
        if (sel.OrderByColumns.Count > 0)
        {
            sb.Append($"\n{tabs}order by");
            for (int oi = 0; oi < sel.OrderByColumns.Count; oi++)
            {
                var (lineLead, blockLead, item) = SplitItemComments(sel.OrderByColumns[oi], $"{tabs}\t");
                sb.Append($"\n{lineLead}{tabs}\t{blockLead}{RuleHelpers.EmitExpr(item, engine, indent + 1)}");
                if (oi < sel.OrderByColumns.Count - 1) sb.Append(",");
            }
        }

        // ── OPTION (...) query hint — trailing line, never a WHERE condition ──
        if (sel.OptionTokens != null)
            sb.Append($"\n{tabs}option({RuleHelpers.EmitRawTokens(sel.OptionTokens)})");

        return sb.ToString();
    }

    /// <summary>
    /// Peels the comments off a GROUP BY / ORDER BY item: -- comments render on their own line(s)
    /// above it (prefixed by <paramref name="linePrefix"/>), /* */ comments glue inline in front of
    /// the expression — the same layout the select column list uses.
    /// </summary>
    private static (string lineLead, string blockLead, AstNode item) SplitItemComments(
        AstNode item, string linePrefix)
    {
        if (item is not ListItemNode li) return ("", "", item);
        var (lineLead, blockLead) = RuleHelpers.SplitLeadingComments(li.LeadingComments, linePrefix);
        return (lineLead, blockLead, li.Expression);
    }

    /// <summary>
    /// True when the SELECT only assigns values to variables ("select @a = 'x', @i = 67"), i.e.
    /// it fills variables instead of producing a result set. Such a SELECT is laid out like a
    /// DECLARE: the first assignment on the select line, the rest one tab in.
    /// A -- comment above the first column keeps the regular layout (it needs its own line).
    /// </summary>
    private static bool IsVariableAssignment(SelectStatementNode sel)
    {
        if (sel.Columns.Count == 0) return false;
        if (sel.Columns[0].LeadingComments.Any(c => !c.StartsWith("/*"))) return false;
        return sel.Columns.All(c => c.Alias == null && IsAssignmentToVariable(c.Expression));
    }

    private static bool IsAssignmentToVariable(AstNode expr) =>
        expr is BinaryExprNode b
        && b.Op.Type == TokenType.Equals
        && b.Left is ColumnRefNode target
        && target.Parts.Count > 0
        && target.Parts[0].Type == TokenType.Variable;

}

}
