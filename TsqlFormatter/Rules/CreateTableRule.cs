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
        sb.Append($"{tabs}create table {ct.TableName} (\n");

        for (int i = 0; i < ct.Columns.Count; i++)
        {
            var col   = ct.Columns[i];
            var comma = i < ct.Columns.Count - 1 ? "," : "";
            // Column name preserves original case; definition kept as-is
            sb.Append($"{tabs}\t{col.Name} {col.Definition}{comma}\n");
        }

        sb.Append($"{tabs})");
        return sb.ToString();
    }
}

}
