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
            // The previous column's /* */ comment had no line break after it: this column carries
            // on that line, the way it was written.
            bool continuesLine = i > 0 && !sel.Columns[i - 1].TrailingBreakAfter
                                       && col.LeadingComments.Count == 0;
            if (inlineFirst && i == 0)
                sb.Append($" {blockLead}{colStr}{alias}{comma}{comment}");
            else if (continuesLine)
                sb.Append($" {blockLead}{colStr}{alias}{comma}{comment}");
            else
                sb.Append($"\n{lineLead}{tabs}\t{blockLead}{colStr}{alias}{comma}{comment}");
        }

        // Comments written between the column list and INTO/FROM stay there.
        foreach (var c in sel.PreFromComments)
            sb.Append($"\n{tabs}{c}");

        // ── INTO (SELECT ... INTO #tbl) ───────────────────────────────────────
        if (sel.IntoTable != null)
            sb.Append($"\n{tabs}into {sel.IntoTable}");

        // ── FROM ──────────────────────────────────────────────────────────────
        if (sel.FromClauses.Count > 0)
        {
            sb.Append($"\n{tabs}from");
            // A FROM can list several sources ("from a as t1, b as t2"); the joins that follow a
            // source belong to it. The first source stays on the from line, each further one goes
            // on its own line one tab in, and the comma closes the source before it — after its
            // joins, and before its trailing -- comment so the comma is never commented out.
            var sources = new List<(StringBuilder Body, string? Comment)>();
            foreach (var clause in sel.FromClauses)
            {
                if (clause is TableRefNode tref)
                    sources.Add((new StringBuilder(RuleHelpers.EmitTableRef(tref, engine, indent)),
                                 tref.TrailingComment));
                else if (clause is JoinNode join)
                {
                    if (sources.Count == 0) sources.Add((new StringBuilder(), null));
                    // A join lands after the source's comment, on its own line, so the comment
                    // stays where it was written.
                    var (body, comment) = sources[^1];
                    if (comment != null)
                    {
                        body.Append($"{RuleHelpers.Tabs(2)}{comment}");
                        sources[^1] = (body, null);
                    }
                    body.Append(RuleHelpers.FormatJoin(join, engine, indent));
                }
            }

            for (int i = 0; i < sources.Count; i++)
            {
                var (body, comment) = sources[i];
                var text = body.ToString()
                         + (i < sources.Count - 1 ? "," : "")
                         + (comment != null ? $"{RuleHelpers.Tabs(2)}{comment}" : "");
                sb.Append(i == 0 ? $" {text}" : $"\n{tabs}\t{text}");
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
        if (sel.Columns[0].LeadingComments.Any(c => !c.Text.StartsWith("/*"))) return false;
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
