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
            var ifExists = drop.IfExists ? " if exists" : "";
            return $"{tabs}drop table{ifExists} {drop.TableName}";
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
