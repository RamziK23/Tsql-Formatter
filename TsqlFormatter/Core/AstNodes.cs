using System.Linq;
using System.Collections.Generic;

namespace TsqlFormatter.Core
{

// ─── Base ────────────────────────────────────────────────────────────────────

/// <summary>Comment text helpers shared by the parser and the emitters.</summary>
public static class CommentText
{
    /// <summary>
    /// Renders a comment that has to keep CODE BEHIND IT on the same line — between "left join"
    /// and its table, between a table name and its column list. A -- comment cannot live there
    /// (everything after it would be commented out), so it is rewritten as a /* */ comment. Text
    /// that already contains "*/" is left as a -- comment: rewriting it would end the comment
    /// early and change what the script means.
    /// </summary>
    public static string AsInline(string comment)
    {
        if (comment.StartsWith("/*")) return comment;
        var text = comment.TrimStart('-').Trim();
        return text.Contains("*/") ? comment : $"/*{text}*/";
    }
}

public abstract class AstNode
{
    /// <summary>
    /// A -- line comment that trailed this statement on the SAME line as its last token.
    /// Kept here so the comment stays attached to its original line instead of migrating
    /// to the following statement. Rendered by FormatScript on the statement's last line.
    /// </summary>
    public string? StatementTrailingComment { get; set; }

    /// <summary>
    /// CTEs declared in front of this statement: "with a as (…), b as (…) select|insert|update|
    /// delete …". Held on the base node because every one of those statements can carry them.
    /// </summary>
    public List<AstNode> CteDefinitions { get; } = new();

    /// <summary>
    /// The author terminated this statement with ';' on its last line. Re-emitted glued to the
    /// statement's last token, before any trailing comment.
    /// </summary>
    public bool TrailingSemicolon { get; set; }

    /// <summary>
    /// Comments that turned up where an operand was expected ("where x = --note\n 1"). No
    /// expression can hold them, so they are lifted to the statement and rendered on their own
    /// line(s) above it — moved, but never lost, and never left where they would comment out code.
    /// </summary>
    public List<string> HoistedComments { get; } = new();

    /// <summary>
    /// True when the source had a blank line (2+ newlines) before this statement.
    /// FormatScript uses it to preserve the original blank-line structure between
    /// statements instead of always forcing one.
    /// </summary>
    public bool BlankLineBefore { get; set; }

