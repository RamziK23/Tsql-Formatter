using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

/// <summary>
/// Rule `merge`. Every keyword of the statement stands at the statement's own indent — merge,
/// using, when, then, set, values — and so does the closing paren of any list. Only list
/// contents and the ON / branch conditions are indented, one tab:
///
///     merge core_baseorganizationinfo as target
///     using (
///         select
///             c.id
///         from #src as c
///     ) as source (
///         id
///     )
///         on target.id = source.id
///         and target.idd = source.idd
///     when matched
///         and (
///             …
///         )
///     then update
///     set
///         target.uuid = source.uuid
///     when not matched		-- комментарий
///     then insert (
///         id
///     )
///     values (
///         source.id
///     )
///     when not matched by source
///     then delete;
///
/// THEN shares its line with the action word; a comment written on that line closes it.
/// </summary>
public sealed class MergeRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is MergeNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var m    = (MergeNode)node;
        var tabs = RuleHelpers.Tabs(indent);
        var t1   = RuleHelpers.Tabs(indent + 1);
        var sb   = new StringBuilder();

        var into = m.HasInto ? "into " : "";
        sb.Append($"{tabs}merge {into}{RuleHelpers.EmitTableRef(m.Target, engine, indent)}");
        if (m.TargetComment != null) RuleHelpers.AppendTrailing(sb, m.TargetComment);

        // "using <source>", with the derived table's column list — when the author wrote one —
        // laid out like every other list: one name per line, the paren closing at the keyword's
        // own indent.
        sb.Append($"\n{tabs}using {RuleHelpers.EmitTableRef(m.Source, engine, indent, withColumnAliases: false)}");
        AppendNameList(sb, m.Source.ColumnAliases.Select(c => c.Value).ToList(), tabs, t1);
        if (m.SourceComment != null) RuleHelpers.AppendTrailing(sb, m.SourceComment);

        // ON: the first condition on the "on" line, every further one under it at the same
        // indent — the JOIN layout.
        for (int i = 0; i < m.OnConditions.Count; i++)
        {
            var c    = m.OnConditions[i] as ConditionNode;
            var text = RuleHelpers.EmitExpr(c?.Expression ?? m.OnConditions[i], engine, indent + 1);
            foreach (var lc in c?.LeadingComments ?? new System.Collections.Generic.List<string>())
                sb.Append($"\n{t1}{lc}");
            var line = i == 0 ? $"{t1}on {text}" : $"{t1}{c?.LogicalOp ?? "and"} {text}";
            if (c?.TrailingComment != null) line = RuleHelpers.AppendTrailing(line, c.TrailingComment);
            sb.Append($"\n{line}");
        }

        foreach (var w in m.Whens)
        {
            foreach (var lc in w.LeadingComments) sb.Append($"\n{tabs}{lc}");
            var whenLine = $"{tabs}when {w.Kind}";
            if (w.KindComment != null) whenLine = RuleHelpers.AppendTrailing(whenLine, w.KindComment);
            sb.Append($"\n{whenLine}");
            // Every extra condition takes a line of its own, one tab in — the same shape the ON
            // list has, so a group in a branch reads like a group anywhere else.
            foreach (var cond in w.ExtraConditions)
            {
                var c    = cond as ConditionNode;
                var text = RuleHelpers.EmitExpr(c?.Expression ?? cond, engine, indent + 1);
                foreach (var lc in c?.LeadingComments ?? new System.Collections.Generic.List<string>())
                    sb.Append($"\n{t1}{lc}");
                var line = $"{t1}{c?.LogicalOp ?? "and"} {text}";
                if (c?.TrailingComment != null) line = RuleHelpers.AppendTrailing(line, c.TrailingComment);
                sb.Append($"\n{line}");
            }
            foreach (var lc in w.ThenLeadingComments) sb.Append($"\n{tabs}{lc}");

            // THEN and the action word share a line; a comment written there closes it.
            var head = $"{tabs}then {ActionHead(w)}";
            if (w.ThenComment != null) head = RuleHelpers.AppendTrailing(head, w.ThenComment);
            sb.Append($"\n{head}");
            AppendActionBody(sb, w, engine, indent);
        }

        if (m.OutputTokens != null)
        {
            sb.Append($"\n{tabs}{RuleHelpers.EmitRawTokens(m.OutputTokens)}");
            if (m.OutputComment != null) RuleHelpers.AppendTrailing(sb, m.OutputComment);
        }
        if (m.OutputIntoTokens != null)
        {
            sb.Append($"\n{tabs}{RuleHelpers.EmitRawTokens(m.OutputIntoTokens)}");
            if (m.OutputIntoComment != null) RuleHelpers.AppendTrailing(sb, m.OutputIntoComment);
        }

        return sb.ToString();
    }

    /// <summary>What follows THEN on its line: the action word, plus the '(' that opens an
    /// INSERT column list.</summary>
    private static string ActionHead(MergeWhenNode w) => w.Action switch
    {
        "update" => "update",
        "insert" => w.DefaultValues ? "insert default values"
                    : w.InsertColumns.Count > 0 ? "insert (" : "insert",
        _        => "delete",
    };

    private static void AppendActionBody(StringBuilder sb, MergeWhenNode w, FormatterEngine engine, int indent)
    {
        var tabs = RuleHelpers.Tabs(indent);
        var t1   = RuleHelpers.Tabs(indent + 1);

        switch (w.Action)
        {
            case "update":
                // "set" opens the list on a line of its own; each assignment one tab in, the
                // comma before its comment.
                sb.Append($"\n{tabs}set");
                for (int i = 0; i < w.Assignments.Count; i++)
                {
                    var a      = w.Assignments[i];
                    var target = RuleHelpers.EmitExpr(a.Target, engine, indent + 1);
                    var value  = RuleHelpers.EmitExpr(a.Value,  engine, indent + 1);
                    var line   = $"{t1}{target} = {value}";
                    if (i < w.Assignments.Count - 1) line += ",";
                    if (a.TrailingComment != null) line = RuleHelpers.AppendTrailing(line, a.TrailingComment);
                    sb.Append($"\n{line}");
                }
                break;

            case "insert":
                // The '(' of the column list is already on the THEN line; the names follow one
                // tab in and the paren closes at the statement's indent.
                if (w.InsertColumns.Count > 0)
                {
                    for (int i = 0; i < w.InsertColumns.Count; i++)
                    {
                        var comma = i < w.InsertColumns.Count - 1 ? "," : "";
                        sb.Append($"\n{t1}{RuleHelpers.EmitExpr(w.InsertColumns[i], engine, indent + 1)}{comma}");
                    }
                    sb.Append($"\n{tabs})");
                }
                if (w.InsertValues is ValuesNode vn)
                {
                    for (int r = 0; r < vn.Rows.Count; r++)
                    {
                        sb.Append($"\n{tabs}values (");
                        for (int i = 0; i < vn.Rows[r].Count; i++)
                        {
                            var comma = i < vn.Rows[r].Count - 1 ? "," : "";
                            sb.Append($"\n{t1}{RuleHelpers.EmitExpr(vn.Rows[r][i], engine, indent + 1)}{comma}");
                        }
                        var close = $"{tabs})";
                        if (r < vn.Rows.Count - 1) close += ",";
                        if (r < vn.RowComments.Count && vn.RowComments[r] != null)
                            close = RuleHelpers.AppendTrailing(close, vn.RowComments[r]!);
                        sb.Append($"\n{close}");
                    }
                }
                break;
        }
    }

    /// <summary>Renders a parenthesised list of names opened on the line already in
    /// <paramref name="sb"/>: one name per line, the closing paren back at that line's indent.
    /// Does nothing when the list is empty.</summary>
    private static void AppendNameList(StringBuilder sb, System.Collections.Generic.List<string> names,
                                       string tabs, string t1)
    {
        if (names.Count == 0) return;
        sb.Append(" (");
        for (int i = 0; i < names.Count; i++)
            sb.Append($"\n{t1}{names[i]}{(i < names.Count - 1 ? "," : "")}");
        sb.Append($"\n{tabs})");
    }
}

}
