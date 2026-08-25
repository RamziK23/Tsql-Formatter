using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

/// <summary>
/// Rule 2.14: CREATE TABLE and DROP TABLE formatting.
///
/// create table #name (
///     col1 varchar(255),
///     col2 int
/// )
///
/// drop table if exists #name
/// </summary>
public sealed class CreateTableRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is CreateTableNode || node is DropTableNode;

    public string Format(AstNode node, FormatterEngine engine, int indent)
    {
        var tabs = RuleHelpers.Tabs(indent);

        if (node is DropTableNode drop)
        {
            // The statement is one line, so a comment written inside it keeps its place there —
            // a -- comment as a /* */ one, since everything after it must stay code.
            static string Cmts(System.Collections.Generic.List<string> cs) =>
                string.Concat(cs.Select(c => RuleHelpers.AsInlineComment(c) + " "));
            var line = new StringBuilder($"{tabs}drop ");
            line.Append(Cmts(drop.KindComments)).Append("table ");
            line.Append(Cmts(drop.ExistsComments));
            if (drop.IfExists) line.Append("if exists ");
            line.Append(Cmts(drop.NameComments)).Append(drop.TableName);
            return line.ToString();
        }

        var ct = (CreateTableNode)node;
        var sb = new StringBuilder();
        // Opening paren stays on the same line as the table name (matches compact source).
        // A comment written after the name keeps its place, in front of the column list.
        var nameComment = ct.NameComment != null ? $"{ct.NameComment} " : "";
        sb.Append($"{tabs}create table {ct.TableName} {nameComment}(\n");

        sb.Append(RuleHelpers.EmitColumnDefs(ct.Columns, indent));
        foreach (var c in ct.CloseComments) sb.Append($"{tabs}\t{c}\n");
        sb.Append($"{tabs})");
        return sb.ToString();
    }
}

}