    /// <summary>
    /// True when the source had a ';' right before this statement AND the statement starts
    /// with WITH (a CTE). T-SQL requires the previous statement to be terminated with a
    /// semicolon before WITH, so dropping it would break the script — it is re-emitted
    /// as a ";with ..." prefix.
    /// </summary>
    public bool LeadingSemicolon { get; set; }
}

// ─── Script root ─────────────────────────────────────────────────────────────

public sealed class ScriptNode : AstNode
{
    public List<AstNode> Statements { get; } = new();
    /// <summary>True when the source started with ; before the first statement (e.g. ;WITH).</summary>
    public bool HasLeadingSemicolon { get; set; }
    /// <summary>
    /// Source text of a construct that runs off the end of the input (a selection cut mid-statement,
    /// e.g. "… from openquery(" with the rest missing). It is emitted verbatim after the statements
    /// that did parse, so an unfinished tail costs only its own formatting — not the whole script's.
    /// </summary>
    public string? UnparsedTail { get; set; }
    /// <summary>True when a blank line separated the unparsed tail from the previous statement.</summary>
    public bool UnparsedTailBlankBefore { get; set; }
    /// <summary>
    /// True when the tail is the leftover of a statement whose earlier part was formatted: it
    /// starts with the original whitespace (or none) that stood between the two, so it is appended
    /// as-is — "… from openquery(" keeps the unfinished call on the line it was written on.
    /// </summary>
    public bool UnparsedTailGlued { get; set; }
}

// ─── Statements ──────────────────────────────────────────────────────────────

/// <summary>Single variable inside a DECLARE statement.</summary>
public sealed class DeclareVarNode : AstNode
{
    public Token Variable { get; init; } = null!;
    public List<Token> DataType { get; } = new();
    public AstNode? Initializer { get; init; }
    public string? TrailingComment { get; set; }
    /// <summary>Columns of a table variable ("declare @t table ( … )"), laid out like CREATE TABLE.</summary>
    public List<ColumnDefNode>? TableColumns { get; set; }
}

/// <summary>DECLARE statement — one or more comma-separated variables.</summary>
public sealed class DeclareNode : AstNode
{
    public List<DeclareVarNode> Variables { get; } = new();
}

public sealed class SelectStatementNode : AstNode
{
    public string? TopExpr { get; init; }
    public bool IsDistinct { get; init; }
    /// <summary>Target of SELECT ... INTO #tbl (table name), or null.</summary>
    public string? IntoTable { get; set; }
    /// <summary>Comment written on the INTO line, after the target.</summary>
    public string? IntoComment { get; set; }
    /// <summary>True when the author wrote that comment glued to the table name
    /// ("into #wru/*note*/"); it then stays glued.</summary>
    public bool IntoCommentGlued { get; set; }
    public List<SelectColumnNode> Columns { get; } = new();
    public List<AstNode> FromClauses { get; } = new();
    public List<AstNode> WhereConditions { get; } = new();
    public List<AstNode> GroupByColumns { get; } = new();
    /// <summary>HAVING conditions: first item without operator, the rest with and/or.</summary>
    public List<AstNode> HavingConditions { get; } = new();
    public List<AstNode> OrderByColumns { get; } = new();
    /// <summary>A -- comment written on the SELECT line itself; it stays on that line.</summary>
    public string? HeaderComment { get; set; }
    /// <summary>Standalone comments between the column list and INTO/FROM — a commented-out
    /// clause, usually. Rendered on their own line(s) there.</summary>
    public List<string> PreFromComments { get; } = new();
    /// <summary>Standalone comments that trailed the FROM/JOIN block (before WHERE/GROUP/etc).</summary>
    public List<string> PostFromComments { get; } = new();
    /// <summary>Comments that preceded the SELECT keyword (e.g. inside "( --note\n select ...)").
    /// Rendered on their own line(s) above the select header.</summary>
    public List<string> LeadingComments { get; } = new();
    /// <summary>Query hint clause: OPTION (...). Tokens inside the parens (excluding them),
    /// or null when absent. Rendered as a trailing "option(...)" line, never as a condition.</summary>
    public List<Token>? OptionTokens { get; set; }
}

/// <summary>
/// UNION / EXCEPT / INTERSECT between two SELECT statements.
/// </summary>
public sealed class SetOperationNode : AstNode
{
    /// <summary>UNION, UNION ALL, EXCEPT, INTERSECT</summary>
    public string Operator { get; init; } = "UNION";
    public AstNode Left { get; init; } = null!;
    public AstNode Right { get; init; } = null!;
}

// ─── INSERT ──────────────────────────────────────────────────────────────────

public sealed class InsertNode : AstNode
{
    public TableRefNode Table { get; init; } = null!;
    /// <summary>Explicit column list after INSERT INTO t (...), may be empty.</summary>
    public List<AstNode> Columns { get; } = new();
    /// <summary>Either ValuesNode or SelectStatementNode / SetOperationNode.</summary>
    public AstNode? Source { get; init; }
    /// <summary>A -- comment on the closing paren's line: "insert into t (…)  -- note".</summary>
    public string? ColumnsComment { get; set; }
}

public sealed class ValuesNode : AstNode
{
    /// <summary>One list of expressions per VALUES row.</summary>
    public List<List<AstNode>> Rows { get; } = new();
    /// <summary>Trailing comment per row (same length as <see cref="Rows"/>, null where none).
    /// The row's comma is emitted before it.</summary>
    public List<string?> RowComments { get; } = new();
}

// ─── UPDATE ──────────────────────────────────────────────────────────────────

public sealed class UpdateNode : AstNode
{
    public TableRefNode Table { get; init; } = null!;
    public List<AssignmentNode> Assignments { get; } = new();
    /// <summary>FROM clauses (table refs + joins), same shape as SelectStatementNode.</summary>
    public List<AstNode> FromClauses { get; } = new();
    public List<AstNode> WhereConditions { get; } = new();
    /// <summary>Comment written on the "update …" line itself.</summary>
    public string? TargetComment { get; set; }
    /// <summary>Standalone comments between the table and SET.</summary>
    public List<string> PreSetComments { get; } = new();
    /// <summary>Standalone comments between the assignments and FROM.</summary>
    public List<string> PreFromComments { get; } = new();
    /// <summary>Standalone comments between FROM/JOIN and WHERE.</summary>
    public List<string> PreWhereComments { get; } = new();
}

public sealed class AssignmentNode : AstNode
{
    public AstNode Target { get; init; } = null!;   // column ref
    public AstNode Value  { get; init; } = null!;
    /// <summary>Comment written after this assignment; the comma is emitted before it.</summary>
    public string? TrailingComment { get; set; }
}

/// <summary>
/// MERGE target USING source ON … WHEN … THEN … [OUTPUT …].
/// </summary>
public sealed class MergeNode : AstNode
{
    public TableRefNode Target { get; init; } = null!;
    /// <summary>The optional INTO the author wrote ("merge into t"); kept as written.</summary>
    public bool HasInto { get; init; }
    /// <summary>Comment written on the "merge …" line.</summary>
    public string? TargetComment { get; set; }
    public TableRefNode Source { get; init; } = null!;
    /// <summary>Comment written on the source's closing line ("…) as src -- note").</summary>
    public string? SourceComment { get; set; }
    public List<AstNode> OnConditions { get; } = new();
    public List<MergeWhenNode> Whens { get; } = new();
    /// <summary>OUTPUT … kept verbatim on its own line, or null.</summary>
    public List<Token>? OutputTokens { get; set; }
    public string? OutputComment { get; set; }
    /// <summary>The INTO … target of an OUTPUT clause, on its own line.</summary>
    public List<Token>? OutputIntoTokens { get; set; }
    public string? OutputIntoComment { get; set; }
}

/// <summary>One WHEN … THEN &lt;action&gt; branch of a MERGE.</summary>
public sealed class MergeWhenNode : AstNode
{
    /// <summary>"matched", "not matched by target" or "not matched by source".</summary>
    public string Kind { get; init; } = "matched";
    /// <summary>Extra conditions after AND; the first stays on the when line.</summary>
    public List<AstNode> ExtraConditions { get; } = new();
    /// <summary>Comment written between the conditions and THEN.</summary>
    public string? ConditionComment { get; set; }
    /// <summary>Comment written on the THEN line.</summary>
    public string? ThenComment { get; set; }
    /// <summary>"update" (with <see cref="Assignments"/>), "insert" or "delete".</summary>
    public string Action { get; set; } = "delete";
    public List<AssignmentNode> Assignments { get; } = new();
    /// <summary>INSERT column list, may be empty.</summary>
    public List<AstNode> InsertColumns { get; } = new();
    /// <summary>INSERT source: a ValuesNode, or null for "insert default values".</summary>
    public AstNode? InsertValues { get; set; }
    /// <summary>True for "insert default values".</summary>
    public bool DefaultValues { get; set; }
}

// ─── DELETE ──────────────────────────────────────────────────────────────────

public sealed class DeleteNode : AstNode
{
    public TableRefNode? TargetAlias { get; init; }   // DELETE alias FROM …
    public TableRefNode? Table { get; init; }          // DELETE FROM tbl
    public List<AstNode> FromClauses { get; } = new();
    public List<AstNode> WhereConditions { get; } = new();
    /// <summary>Comment written on the "delete …" line itself.</summary>
    public string? TargetComment { get; set; }
    /// <summary>Standalone comments between the target and FROM.</summary>
    public List<string> PreFromComments { get; } = new();
    /// <summary>Standalone comments between FROM/JOIN and WHERE.</summary>
    public List<string> PreWhereComments { get; } = new();
}

// ─── SELECT columns ──────────────────────────────────────────────────────────

/// <summary>
/// A comment written before something, together with whether the source had a line break between
/// the comment and what follows. A /* */ comment is glued in front of the expression when there
/// was none and given its own line when there was — the layout the author chose is kept either way.
/// </summary>
public sealed class LeadingComment
{
    public string Text { get; init; } = "";
    public bool BreakAfter { get; init; } = true;
}

public sealed class SelectColumnNode : AstNode
{
    public AstNode Expression { get; init; } = null!;
    public Token? Alias { get; init; }
    public bool CommaLeading { get; init; }
    public string? TrailingComment { get; set; }
    /// <summary>False when no line break stood between this column's trailing /* */ comment and
    /// the next column, which then continues on the comment's closing line.</summary>
    public bool TrailingBreakAfter { get; set; } = true;
    /// <summary>Comments before the column. A /* */ block comment renders inline before the
    /// expression (/* note */ expr) or on its own line, following the source; a -- line comment
    /// always renders on its own line above the column (inline it would comment the column out).</summary>
    public List<LeadingComment> LeadingComments { get; } = new();
    /// <summary>A /* */ comment written between the expression and the AS keyword.</summary>
    public string? PreAliasComment { get; set; }
    /// <summary>A /* */ comment written between the AS keyword and the alias.</summary>
    public string? PostAliasComment { get; set; }
}

// ─── FROM / JOIN ─────────────────────────────────────────────────────────────

public sealed class TableRefNode : AstNode
{
    public List<Token> Name { get; } = new();
    public Token? Alias { get; init; }
    public string? HintNolock { get; init; }
    /// <summary>A -- comment on the same line as this table reference in a FROM clause.</summary>
    public string? TrailingComment { get; set; }
    /// <summary>True when the author wrote that comment glued to the source ("as u/*note*/");
    /// it then stays glued.</summary>
    public bool TrailingCommentGlued { get; set; }
    /// <summary>Comment written between the clause keyword (from / join) and the table name;
    /// it stays there, in front of the name.</summary>
    public string? LeadingComment { get; set; }
    /// <summary>Subquery used as a table source: (SELECT ...) AS alias</summary>
    public SubQueryNode? SubQuery { get; init; }
    /// <summary>Arguments for function-valued table sources: func(arg1, arg2) AS alias</summary>
    public List<AstNode>? FuncArgs { get; init; }
    /// <summary>True when this is an OPENQUERY(server, 'remote sql') table source (rule 7).</summary>
    public bool IsOpenQuery { get; init; }
    /// <summary>PIVOT / UNPIVOT applied to this source, with its own alias.</summary>
    public PivotNode? Pivot { get; set; }
}

/// <summary>
/// PIVOT (count(x) FOR col IN ([a], [b])) AS pvt — and UNPIVOT, which has the same shape with a
/// plain column where PIVOT has its aggregate.
/// </summary>
public sealed class PivotNode : AstNode
{
    /// <summary>"pivot" or "unpivot".</summary>
    public string Kind { get; init; } = "pivot";
    /// <summary>The aggregate call (PIVOT) or the value column (UNPIVOT).</summary>
    public AstNode Head { get; init; } = null!;
    /// <summary>The column after FOR.</summary>
    public AstNode ForColumn { get; init; } = null!;
    /// <summary>The IN (...) list, one entry per value.</summary>
    public List<AstNode> InValues { get; } = new();
    public Token? Alias { get; set; }
    /// <summary>Standalone comments written between the source and the PIVOT keyword.</summary>
    public List<string> LeadingComments { get; } = new();
    /// <summary>Comment written after the aggregate / value column.</summary>
    public string? HeadComment { get; set; }
    /// <summary>Comment written after the IN (...) list.</summary>
    public string? InComment { get; set; }
}

public sealed class JoinNode : AstNode
{
    public string JoinType { get; init; } = "INNER JOIN";
    public TableRefNode Table { get; init; } = null!;
    public List<AstNode> Conditions { get; } = new();
    /// <summary>Standalone comments that appeared on their own line(s) before this join.</summary>
    public List<string> LeadingComments { get; } = new();
    /// <summary>A comment on the join's own line, after the table/alias:
    /// "left join dbo.t as u  --создал процесс".</summary>
    public string? TrailingComment { get; set; }
}

// ─── WHERE / conditions ──────────────────────────────────────────────────────

public sealed class ConditionNode : AstNode
{
    public string? LogicalOp { get; init; }
    public AstNode Expression { get; init; } = null!;
    /// <summary>Inline comment on the same line, after the condition expression.</summary>
    public string? TrailingComment { get; set; }
    /// <summary>Standalone comments on their own line(s) before this condition (e.g. a
    /// commented-out "--and x = 1"). Rendered above the condition, never inline.</summary>
    public List<string> LeadingComments { get; } = new();
}

// ─── Expressions ─────────────────────────────────────────────────────────────

public sealed class BinaryExprNode : AstNode
{
    public AstNode Left { get; init; } = null!;
    public Token Op { get; init; } = null!;
    public AstNode Right { get; init; } = null!;
    /// <summary>A /* */ comment written between the left operand and the operator, kept there.</summary>
    public string? OpLeadingComment { get; set; }
}

/// <summary>A parenthesised expression: ( expr ). Preserves grouping like ((a+b)*(c-d)).</summary>
public sealed class ParenExprNode : AstNode
{
    public AstNode Inner { get; init; } = null!;
}

/// <summary>A parenthesised boolean group: ( cond and cond or cond ). Preserves AND/OR structure.</summary>
public sealed class ConditionGroupNode : AstNode
{
    public List<AstNode> Conditions { get; } = new();
    /// <summary>A comment on the same line as the opening paren: "not (  --note".</summary>
    public string? OpenComment { get; init; }
}

/// <summary>An ORDER BY item: expression plus optional ASC/DESC direction.</summary>
public sealed class OrderByItemNode : AstNode
{
    public AstNode Expression { get; init; } = null!;
    public string? Direction { get; init; }  // "asc" | "desc" | null
    /// <summary>Comment written after this item on the same line.</summary>
    public string? TrailingComment { get; set; }
}

public sealed class InExprNode : AstNode
{
    public AstNode Left { get; init; } = null!;
    public bool Negated { get; init; }
    public List<AstNode> Values { get; } = new();
    /// <summary>Set when the IN list is a subquery: x in (select ...).</summary>
    public SubQueryNode? SubQuery { get; init; }
    /// <summary>True when a -- comment forces the list onto separate lines. A /* */ block
    /// comment is transparent and keeps the list inline, so it does not count here.</summary>
    public bool HasComments => Values.Any(v =>
        v is CommentedValueNode ||
        (v is InValueGroupNode g && g.TrailingLineComment != null));
}

public sealed class CommentedValueNode : AstNode
{
    public AstNode Value { get; init; } = null!;
    public string? TrailingComment { get; init; }
}

public sealed class BetweenExprNode : AstNode
{
    public AstNode Left { get; init; } = null!;
    public AstNode Low { get; init; } = null!;
    public AstNode High { get; init; } = null!;
    /// <summary>True for NOT BETWEEN. Dropping it would invert the condition.</summary>
    public bool Negated { get; init; }
}

public sealed class LikeExprNode : AstNode
{
    public AstNode Left { get; init; } = null!;
    public AstNode Pattern { get; init; } = null!;
    /// <summary>True for NOT LIKE. Dropping it would invert the condition.</summary>
    public bool Negated { get; init; }
}

public sealed class IsNullExprNode : AstNode
{
    public AstNode Left { get; init; } = null!;
    public bool IsNotNull { get; init; }
}

public sealed class SubQueryNode : AstNode
{
    public AstNode Select { get; init; } = null!;   // SelectStatementNode or SetOperationNode
    /// <summary>A comment on the SAME line as the opening '(' (e.g. "exists ( --note").
    /// Rendered right after the '(', so it stays on that line instead of above the select.</summary>
    public string? OpenComment { get; set; }
    /// <summary>Comments standing between the end of the subquery and its ')'. Rendered on their
    /// own line(s) there, so a note at the bottom of a subquery stays at the bottom of it.</summary>
    public List<string> CloseComments { get; } = new();
}

/// <summary>
/// The OVER (…) specification of a window function: PARTITION BY / ORDER BY lists plus any frame
/// clause (ROWS/RANGE …) kept as raw tokens.
/// </summary>
public sealed class WindowSpecNode : AstNode
{
    public List<AstNode> PartitionBy { get; } = new();
    public List<AstNode> OrderBy { get; } = new();
    public List<Token> Frame { get; } = new();
    /// <summary>Comments written on their own line(s) right after "over (", kept there.</summary>
    public List<string> LeadingComments { get; } = new();
}

public sealed class FunctionCallNode : AstNode
{
    public string Name { get; init; } = string.Empty;
    /// <summary>True when the function name was a SQL keyword (built-in). Emit lowercase.</summary>
    public bool IsKeywordFunction { get; init; }
    public List<AstNode> Arguments { get; } = new();
    /// <summary>
    /// Trailing comment per argument (same length as <see cref="Arguments"/>, null where there is
    /// none). A comma goes before the comment, never inside it.
    /// </summary>
    public List<string?> ArgumentComments { get; } = new();
    /// <summary>Window function OVER (...) clause, if any.</summary>
    public AstNode? OverClause { get; init; }
    /// <summary>Set quantifier inside an aggregate: "distinct" or "all" (e.g. count(distinct x)).</summary>
    public string? SetQuantifier { get; init; }
    /// <summary>True for a negated EXISTS: renders "not exists (...)" as a single construct.</summary>
    public bool Negated { get; init; }
}

/// <summary>Unary boolean negation: NOT (condition/group). Kept as one node so a condition
/// list never splits "not" and its operand into two separate conditions.</summary>
public sealed class NotExprNode : AstNode
{
    public AstNode Inner { get; init; } = null!;
}

/// <summary>
/// An operand with /* */ comments glued around it, the way the author wrote them:
/// "between /*dateadd(month,-2,*/@from/*)*/ and @to" — commented-out code around a value.
/// </summary>
public sealed class InlineCommentedNode : AstNode
{
    public string? Before { get; set; }
    public AstNode Inner { get; init; } = null!;
    public string? After { get; set; }
}

/// <summary>A sign written in front of an operand: "+14", "-(a + b)".</summary>
public sealed class UnaryExprNode : AstNode
{
    public Token Op { get; init; } = null!;
    public AstNode Operand { get; init; } = null!;
}

public sealed class CaseExprNode : AstNode
{
    public AstNode? InputExpr { get; init; }
    public List<WhenClauseNode> WhenClauses { get; } = new();
    public AstNode? ElseExpr { get; init; }
    public string? ElseComment { get; init; }
    /// <summary>A comment written on the "case" line itself.</summary>
    public string? HeaderComment { get; set; }
    /// <summary>Standalone comments written just before ELSE, each on its own line.</summary>
    public List<string> ElseLeadingComments { get; } = new();
    /// <summary>Standalone comments written just before END, each on its own line.</summary>
    public List<string> EndLeadingComments { get; } = new();
}

public sealed class WhenClauseNode : AstNode
{
    public List<AstNode> Conditions { get; } = new();
    public AstNode Then { get; init; } = null!;
    public string? ThenComment { get; init; }
    /// <summary>Standalone comments written above this WHEN, each on its own line.</summary>
    public List<string> LeadingComments { get; } = new();
    /// <summary>A comment written between the last condition and THEN; closes the when line.</summary>
    public string? ConditionComment { get; set; }
    /// <summary>A /* */ comment written between THEN and its value; stays in front of the value.</summary>
    public string? ThenLeadingComment { get; set; }
}

public sealed class LiteralNode : AstNode
{
    public Token Token { get; init; } = null!;
    public string? TrailingComment { get; set; }
}

public sealed class ColumnRefNode : AstNode
{
    public List<Token> Parts { get; } = new();
    public string? TrailingComment { get; set; }
}

/// <summary>A ';' the author wrote on a line of its own; it keeps that line.</summary>
public sealed class SemicolonNode : AstNode
{
}

public sealed class GoSeparatorNode : AstNode
{
    /// <summary>Optional repeat count on the same line: "GO 5" runs the batch 5 times.
    /// Must be preserved, or the batch silently stops repeating.</summary>
    public string? Count { get; set; }
}

// ─── Fragment nodes (for formatting partial selections) ──────────────────────

/// <summary>A bare WHERE fragment: "where a = 1 and b = 2" (with or without leading 'where').</summary>
public sealed class WhereFragmentNode : AstNode
{
    public List<AstNode> Conditions { get; } = new List<AstNode>();
}

/// <summary>A bare column-list fragment: "a, b, count(*) as cnt".</summary>
public sealed class ColumnListFragmentNode : AstNode
{
    public List<SelectColumnNode> Columns { get; } = new List<SelectColumnNode>();
}

/// <summary>A bare JOIN fragment: "inner join t on t.id = x.id".</summary>
public sealed class JoinFragmentNode : AstNode
{
    public List<JoinNode> Joins { get; } = new List<JoinNode>();
}

/// <summary>A bare GROUP BY fragment: "group by a, b, c".</summary>
public sealed class GroupByFragmentNode : AstNode
{
    public List<AstNode> Columns { get; } = new List<AstNode>();
}

/// <summary>A bare ORDER BY fragment: "order by a desc, b".</summary>
public sealed class OrderByFragmentNode : AstNode
{
    public List<AstNode> Items { get; } = new List<AstNode>();
}

// ─── BEGIN / END ─────────────────────────────────────────────────────────────

/// <summary>BEGIN ... END block. Rule 2.8: blank line after begin, before end; content +1 tab.</summary>
public sealed class BeginEndNode : AstNode
{
    public List<AstNode> Body { get; } = new List<AstNode>();
    /// <summary>"try" or "catch" for BEGIN TRY … END TRY / BEGIN CATCH … END CATCH; null for a
    /// plain BEGIN … END block.</summary>
    public string? Label { get; init; }
}

// ─── CREATE TABLE / DROP TABLE ───────────────────────────────────────────────

public sealed class ColumnDefNode : AstNode
{
    /// <summary>Column name token.</summary>
    public string Name { get; init; } = "";
    /// <summary>Full definition after the name: type + constraints (e.g. "varchar(255)" or "int not null default 0").</summary>
    public string Definition { get; init; } = "";
    /// <summary>Comment written after this column; the comma is emitted before it.</summary>
    public string? TrailingComment { get; set; }
    /// <summary>Standalone comments written above this column, each on its own line.</summary>
    public List<string> LeadingComments { get; } = new();
}

public sealed class CreateTableNode : AstNode
{
    public string TableName { get; init; } = "";
    public List<ColumnDefNode> Columns { get; } = new List<ColumnDefNode>();
    /// <summary>Comment written between the table name and its column list; it stays there,
    /// in front of the opening paren.</summary>
    public string? NameComment { get; set; }
    /// <summary>Standalone comments left after the last column, above the closing paren.</summary>
    public List<string> CloseComments { get; } = new();
}

public sealed class DropTableNode : AstNode
{
    public bool IfExists { get; init; }
    public string TableName { get; init; } = "";
    /// <summary>Comments written between DROP and TABLE ("drop /*x*/ table #t"); they stay
    /// there, inline, since the statement is one line.</summary>
    public List<string> KindComments { get; } = new();
    /// <summary>Comments written between TABLE and IF EXISTS.</summary>
    public List<string> ExistsComments { get; } = new();
    /// <summary>Comments written between TABLE (or IF EXISTS) and the table name.</summary>
    public List<string> NameComments { get; } = new();
}

// ─── IN value group (rule 2.3.2.3) ──────────────────────────────────────────

/// <summary>
/// A group of values inside an IN list that share one comment.
/// Examples:
///   44199, 431064, 9730,  --УТВ
///   /*ктв*/44199, 431064
/// </summary>
/// <summary>
/// A GROUP BY / ORDER BY item together with the standalone comments written above it. A -- comment
/// on its own line is not an expression, so without this it aborted the parse of the whole script.
/// </summary>
public sealed class ListItemNode : AstNode
{
    public AstNode Expression { get; init; } = null!;
    public List<string> LeadingComments { get; } = new();
}

public sealed class InValueGroupNode : AstNode
{
    public List<AstNode> Values { get; } = new List<AstNode>();
    /// <summary>Leading /*block*/ comment before the first value.</summary>
    public string? LeadingBlockComment { get; init; }
    /// <summary>Trailing -- line comment after the last value.</summary>
    public string? TrailingLineComment { get; init; }
}

// ─── Programmable objects (CREATE/ALTER FUNCTION / PROCEDURE) ─────────────────

/// <summary>A single parameter of a function/procedure: @name type [= default] [output].</summary>
public sealed class ParamNode : AstNode
{
    public Token Variable { get; init; } = null!;
    public List<Token> DataType { get; } = new();
    public AstNode? Default { get; init; }
    public bool Output { get; init; }
    public string? TrailingComment { get; set; }
}

/// <summary>CREATE/ALTER FUNCTION|PROCEDURE with a parameter list and a procedural body.</summary>
public sealed class ProgrammableObjectNode : AstNode
{
    public string Prefix { get; init; } = "";              // e.g. "create or alter function"
    public string Name { get; init; } = "";                // e.g. "dbo.fnErlangAgents"
    public bool HasParens { get; init; }                   // parameters were parenthesised
    public List<ParamNode> Params { get; } = new();
    public List<Token> ReturnsClause { get; } = new();     // tokens between ')' and AS (e.g. "returns int")
    public AstNode? Body { get; init; }                    // usually a BeginEndNode
}

/// <summary>IF &lt;conditions&gt; &lt;then&gt; [ELSE &lt;else&gt;].</summary>
public sealed class IfNode : AstNode
{
    public List<AstNode> Conditions { get; } = new();
    public AstNode? Then { get; init; }
    public AstNode? Else { get; init; }
    /// <summary>A comment written on the "else" line itself; it stays there.</summary>
    public string? ElseComment { get; set; }
}

/// <summary>WHILE &lt;conditions&gt; &lt;body&gt;.</summary>
public sealed class WhileNode : AstNode
{
    public List<AstNode> Conditions { get; } = new();
    public AstNode? Body { get; init; }
}

/// <summary>SET @var {= | += | -= | …} &lt;value&gt;.</summary>
public sealed class SetNode : AstNode
{
    public AstNode Target { get; init; } = null!;
    public string Op { get; init; } = "=";
    public AstNode Value { get; init; } = null!;
}

/// <summary>RETURN [&lt;value&gt;].</summary>
public sealed class ReturnNode : AstNode
{
    public AstNode? Value { get; init; }
}

public sealed class RawTokensNode : AstNode
{
    public List<Token> Tokens { get; } = new();
    /// <summary>True if the original source had a blank line before this node.</summary>
    public bool BlankBefore { get; set; }
    /// <summary>True if the original source had a blank line after this node.</summary>
    public bool BlankAfter { get; set; }
}

public sealed class CteDefinitionNode : AstNode
{
    public Token Name { get; init; } = null!;
    /// <summary>SelectStatementNode or SetOperationNode.</summary>
    public AstNode Body { get; init; } = null!;
}

}
