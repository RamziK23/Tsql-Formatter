using System.Collections.Generic;
using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

/// <summary>
/// INSERT INTO table [(col1, col2, ...)]
/// VALUES (v1, v2), (v3, v4)
/// — or —
/// INSERT INTO table [(cols)]
/// SELECT ...
/// </summary>
public sealed class InsertRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is InsertNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var ins  = (InsertNode)node;
        var tabs = RuleHelpers.Tabs(indent);
        var sb   = new System.Text.StringBuilder();

        sb.Append(RuleHelpers.EmitCteHeader(ins, engine, indent));
        sb.Append($"{tabs}insert into {RuleHelpers.EmitTableRef(ins.Table, engine, indent)}");

        // Column list: opening paren after a space (like create table), each column on its own
        // line (+1 tab), closing paren on its own line aligned with insert.
        if (ins.Columns.Count > 0)
        {
            var t1 = RuleHelpers.Tabs(indent + 1);
            sb.Append(" (\n");
            for (int i = 0; i < ins.Columns.Count; i++)
            {
                sb.Append($"{t1}{RuleHelpers.EmitExpr(ins.Columns[i], engine, indent + 1)}");
                if (i < ins.Columns.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append($"{tabs})");
            if (ins.ColumnsComment != null)
                RuleHelpers.AppendTrailing(sb, ins.ColumnsComment);
        }

        if (ins.Source is ValuesNode vn)
        {
            sb.Append($"\n{tabs}values");
            for (int r = 0; r < vn.Rows.Count; r++)
            {
                var vals = string.Join(", ", vn.Rows[r].Select(v => RuleHelpers.EmitExpr(v, engine, indent)));
                sb.Append($"\n{tabs}\t({vals})");
                if (r < vn.Rows.Count - 1) sb.Append(",");
                // The row's comment follows its comma, never precedes it.
                if (r < vn.RowComments.Count && vn.RowComments[r] != null)
                    RuleHelpers.AppendTrailing(sb, vn.RowComments[r]!);
            }
        }
        else if (ins.Source != null)
        {
            sb.Append("\n");
            sb.Append(engine.Format(ins.Source, indent));
        }

        return sb.ToString();
    }
}

}
