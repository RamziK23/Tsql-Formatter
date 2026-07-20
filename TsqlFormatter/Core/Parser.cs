using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TsqlFormatter.Core
{

public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;

    private static readonly HashSet<TokenType> Skippable = new()
    {
        TokenType.Whitespace, TokenType.Newline
    };

    public Parser(List<Token> tokens) { _tokens = tokens; }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Script
    // ═══════════════════════════════════════════════════════════════════════════

    public ScriptNode Parse()
    {
        var script = new ScriptNode();
        bool isFirstStatement = true;

        while (!IsAtEnd())
        {
            bool hadGo = false;
            bool hadSemicolon = false;
            while (!IsAtEnd())
            {
                var raw = PeekRaw();
                if (raw.Type == TokenType.Semicolon)
                    { hadSemicolon = true; AdvanceRaw(); continue; }
                if (raw.Type is TokenType.Whitespace or TokenType.Newline)
                    { AdvanceRaw(); continue; }
                if (IsGoKeyword())
                    { hadGo = true; AdvanceRaw(); continue; }
                break;
            }

            // Trailing GO(s) at end of file — collapse to one and stop
            if (IsAtEnd())
            {
                if (hadGo && script.Statements.Count > 0)
                    script.Statements.Add(new GoSeparatorNode());
                break;
            }

            // Deduplicated GO separator between statements
            if (hadGo && script.Statements.Count > 0)
                script.Statements.Add(new GoSeparatorNode());

            // Leading semicolon before first statement (e.g. ;with ...)
            if (isFirstStatement && hadSemicolon)
                script.HasLeadingSemicolon = true;
            isFirstStatement = false;

            var stmt = ParseStatement();
            if (stmt != null) script.Statements.Add(stmt);
        }
        return script;
    }

    /// <summary>
    /// Parses a partial SQL selection (fragment). Returns a fragment node when the input
    /// is a bare WHERE clause, column list, or JOIN chain; otherwise returns null so the
    /// caller can fall back to full-statement parsing.
    /// </summary>
    public AstNode? ParseFragment()
    {
        var first = Peek();

        // A leading standalone comment means this is (at least) a full statement with a
        // preceding comment, not a bare clause fragment. Let full parsing handle it.
        if (first.Type is TokenType.LineComment or TokenType.BlockComment)
            return null;

        // Bare WHERE clause: "where a = 1 and b = 2"
        if (first.IsKeyword("WHERE"))
        {
            Advance();
            var node = new WhereFragmentNode();
            node.Conditions.AddRange(ParseConditionList(isJoinOn: false));
            return AtFragmentEnd() ? node : null;
        }

        // Bare GROUP BY: "group by a, b, c"
        if (first.IsKeyword("GROUP"))
        {
            Advance();
            if (Peek().IsKeyword("BY")) Advance();
            var node = new GroupByFragmentNode();
            node.Columns.AddRange(ParseExpressionList());
            return AtFragmentEnd() ? node : null;
        }

        // Bare ORDER BY: "order by a desc, b asc"
        if (first.IsKeyword("ORDER"))
        {
            Advance();
            if (Peek().IsKeyword("BY")) Advance();
            var node = new OrderByFragmentNode();
            node.Items.AddRange(ParseOrderByList());
            return AtFragmentEnd() ? node : null;
        }

        // Bare JOIN chain: "inner join t on ... [join ...]"
        if (IsJoinKeyword())
        {
            var node = new JoinFragmentNode();
            while (IsJoinKeyword()) node.Joins.Add(ParseJoin());
            return AtFragmentEnd() ? node : null;
        }

        // If it starts with a statement keyword, this isn't a fragment.
        if (first.IsKeyword("SELECT") || first.IsKeyword("INSERT") || first.IsKeyword("UPDATE")
            || first.IsKeyword("DELETE") || first.IsKeyword("WITH") || first.IsKeyword("BEGIN")
            || first.IsKeyword("CREATE") || first.IsKeyword("DROP") || first.IsKeyword("DECLARE")
            || first.Type == TokenType.DeclareKeyword)
            return null;

        // Bare condition list (no leading WHERE): "a = 1 and b = 2".
        int save = _pos;
        try
        {
            var conds = ParseConditionList(isJoinOn: false);
            if (conds.Count > 0 && AtFragmentEnd() && LooksLikeConditions(conds))
            {
                var node = new WhereFragmentNode();
                node.Conditions.AddRange(conds);
                return node;
            }
        }
        catch { /* fall through */ }
        _pos = save;

        // Bare column list: "a, b, count(*) as cnt"
        save = _pos;
        try
        {
            var cols = ParseSelectColumns();
            if (cols.Count > 0 && AtFragmentEnd())
            {
                var node = new ColumnListFragmentNode();
                node.Columns.AddRange(cols);
                return node;
            }
        }
        catch { /* fall through */ }
        _pos = save;

        return null;
    }

    /// <summary>True if only EOF remains (skippables are transparent to Peek).</summary>
    private bool AtFragmentEnd() => Peek().Type == TokenType.EndOfFile;

    /// <summary>Heuristic: a condition list is "real" if at least one item is a comparison/logical expr.</summary>
    private static bool LooksLikeConditions(List<AstNode> conds)
    {
        foreach (var c in conds)
        {
            if (c is ConditionNode cn)
            {
                var e = cn.Expression;
                if (e is BinaryExprNode || e is InExprNode || e is BetweenExprNode || e is LikeExprNode)
                    return true;
            }
        }
        return false;
    }

    private bool IsGoKeyword()
    {
        var t = Peek();
        return t.Type == TokenType.Identifier && t.Value.Equals("GO", StringComparison.OrdinalIgnoreCase);
    }

    private AstNode? ParseStatement()
    {
        var tok = Peek();
        if (tok.Type == TokenType.DeclareKeyword)  return ParseDeclare();
        if (tok.IsKeyword("WITH"))                 return ParseWithCte();
        if (tok.IsKeyword("SELECT"))               return ParseSelectOrSet(null);
        if (tok.IsKeyword("INSERT"))               return ParseInsert();
        if (tok.IsKeyword("UPDATE"))               return ParseUpdate();
        if (tok.IsKeyword("DELETE"))               return ParseDelete();
        if (tok.IsKeyword("BEGIN"))                return ParseBeginEnd();
        if (tok.IsKeyword("CREATE"))               return ParseCreate();
        if (tok.IsKeyword("DROP"))                 return ParseDrop();
        if (tok.Type == TokenType.Semicolon)        { Advance(); return null; }
        // Standalone comment: consume as single-token raw node (prevent bleeding into next statement)
        if (tok.Type is TokenType.LineComment or TokenType.BlockComment)
        {
            bool blankBefore = CountNewlinesBackFromCurrent() >= 2;
            var cmt = new RawTokensNode(); cmt.Tokens.Add(Advance());
            bool blankAfter = CountNewlinesForwardFromCurrent() >= 2;
            cmt.BlankBefore = blankBefore;
            cmt.BlankAfter = blankAfter;
            return cmt;
        }
        return ParseRawStatement();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DECLARE
    // ═══════════════════════════════════════════════════════════════════════════

    private DeclareNode ParseDeclare()
    {
        Advance(); // DECLARE keyword
        var node = new DeclareNode();
        do
        {
            var variable = Expect(TokenType.Variable);
            var dataType = new List<Token>();
            int typeDepth = 0;
            while (!IsAtEnd())
            {
                var t = Peek();
                // A comma or '=' ends the type only at paren depth 0
                if (typeDepth == 0 && (PeekIs(TokenType.Equals) || PeekIs(TokenType.Comma))) break;
                if (t.IsKeyword("SELECT") || t.Type == TokenType.DeclareKeyword
                    || IsGoKeyword() || t.Type == TokenType.EndOfFile) break;
                if (t.Type == TokenType.LeftParen)  typeDepth++;
                if (t.Type == TokenType.RightParen) typeDepth--;
                dataType.Add(Advance());
            }
            AstNode? init = null;
            if (TryConsume(TokenType.Equals)) init = ParseExpression();
            // Trailing comment before the comma: @a int = 1, --note
            string? varComment = null;
            if (PeekIs(TokenType.LineComment)) varComment = Advance().Value;
            node.Variables.Add(new DeclareVarNode { Variable = variable, Initializer = init, TrailingComment = varComment }
                .Tap(v => v.DataType.AddRange(dataType)));
            // A trailing comment may also sit after the comma: @a int = 1, --note
            if (PeekIs(TokenType.Comma))
            {
                // peek: comment right after comma belongs to the just-added variable if it had none
            }
        } while (PeekIs(TokenType.Comma) && ConsumeCommaThenMaybeComment(node));
        return node;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WITH / CTE
    // ═══════════════════════════════════════════════════════════════════════════

    private AstNode ParseWithCte()
    {
        Advance(); // WITH
        var ctes = new List<AstNode>();
        while (true)
        {
            var cteName = Advance();
            Expect(TokenType.Keyword, "AS");
            Expect(TokenType.LeftParen);
            var cteBody = ParseSelectOrSet(null);
            Expect(TokenType.RightParen);
            ctes.Add(new CteDefinitionNode { Name = cteName, Body = cteBody });
            if (!PeekIs(TokenType.Comma)) break;
            Advance();
        }
        return ParseSelectOrSet(ctes);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SELECT  (+ UNION / EXCEPT / INTERSECT chaining)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a single SELECT then checks for UNION / EXCEPT / INTERSECT.
    /// Returns either a SelectStatementNode or a SetOperationNode tree.
    /// CTEs are attached only to the outermost left node.
    /// </summary>
    private AstNode ParseSelectOrSet(List<AstNode>? ctes)
    {
        AstNode left = ParseSelectStatement(ctes);

        while (IsSetOperator())
        {
            string op = BuildSetOperator(); // "UNION" / "UNION ALL" / "EXCEPT" / "INTERSECT"
            AstNode right = ParseSelectStatement(null);
            left = new SetOperationNode { Operator = op, Left = left, Right = right };
        }

        return left;
    }

    private bool IsSetOperator()
    {
        var t = Peek();
        return t.IsKeyword("UNION") || t.IsKeyword("EXCEPT") || t.IsKeyword("INTERSECT");
    }

    private string BuildSetOperator()
    {
        var t = Advance(); // UNION / EXCEPT / INTERSECT
        string op = t.Value.ToLowerInvariant();
        if (op == "union" && Peek().IsKeyword("ALL")) { Advance(); op = "union all"; }
        return op;
    }

    private SelectStatementNode ParseSelectStatement(List<AstNode>? ctes)
    {
        Expect(TokenType.Keyword, "SELECT");
        // SELECT [DISTINCT] [TOP ...] — DISTINCT precedes TOP syntactically.
        bool distinct = false;
        if (Peek().IsKeyword("DISTINCT")) { distinct = true; Advance(); }
        string? topExpr = null;
        if (Peek().IsKeyword("TOP"))
        {
            Advance();
            var tb = new System.Text.StringBuilder();
            if (PeekIs(TokenType.LeftParen))
            {
                Advance();
                int depth = 1; tb.Append('(');
                while (!IsAtEnd() && depth > 0)
                {
                    var t = Peek();
                    if (t.Type == TokenType.LeftParen) depth++;
                    if (t.Type == TokenType.RightParen) { depth--; }
                    tb.Append(t.Value);
                    Advance();
                }
            }
            else
            {
                tb.Append(Advance().Value); // TOP n
            }
            if (Peek().Value.Equals("PERCENT", System.StringComparison.OrdinalIgnoreCase)
                && Peek().Type != TokenType.StringLiteral) { Advance(); tb.Append(" percent"); }
            // WITH TIES — TIES may lex as an identifier, so match by value.
            if (Peek().IsKeyword("WITH")
                && PeekAt(1).Value.Equals("TIES", System.StringComparison.OrdinalIgnoreCase))
            { Advance(); Advance(); tb.Append(" with ties"); }
            topExpr = tb.ToString();
        }
        var node = new SelectStatementNode { IsDistinct = distinct, TopExpr = topExpr };
        if (ctes != null) node.CteDefinitions.AddRange(ctes);
        node.Columns.AddRange(ParseSelectColumns());
        if (Peek().IsKeyword("FROM"))
        {
            Advance();
            node.FromClauses.Add(ParseTableRef());
            // Collect joins, tolerating standalone comments between FROM and each JOIN.
            while (true)
            {
                var pending = CollectStandaloneComments();
                if (IsJoinKeyword())
                {
                    var join = ParseJoin();
                    join.LeadingComments.AddRange(pending);
                    node.FromClauses.Add(join);
                }
                else
                {
                    // Not a join — push the comments onto the next construct instead.
                    node.PostFromComments.AddRange(pending);
                    break;
                }
            }
        }
        if (Peek().IsKeyword("WHERE"))  { Advance(); node.WhereConditions.AddRange(ParseConditionList(isJoinOn: false)); }
        if (Peek().IsKeyword("GROUP"))  { Advance(); Expect(TokenType.Keyword, "BY"); node.GroupByColumns.AddRange(ParseExpressionList()); }
        if (Peek().IsKeyword("HAVING")) { Advance(); node.WhereConditions.Add(new ConditionNode { LogicalOp = "having", Expression = ParseExpression() }); }
        if (Peek().IsKeyword("ORDER"))  { Advance(); Expect(TokenType.Keyword, "BY"); node.OrderByColumns.AddRange(ParseOrderByList()); }
        return node;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  INSERT
    // ═══════════════════════════════════════════════════════════════════════════

    private InsertNode ParseInsert()
    {
        Advance(); // INSERT
        if (Peek().IsKeyword("INTO")) Advance(); // optional INTO

        var table = ParseTableRef();

        // Optional column list: INSERT INTO t (col1, col2)
        var columns = new List<AstNode>();
        if (PeekIs(TokenType.LeftParen))
        {
            // Peek ahead: if first non-paren token is SELECT → this is not a column list
            int savedPos = _pos;
            Advance(); // (
            if (!Peek().IsKeyword("SELECT"))
            {
                while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
                {
                    columns.Add(ParsePrimary());
                    if (PeekIs(TokenType.Comma)) Advance();
                }
                Expect(TokenType.RightParen);
            }
            else
            {
                // Restore: the ( belongs to the subquery handled below
                _pos = savedPos;
            }
        }

        AstNode? source = null;
        if (Peek().IsKeyword("VALUES"))
        {
            source = ParseValues();
        }
        else if (Peek().IsKeyword("SELECT"))
        {
            source = ParseSelectOrSet(null);
        }
        else if (PeekIs(TokenType.LeftParen))
        {
            // (SELECT ...)
            Advance();
            source = ParseSelectOrSet(null);
            Expect(TokenType.RightParen);
        }

        return new InsertNode { Table = table, Source = source }.Tap(n => n.Columns.AddRange(columns));
    }

    private ValuesNode ParseValues()
    {
        Advance(); // VALUES
        var valuesNode = new ValuesNode();
        do
        {
            Expect(TokenType.LeftParen);
            var row = new List<AstNode>();
            while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
            {
                row.Add(ParseExpression());
                if (PeekIs(TokenType.Comma)) Advance();
            }
            Expect(TokenType.RightParen);
            valuesNode.Rows.Add(row);
        } while (PeekIs(TokenType.Comma) && AdvanceAndTrue());
        return valuesNode;
    }

    private bool AdvanceAndTrue() { Advance(); return true; }

    /// <summary>Consumes the comma between DECLARE variables; a comment right after the comma
    /// is attached to the variable just parsed (if it doesn't already carry one).</summary>
    private bool ConsumeCommaThenMaybeComment(DeclareNode node)
    {
        Advance(); // comma
        if (PeekIs(TokenType.LineComment))
        {
            var c = Advance().Value;
            if (node.Variables.Count > 0 && node.Variables[^1].TrailingComment == null)
                node.Variables[^1].TrailingComment = c;
        }
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  UPDATE
    // ═══════════════════════════════════════════════════════════════════════════

    private UpdateNode ParseUpdate()
    {
        Advance(); // UPDATE
        var table = ParseTableRef();
        Expect(TokenType.Keyword, "SET");

        var assignments = new List<AssignmentNode>();
        do
        {
            var target = ParsePrimary();
            Expect(TokenType.Equals);
            var value = ParseExpression();
            assignments.Add(new AssignmentNode { Target = target, Value = value });
        } while (PeekIs(TokenType.Comma) && AdvanceAndTrue());

        var fromClauses = new List<AstNode>();
        if (Peek().IsKeyword("FROM"))
        {
            Advance();
            fromClauses.Add(ParseTableRef());
            while (IsJoinKeyword()) fromClauses.Add(ParseJoin());
        }

        var whereConds = new List<AstNode>();
        if (Peek().IsKeyword("WHERE")) { Advance(); whereConds.AddRange(ParseConditionList(isJoinOn: false)); }

        return new UpdateNode { Table = table }.Tap(n =>
        {
            n.Assignments.AddRange(assignments);
            n.FromClauses.AddRange(fromClauses);
            n.WhereConditions.AddRange(whereConds);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DELETE
    // ═══════════════════════════════════════════════════════════════════════════

    private DeleteNode ParseDelete()
    {
        Advance(); // DELETE

        // Two forms:
        //   DELETE alias FROM tbl ...
        //   DELETE FROM tbl ...
        TableRefNode? targetAlias = null;
        TableRefNode? table = null;
        var fromClauses = new List<AstNode>();

        if (Peek().IsKeyword("FROM"))
        {
            // DELETE FROM tbl ...
            Advance();
            table = ParseTableRef();
            while (IsJoinKeyword()) fromClauses.Add(ParseJoin());
        }
        else
        {
            // DELETE alias FROM tbl ...
            targetAlias = ParseTableRef();
            if (Peek().IsKeyword("FROM"))
            {
                Advance();
                fromClauses.Add(ParseTableRef());
                while (IsJoinKeyword()) fromClauses.Add(ParseJoin());
            }
        }

        var whereConds = new List<AstNode>();
        if (Peek().IsKeyword("WHERE")) { Advance(); whereConds.AddRange(ParseConditionList(isJoinOn: false)); }

        return new DeleteNode { TargetAlias = targetAlias, Table = table }.Tap(n =>
        {
            n.FromClauses.AddRange(fromClauses);
            n.WhereConditions.AddRange(whereConds);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SELECT column list
    // ═══════════════════════════════════════════════════════════════════════════

    private List<SelectColumnNode> ParseSelectColumns()
    {
        var cols = new List<SelectColumnNode>();
        while (!IsAtEnd() && !IsSelectClauseKeyword())
        {
            if (PeekIs(TokenType.Comma)) { Advance(); continue; }

            // Leading block comment before a column: /* note */ expr
            string? leadingComment = null;
            if (PeekIs(TokenType.BlockComment)) leadingComment = Advance().Value;

            // Rule: detect T-SQL assignment alias: [Alias] = expression
            // Pattern: simple identifier/quoted-identifier immediately followed by =
            Token? assignAlias = null;
            if (Peek().Type is TokenType.Identifier or TokenType.QuotedIdentifier
                && PeekAt(1).Type == TokenType.Equals)
            {
                assignAlias = Advance(); // consume alias token
                Advance();              // consume =
            }

            var expr = ParseExpression();
            Token? alias = assignAlias;
            if (alias == null)
            {
                if (Peek().IsKeyword("AS")) { Advance(); alias = Advance(); }
                else if (Peek().Type is TokenType.Identifier or TokenType.QuotedIdentifier
                         && !IsClauseKeyword(Peek()) && !IsGoKeyword())
                    alias = Advance();
            }
            string? comment = null;
            // Only a comment on the SAME line is a trailing comment for this column.
            comment = TryTakeSameLineComment();
            if (PeekIs(TokenType.Comma)) Advance();
            // A trailing comment may also sit AFTER the comma on the same line: "a, --note"
            if (comment == null) comment = TryTakeSameLineComment();
            cols.Add(new SelectColumnNode { Expression = expr, Alias = alias, TrailingComment = comment, LeadingComment = leadingComment });
        }
        return cols;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  FROM / JOIN
    // ═══════════════════════════════════════════════════════════════════════════

    private TableRefNode ParseTableRef()
    {
        // Subquery as table source: (SELECT ...) AS alias
        if (PeekIs(TokenType.LeftParen))
        {
            Advance(); // (
            var sub = ParseSelectOrSet(null);
            Expect(TokenType.RightParen);
            Token? sqAlias = null;
            if (Peek().IsKeyword("AS")) { Advance(); sqAlias = Advance(); }
            else if (Peek().Type is TokenType.Identifier or TokenType.QuotedIdentifier && !IsJoinKeyword() && !IsSelectClauseKeyword())
                sqAlias = Advance();
            return new TableRefNode { SubQuery = new SubQueryNode { Select = sub }, Alias = sqAlias };
        }

        var nameParts = new List<Token>();
        nameParts.Add(Advance());
        while (PeekIs(TokenType.Dot)) { nameParts.Add(Advance()); nameParts.Add(Advance()); }

        // Function-valued table source: name(arg1, arg2, ...) — e.g. openjson(col) or STRING_SPLIT(col, ',')
        List<AstNode>? funcArgs = null;
        if (PeekIs(TokenType.LeftParen))
        {
            Advance(); // (
            funcArgs = new List<AstNode>();
            while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
            {
                funcArgs.Add(ParseExpression());
                if (PeekIs(TokenType.Comma)) Advance();
                else break;
            }
            Expect(TokenType.RightParen);
        }

        Token? alias = null;
        if (Peek().IsKeyword("AS")) { Advance(); alias = Advance(); }
        else if (Peek().Type is TokenType.Identifier or TokenType.QuotedIdentifier
                 && !IsJoinKeyword() && !IsSelectClauseKeyword() && !IsGoKeyword())
            alias = Advance();

        // Table hint: WITH (NOLOCK), WITH (INDEX(..)), etc. — capture raw hint text.
        string? hint = null;
        if (Peek().IsKeyword("WITH") && PeekAt(1).Type == TokenType.LeftParen)
        {
            Advance(); // WITH
            Advance(); // (
            var hb = new System.Text.StringBuilder();
            int depth = 1;
            while (!IsAtEnd() && depth > 0)
            {
                var t = Peek();
                if (t.Type == TokenType.LeftParen)  depth++;
                if (t.Type == TokenType.RightParen) { depth--; if (depth == 0) { Advance(); break; } }
                if (hb.Length > 0 && t.Type != TokenType.Comma
                    && !(hb.Length > 0 && hb[hb.Length-1] == '(')
                    && t.Type != TokenType.RightParen && t.Type != TokenType.LeftParen)
                    hb.Append(' ');
                if (t.Type == TokenType.Comma) hb.Append(", ");
                else hb.Append(t.Value);
                Advance();
            }
            hint = hb.ToString().Trim();
        }

        bool isOpenQuery = nameParts.Count == 1
            && nameParts[0].Value.Equals("openquery", System.StringComparison.OrdinalIgnoreCase)
            && funcArgs != null;
        return new TableRefNode { Alias = alias, FuncArgs = funcArgs, IsOpenQuery = isOpenQuery, HintNolock = hint }
            .Tap(n => n.Name.AddRange(nameParts));
    }

    private bool IsJoinKeyword()
    {
        var t = Peek();
        return t.IsKeyword("INNER") || t.IsKeyword("LEFT") || t.IsKeyword("RIGHT")
            || t.IsKeyword("FULL") || t.IsKeyword("CROSS") || t.IsKeyword("JOIN")
            || t.IsKeyword("OUTER") // removed for LEFT/RIGHT/FULL OUTER JOIN
            || t.IsKeyword("APPLY"); // CROSS APPLY / OUTER APPLY
    }

    private JoinNode ParseJoin()
    {
        var parts = new System.Text.StringBuilder();
        while (IsJoinKeyword()) { if (parts.Length > 0) parts.Append(' '); parts.Append(Advance().Value.ToLowerInvariant()); }
        // Rule: remove OUTER only when it precedes JOIN (not APPLY)
        var jt = System.Text.RegularExpressions.Regex.Replace(parts.ToString(), @"\bouter\s+join\b", "join");
        jt = System.Text.RegularExpressions.Regex.Replace(jt, @"\s+", " ").Trim();
        if (jt == "join") jt = "inner join";
        parts.Clear(); parts.Append(jt);
        var table = ParseTableRef();
        var conditions = new List<AstNode>();
        if (Peek().IsKeyword("ON")) { Advance(); conditions.AddRange(ParseConditionList(isJoinOn: true)); }
        return new JoinNode { JoinType = parts.ToString(), Table = table }.Tap(n => n.Conditions.AddRange(conditions));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WHERE / condition lists
    // ═══════════════════════════════════════════════════════════════════════════

    private List<AstNode> ParseConditionList(bool isJoinOn)
    {
        var list = new List<AstNode>();
        string? logicalOp = null;
        while (!IsAtEnd() && !IsConditionTerminator(isJoinOn))
        {
            if (Peek().IsKeyword("AND")) { logicalOp = "and"; Advance(); continue; }
            if (Peek().IsKeyword("OR"))  { logicalOp = "or";  Advance(); continue; }
            var expr = ParseExpression();
            // Inline comment on the same line (block or line) trails this condition.
            string? condComment = TryTakeSameLineInlineComment();
            list.Add(new ConditionNode { LogicalOp = string.IsNullOrEmpty(logicalOp) ? null : logicalOp, Expression = expr, TrailingComment = condComment });
            logicalOp = null;
        }
        return list;
    }

    private bool IsConditionTerminator(bool isJoinOn)
    {
        var t = Peek();
        bool common = t.IsKeyword("GROUP") || t.IsKeyword("ORDER") || t.IsKeyword("HAVING")
            || t.IsKeyword("UNION") || t.IsKeyword("EXCEPT") || t.IsKeyword("INTERSECT")
            || t.IsKeyword("END")    // terminates inside BEGIN blocks
            || t.IsKeyword("SELECT") || t.IsKeyword("INSERT") || t.IsKeyword("UPDATE")
            || t.IsKeyword("DELETE") || t.IsKeyword("CREATE") || t.IsKeyword("DROP")
            || t.IsKeyword("BEGIN")  || t.Type == TokenType.DeclareKeyword
            || t.Type == TokenType.EndOfFile || t.Type == TokenType.RightParen
            || t.Type == TokenType.Semicolon || IsGoKeyword();
        if (isJoinOn) return common || t.IsKeyword("WHERE") || IsJoinKeyword();
        return common;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Expressions
    // ═══════════════════════════════════════════════════════════════════════════

    private AstNode ParseExpression() => ParseComparison();

    private AstNode ParseComparison()
    {
        var left = ParseAdditive();
        var op = Peek();
        if (op.Type is TokenType.Equals or TokenType.NotEquals or TokenType.LessThan
            or TokenType.GreaterThan or TokenType.LessOrEqual or TokenType.GreaterOrEqual)
        { Advance(); return new BinaryExprNode { Left = left, Op = op, Right = ParseAdditive() }; }
        if (op.IsKeyword("IS"))
        {
            Advance(); bool notNull = false;
            if (Peek().IsKeyword("NOT")) { notNull = true; Advance(); }
            Expect(TokenType.Keyword, "NULL");
            return new IsNullExprNode { Left = left, IsNotNull = notNull };
        }
        if (op.IsKeyword("NOT"))
        {
            var next = PeekAt(1);
            if (next.IsKeyword("IN"))   { Advance(); Advance(); Expect(TokenType.LeftParen);
                if (Peek().IsKeyword("SELECT")) { var sub = ParseSelectOrSet(null); Expect(TokenType.RightParen); return new InExprNode { Left = left, Negated = true, SubQuery = new SubQueryNode { Select = sub } }; }
                var v = ParseInList(); Expect(TokenType.RightParen); return new InExprNode { Left = left, Negated = true }.Tap(n => n.Values.AddRange(v)); }
            if (next.IsKeyword("LIKE")) { Advance(); Advance(); return new LikeExprNode { Left = left, Pattern = ParseAdditive() }; }
            if (next.IsKeyword("BETWEEN")) { Advance(); Advance(); var lo = ParseAdditive(); Expect(TokenType.Keyword, "AND"); var hi = ParseAdditive(); return new BetweenExprNode { Left = left, Low = lo, High = hi }; }
        }
        if (op.IsKeyword("IN"))     { Advance(); Expect(TokenType.LeftParen);
            if (Peek().IsKeyword("SELECT")) { var sub = ParseSelectOrSet(null); Expect(TokenType.RightParen); return new InExprNode { Left = left, SubQuery = new SubQueryNode { Select = sub } }; }
            var v = ParseInList(); Expect(TokenType.RightParen); return new InExprNode { Left = left }.Tap(n => n.Values.AddRange(v)); }
        if (op.IsKeyword("LIKE"))   { Advance(); return new LikeExprNode { Left = left, Pattern = ParseAdditive() }; }
        if (op.IsKeyword("BETWEEN")){ Advance(); var lo = ParseAdditive(); Expect(TokenType.Keyword, "AND"); var hi = ParseAdditive(); return new BetweenExprNode { Left = left, Low = lo, High = hi }; }
        return left;
    }

    /// <summary>Handles +, -, string concatenation.</summary>
    private AstNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (true)
        {
            var op = Peek();
            if (op.Type is not (TokenType.Plus or TokenType.Minus)) break;
            // Don't consume Minus that could start a negative number literal in a list context
            Advance();
            var right = ParseMultiplicative();
            left = new BinaryExprNode { Left = left, Op = op, Right = right };
        }
        return left;
    }

    private AstNode ParseMultiplicative()
    {
        var left = ParsePrimary();
        left = ApplyCollate(left);
        while (true)
        {
            var op = Peek();
            if (op.Type is not (TokenType.Multiply or TokenType.Divide or TokenType.Percent)) break;
            Advance();
            var right = ApplyCollate(ParsePrimary());
            left = new BinaryExprNode { Left = left, Op = op, Right = right };
        }
        return left;
    }

    /// <summary>Applies a trailing COLLATE clause to an expression: expr COLLATE Collation_Name.</summary>
    private AstNode ApplyCollate(AstNode expr)
    {
        if (Peek().IsKeyword("COLLATE"))
        {
            var op = Advance();                 // COLLATE
            var collation = Advance();           // collation name (identifier)
            return new BinaryExprNode { Left = expr, Op = op, Right = new ColumnRefNode().Tap(c => c.Parts.Add(collation)) };
        }
        return expr;
    }

    private AstNode ParsePrimary()
    {
        var tok = Peek();

        // Parenthesised expression or subquery
        if (tok.Type == TokenType.LeftParen)
        {
            Advance();
            if (Peek().IsKeyword("SELECT"))
            {
                var sub = ParseSelectOrSet(null);
                Expect(TokenType.RightParen);
                return new SubQueryNode { Select = sub };
            }
            var inner = ParseExpression();
            // If a boolean operator follows, this is a condition group: ( a = 1 or b = 2 )
            if (Peek().IsKeyword("AND") || Peek().IsKeyword("OR"))
            {
                var group = new ConditionGroupNode();
                group.Conditions.Add(new ConditionNode { Expression = inner });
                while (Peek().IsKeyword("AND") || Peek().IsKeyword("OR"))
                {
                    string op = Peek().IsKeyword("AND") ? "and" : "or";
                    Advance();
                    var next = ParseExpression();
                    group.Conditions.Add(new ConditionNode { LogicalOp = op, Expression = next });
                }
                Expect(TokenType.RightParen);
                return group;
            }
            Expect(TokenType.RightParen);
            return new ParenExprNode { Inner = inner };
        }

        if (tok.IsKeyword("CASE")) return ParseCase();
        if (IsCastFunction(tok))   return ParseCastFunction();

        // EXISTS (subquery) — must come before general function-call detection
        // because exists( would otherwise be intercepted by ParseFunctionCall()
        if (tok.IsKeyword("EXISTS"))
        {
            Advance(); // exists
            Expect(TokenType.LeftParen);
            var sub = ParseSelectOrSet(null);
            Expect(TokenType.RightParen);
            return new FunctionCallNode { Name = "EXISTS", IsKeywordFunction = true }
                .Tap(n => n.Arguments.Add(new SubQueryNode { Select = sub }));
        }

        // Function call: identifier/keyword followed by (
        if (tok.Type is TokenType.Identifier or TokenType.Keyword)
            if (PeekAt(1).Type == TokenType.LeftParen) return ParseFunctionCall();

        // Column reference / identifier chain
        if (tok.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Variable or TokenType.Keyword)
        {
            var col = new ColumnRefNode();
            col.Parts.Add(Advance());
            while (PeekIs(TokenType.Dot)) { col.Parts.Add(Advance()); col.Parts.Add(Advance()); }
            col.TrailingComment = TryTakeSameLineComment();
            return col;
        }

        // Unary minus: -1 → negative literal; -(expr) → BinaryExpr
        if (tok.Type == TokenType.Minus)
        {
            Advance();
            var next = Peek();
            if (next.Type == TokenType.NumberLiteral)
                return new LiteralNode { Token = new Token(TokenType.NumberLiteral, "-" + Advance().Value) };
            var operand = ParsePrimary();
            return new BinaryExprNode
            {
                Left  = new LiteralNode { Token = new Token(TokenType.NumberLiteral, "0") },
                Op    = tok,
                Right = operand
            };
        }

        // Literals
        if (tok.Type is TokenType.StringLiteral or TokenType.NumberLiteral)
        { var lit = new LiteralNode { Token = Advance() }; lit.TrailingComment = TryTakeSameLineComment(); return lit; }
        if (tok.Type == TokenType.BlockComment) return new LiteralNode { Token = Advance() };

        // Wildcard SELECT *
        if (tok.Type == TokenType.Multiply)
        { return new LiteralNode { Token = Advance() }; }

        return new LiteralNode { Token = Advance() };
    }

    private bool IsCastFunction(Token t) =>
        t.Value.Equals("CAST", StringComparison.OrdinalIgnoreCase) ||
        t.Value.Equals("TRY_CAST", StringComparison.OrdinalIgnoreCase) ||
        t.Value.Equals("CONVERT", StringComparison.OrdinalIgnoreCase) ||
        t.Value.Equals("TRY_CONVERT", StringComparison.OrdinalIgnoreCase);

    private FunctionCallNode ParseCastFunction()
    {
        var nameToken = Advance();
        Expect(TokenType.LeftParen);
        var args = new List<AstNode>();
        bool isConvert = nameToken.Value.Equals("CONVERT", StringComparison.OrdinalIgnoreCase)
                      || nameToken.Value.Equals("TRY_CONVERT", StringComparison.OrdinalIgnoreCase);
        if (isConvert)
        {
            args.Add(ParseDataType());
            if (PeekIs(TokenType.Comma)) Advance();
            while (!IsAtEnd() && !PeekIs(TokenType.RightParen)) { args.Add(ParseExpression()); if (PeekIs(TokenType.Comma)) Advance(); else break; }
        }
        else
        {
            args.Add(ParseExpression());
            if (Peek().IsKeyword("AS")) Advance();
            args.Add(ParseDataType());
        }
        Expect(TokenType.RightParen);
        return new FunctionCallNode { Name = nameToken.Value, IsKeywordFunction = nameToken.Type == TokenType.Keyword }.Tap(n => n.Arguments.AddRange(args));
    }

    private AstNode ParseDataType()
    {
        var parts = new ColumnRefNode();
        parts.Parts.Add(Advance());
        if (PeekIs(TokenType.LeftParen))
        {
            Advance();
            while (!IsAtEnd() && !PeekIs(TokenType.RightParen)) { parts.Parts.Add(Advance()); if (PeekIs(TokenType.Comma)) parts.Parts.Add(Advance()); }
            Expect(TokenType.RightParen);
        }
        return parts;
    }

    /// <summary>
    /// Parses an IN list, grouping values that share a trailing -- comment or leading /* */ comment.
    /// Rule 2.3.2.1: no comments → inline.
    /// Rule 2.3.2.2: one value per comment → CommentedValueNode.
    /// Rule 2.3.2.3: multiple values per comment → InValueGroupNode.
    /// </summary>
    private List<AstNode> ParseInList()
    {
        var list = new List<AstNode>();

        while (!IsAtEnd())
        {
            // Skip whitespace/newlines between groups
            while (_pos < _tokens.Count && _tokens[_pos].Type is TokenType.Whitespace or TokenType.Newline)
                _pos++;

            if (PeekIs(TokenType.RightParen)) break;

            // Leading block comment: /*ктв*/value1, value2
            string? leadingBlock = null;
            if (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.BlockComment)
            {
                leadingBlock = _tokens[_pos++].Value;
                while (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Whitespace)
                    _pos++;
            }

            if (PeekIs(TokenType.RightParen)) break;

            // Parse first value of this potential group
            var firstVal = ParsePrimary();

            // Skip inline whitespace (not newline) after the value
            while (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Whitespace)
                _pos++;

            // Trailing comment immediately after value (before comma): value --comment
            string? trailingComment = null;
            if (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.LineComment)
            {
                trailingComment = _tokens[_pos++].Value;
                // Single value with comment
                list.Add(leadingBlock != null
                    ? (AstNode)BuildInGroup(new List<AstNode> { firstVal }, leadingBlock, trailingComment)
                    : new CommentedValueNode { Value = firstVal, TrailingComment = trailingComment });
                continue;
            }

            // Is there a comma? If not → last value, no group
            if (_pos >= _tokens.Count || _tokens[_pos].Type != TokenType.Comma)
            {
                list.Add(leadingBlock != null
                    ? (AstNode)BuildInGroup(new List<AstNode> { firstVal }, leadingBlock, null)
                    : firstVal);
                continue;
            }

            // Consume comma
            _pos++;
            while (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Whitespace)
                _pos++;

            // Trailing comment after comma: value, --comment  → group ends here
            if (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.LineComment)
            {
                trailingComment = _tokens[_pos++].Value;
                list.Add(leadingBlock != null
                    ? (AstNode)BuildInGroup(new List<AstNode> { firstVal }, leadingBlock, trailingComment)
                    : new CommentedValueNode { Value = firstVal, TrailingComment = trailingComment });
                continue;
            }

            // Newline after comma without a comment → single value, no group
            if (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Newline)
            {
                list.Add(leadingBlock != null
                    ? (AstNode)BuildInGroup(new List<AstNode> { firstVal }, leadingBlock, null)
                    : firstVal);
                continue;
            }

            // More values follow → accumulate into group
            var groupVals = new List<AstNode> { firstVal };

            while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
            {
                while (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Whitespace)
                    _pos++;

                if (_pos >= _tokens.Count) break;
                if (_tokens[_pos].Type is TokenType.Newline or TokenType.LineComment or TokenType.BlockComment) break;
                if (PeekIs(TokenType.RightParen)) break;

                var nextVal = ParsePrimary();
                groupVals.Add(nextVal);

                while (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Whitespace)
                    _pos++;

                // Trailing comment after this value
                if (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.LineComment)
                {
                    trailingComment = _tokens[_pos++].Value;
                    break;
                }

                // Comma → consume and check for group boundary
                if (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Comma)
                {
                    _pos++;
                    while (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Whitespace)
                        _pos++;

                    if (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.LineComment)
                    {
                        trailingComment = _tokens[_pos++].Value;
                        break;
                    }
                    if (_pos < _tokens.Count && _tokens[_pos].Type is TokenType.Newline or TokenType.BlockComment)
                        break;
                }
                else break;
            }

            // Build the result node
            if (groupVals.Count == 1 && leadingBlock == null && trailingComment == null)
                list.Add(groupVals[0]);
            else if (groupVals.Count == 1 && leadingBlock == null)
                list.Add(new CommentedValueNode { Value = groupVals[0], TrailingComment = trailingComment });
            else
                list.Add(BuildInGroup(groupVals, leadingBlock, trailingComment));
        }

        return list;
    }

    private static InValueGroupNode BuildInGroup(List<AstNode> vals, string? leading, string? trailing)
    {
        var g = new InValueGroupNode { LeadingBlockComment = leading, TrailingLineComment = trailing };
        g.Values.AddRange(vals);
        return g;
    }

    private CaseExprNode ParseCase()
    {
        Expect(TokenType.Keyword, "CASE");
        AstNode? inputExpr = null;
        if (!Peek().IsKeyword("WHEN")) inputExpr = ParsePrimary();
        var whens = new List<WhenClauseNode>();
        AstNode? elseExpr = null; string? elseComment = null;
        while (Peek().IsKeyword("WHEN"))
        {
            Advance();
            var conds = new List<AstNode>(); conds.Add(ParseExpression());
            while (Peek().IsKeyword("AND")) { Advance(); conds.Add(ParseExpression()); }
            Expect(TokenType.Keyword, "THEN");
            var then = ParseExpression(); string? tc = null;
            if (PeekIs(TokenType.LineComment)) tc = Advance().Value;
            whens.Add(new WhenClauseNode { Then = then, ThenComment = tc }.Tap(w => w.Conditions.AddRange(conds)));
        }
        if (Peek().IsKeyword("ELSE")) { Advance(); elseExpr = ParseExpression(); if (PeekIs(TokenType.LineComment)) elseComment = Advance().Value; }
        Expect(TokenType.Keyword, "END");
        return new CaseExprNode { InputExpr = inputExpr, ElseExpr = elseExpr, ElseComment = elseComment }.Tap(n => n.WhenClauses.AddRange(whens));
    }

    private FunctionCallNode ParseFunctionCall()
    {
        var nameToken = Advance(); Advance(); // name + (
        // Optional set quantifier in aggregates: count(distinct x), sum(all y)
        string? setQuantifier = null;
        if (Peek().IsKeyword("DISTINCT")) { Advance(); setQuantifier = "distinct"; }
        else if (Peek().IsKeyword("ALL")) { Advance(); setQuantifier = "all"; }
        var args = new List<AstNode>();
        while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
        {
            // Handle OVER (...) for window functions
            if (Peek().IsKeyword("OVER")) { Advance(); args.Add(ParseWindowSpec()); continue; }
            args.Add(ParseExpression());
            if (PeekIs(TokenType.Comma)) Advance(); else break;
        }
        Expect(TokenType.RightParen);
        // Window function: OVER clause may follow the closing paren
        AstNode? overClause = null;
        if (Peek().IsKeyword("OVER")) { Advance(); overClause = ParseWindowSpec(); }
        var fn = new FunctionCallNode { Name = nameToken.Value, IsKeywordFunction = nameToken.Type == TokenType.Keyword, OverClause = overClause, SetQuantifier = setQuantifier };
        fn.Arguments.AddRange(args);
        return fn;
    }

    private AstNode ParseWindowSpec()
    {
        // OVER (PARTITION BY ... ORDER BY ...)  — emit as raw tokens for now
        Expect(TokenType.LeftParen);
        var raw = new RawTokensNode();
        int depth = 1;
        while (!IsAtEnd() && depth > 0)
        {
            var t = PeekRaw();
            if (t.Type == TokenType.LeftParen)  depth++;
            if (t.Type == TokenType.RightParen) { depth--; if (depth == 0) { AdvanceRaw(); break; } }
            raw.Tokens.Add(AdvanceRaw());
        }
        return raw;
    }

    private List<AstNode> ParseExpressionList()
    {
        var list = new List<AstNode>();
        while (!IsAtEnd() && !IsSelectClauseKeyword())
        {
            list.Add(ParseExpression());
            if (Peek().IsKeyword("ASC") || Peek().IsKeyword("DESC")) Advance();
            if (PeekIs(TokenType.Comma)) { Advance(); continue; } break;
        }
        return list;
    }

    /// <summary>Parses ORDER BY items, preserving optional ASC/DESC direction.</summary>
    private List<AstNode> ParseOrderByList()
    {
        var list = new List<AstNode>();
        while (!IsAtEnd() && !IsSelectClauseKeyword())
        {
            var expr = ParseExpression();
            string? dir = null;
            if (Peek().IsKeyword("ASC"))  { Advance(); dir = "asc";  }
            else if (Peek().IsKeyword("DESC")) { Advance(); dir = "desc"; }
            list.Add(new OrderByItemNode { Expression = expr, Direction = dir });
            if (PeekIs(TokenType.Comma)) { Advance(); continue; } break;
        }
        return list;
    }


    // ═══════════════════════════════════════════════════════════════════════════
    //  BEGIN / END  (rule 2.8)
    // ═══════════════════════════════════════════════════════════════════════════

    private BeginEndNode ParseBeginEnd()
    {
        Advance(); // BEGIN
        var node = new BeginEndNode();
        while (!IsAtEnd() && !Peek().IsKeyword("END"))
        {
            var stmt = ParseStatement();
            if (stmt != null) node.Body.Add(stmt);
        }
        if (Peek().IsKeyword("END")) Advance(); // END
        return node;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CREATE TABLE  (rule 2.14)
    // ═══════════════════════════════════════════════════════════════════════════

    private AstNode ParseCreate()
    {
        Advance(); // CREATE
        if (Peek().IsKeyword("TABLE")) return ParseCreateTable();
        // Unknown CREATE → raw fallback
        return ParseRawStatement();
    }

    private CreateTableNode ParseCreateTable()
    {
        Advance(); // TABLE
        // Collect table name (might be schema.table or #temp)
        var nameTokens = new System.Text.StringBuilder();
        while (!IsAtEnd() && !PeekIs(TokenType.LeftParen) && !IsGoKeyword()
               && Peek().Type != TokenType.EndOfFile)
        {
            nameTokens.Append(Advance().Value);
        }
        var tableName = nameTokens.ToString().Trim();
        var node = new CreateTableNode { TableName = tableName };

        if (!PeekIs(TokenType.LeftParen)) return node;
        Advance(); // outer (

        while (!IsAtEnd())
        {
            // Skip whitespace/newlines between column definitions
            while (!IsAtEnd() && _tokens[_pos].Type is TokenType.Whitespace or TokenType.Newline)
                _pos++;
            // Raw check: stop at outer )
            if (_pos >= _tokens.Count || _tokens[_pos].Type == TokenType.RightParen) break;

            // Column name (next non-whitespace token)
            var colName = Advance().Value;

            // Collect definition: everything until a DEPTH-0 comma or closing )
            // Track paren depth so varchar(255) and decimal(18,2) are parsed whole.
            var defTokens = new System.Text.StringBuilder();
            int depth = 0;
            while (!IsAtEnd())
            {
                var raw = _tokens[_pos];
                // Whitespace/newline: add a space only before non-paren, non-comma content
                if (raw.Type is TokenType.Whitespace or TokenType.Newline)
                {
                    if (depth == 0)
                    {
                        // peek what follows
                        int la = _pos + 1;
                        while (la < _tokens.Count && _tokens[la].Type is TokenType.Whitespace or TokenType.Newline) la++;
                        if (la < _tokens.Count && defTokens.Length > 0)
                        {
                            var nxt = _tokens[la].Type;
                            if (nxt != TokenType.LeftParen && nxt != TokenType.Comma
                                && nxt != TokenType.RightParen)
                                defTokens.Append(' ');
                        }
                    }
                    _pos++;
                    continue;
                }
                if (raw.Type == TokenType.LeftParen)  { depth++; defTokens.Append('('); _pos++; continue; }
                if (raw.Type == TokenType.RightParen)
                {
                    if (depth == 0) break;          // outer ) of CREATE TABLE
                    depth--;
                    defTokens.Append(')');
                    _pos++;
                    continue;
                }
                if (raw.Type == TokenType.Comma && depth == 0) break; // column separator
                // Regular token — no extra space before/after ( or )
                if (defTokens.Length > 0 && raw.Type == TokenType.Comma && depth > 0)
                    defTokens.Append(", ");         // "decimal(18, 2)" — space after comma in type
                else
                    defTokens.Append(raw.Value);
                _pos++;
            }

            node.Columns.Add(new ColumnDefNode
            {
                Name       = colName,
                Definition = defTokens.ToString().Trim()
            });

            // Consume depth-0 comma separator between columns
            if (!IsAtEnd() && _tokens[_pos].Type == TokenType.Comma) _pos++;
        }
        // Consume outer )
        if (!IsAtEnd() && _tokens[_pos].Type == TokenType.RightParen) _pos++;
        return node;
    }

    private AstNode ParseDrop()
    {
        Advance(); // DROP
        if (Peek().IsKeyword("TABLE"))
        {
            Advance(); // TABLE
            bool ifExists = false;
            if (Peek().IsKeyword("IF")) { Advance(); Expect(TokenType.Keyword, "EXISTS"); ifExists = true; }
            var nameTokens = new System.Text.StringBuilder();
            while (!IsAtEnd() && !IsGoKeyword() && Peek().Type != TokenType.EndOfFile
                   && Peek().Type != TokenType.Semicolon
                   && !Peek().IsKeyword("CREATE") && !Peek().IsKeyword("SELECT")
                   && !Peek().IsKeyword("INSERT") && !Peek().IsKeyword("DROP")
                   && !Peek().IsKeyword("BEGIN")
                   && Peek().Type != TokenType.DeclareKeyword)
                nameTokens.Append(Advance().Value);
            return new DropTableNode { IfExists = ifExists, TableName = nameTokens.ToString().Trim() };
        }
        return ParseRawStatement();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Raw fallback
    // ═══════════════════════════════════════════════════════════════════════════

    private RawTokensNode ParseRawStatement()
    {
        var raw = new RawTokensNode();
        while (!IsAtEnd())
        {
            var t = Peek();
            if (t.IsKeyword("SELECT") || t.IsKeyword("WITH") || t.IsKeyword("INSERT")
                || t.IsKeyword("UPDATE") || t.IsKeyword("DELETE")
                || t.IsKeyword("DROP")  || t.IsKeyword("CREATE") || t.IsKeyword("BEGIN")
                || t.Type == TokenType.DeclareKeyword
                || t.Type == TokenType.EndOfFile || IsGoKeyword()) break;
            raw.Tokens.Add(Advance());
        }
        return raw;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private bool IsSelectClauseKeyword()
    {
        var t = Peek();
        return t.IsKeyword("FROM") || t.IsKeyword("WHERE") || t.IsKeyword("GROUP")
            || t.IsKeyword("ORDER") || t.IsKeyword("HAVING") || t.IsKeyword("UNION")
            || t.IsKeyword("EXCEPT") || t.IsKeyword("INTERSECT") || t.IsKeyword("SELECT")
            || t.IsKeyword("END")  // terminates a column list inside BEGIN/END
            || t.Type == TokenType.EndOfFile || t.Type == TokenType.RightParen
            || t.Type == TokenType.Semicolon || IsGoKeyword();
    }

    private bool IsClauseKeyword(Token t) =>
        t.IsKeyword("FROM") || t.IsKeyword("WHERE") || t.IsKeyword("GROUP") || t.IsKeyword("ORDER")
        || t.IsKeyword("HAVING") || t.IsKeyword("UNION") || t.IsKeyword("INNER") || t.IsKeyword("LEFT")
        || t.IsKeyword("RIGHT") || t.IsKeyword("JOIN") || t.IsKeyword("ON") || t.IsKeyword("SELECT")
        || t.IsKeyword("WITH") || t.IsKeyword("EXCEPT") || t.IsKeyword("INTERSECT") || t.IsKeyword("END");

    private Token Peek()
    {
        int i = _pos;
        while (i < _tokens.Count && Skippable.Contains(_tokens[i].Type)) i++;
        return i < _tokens.Count ? _tokens[i] : new Token(TokenType.EndOfFile, string.Empty);
    }

    /// <summary>
    /// Like Peek() but also skips standalone comment tokens, so callers can detect a
    /// clause keyword (JOIN, WHERE, ...) that follows one or more comment lines.
    /// </summary>
    private Token PeekPastComments()
    {
        int i = _pos;
        while (i < _tokens.Count &&
               (Skippable.Contains(_tokens[i].Type)
                || _tokens[i].Type == TokenType.LineComment
                || _tokens[i].Type == TokenType.BlockComment)) i++;
        return i < _tokens.Count ? _tokens[i] : new Token(TokenType.EndOfFile, string.Empty);
    }

    /// <summary>Consumes and returns any standalone comments at the current position.</summary>
    private List<string> CollectStandaloneComments()
    {
        var comments = new List<string>();
        while (true)
        {
            // Skip whitespace/newlines without consuming meaningful tokens.
            while (_pos < _tokens.Count && Skippable.Contains(_tokens[_pos].Type)) _pos++;
            if (_pos < _tokens.Count &&
                (_tokens[_pos].Type == TokenType.LineComment || _tokens[_pos].Type == TokenType.BlockComment))
            {
                comments.Add(_tokens[_pos].Value);
                _pos++;
            }
            else break;
        }
        return comments;
    }

    private Token PeekAt(int offset)
    {
        int count = 0, i = _pos;
        while (i < _tokens.Count)
        {
            if (!Skippable.Contains(_tokens[i].Type)) { if (count == offset) return _tokens[i]; count++; }
            i++;
        }
        return new Token(TokenType.EndOfFile, string.Empty);
    }

    /// <summary>Counts consecutive Newline tokens immediately before the next meaningful token.</summary>
    private int CountNewlinesBackFromCurrent()
    {
        int i = _pos - 1, count = 0;
        while (i >= 0 && (_tokens[i].Type == TokenType.Newline || _tokens[i].Type == TokenType.Whitespace))
        {
            if (_tokens[i].Type == TokenType.Newline) count++;
            i--;
        }
        return count;
    }

    /// <summary>Counts consecutive Newline tokens immediately after the current position.</summary>
    private int CountNewlinesForwardFromCurrent()
    {
        int i = _pos, count = 0;
        while (i < _tokens.Count && (_tokens[i].Type == TokenType.Newline || _tokens[i].Type == TokenType.Whitespace))
        {
            if (_tokens[i].Type == TokenType.Newline) count++;
            i++;
        }
        return count;
    }

    private Token PeekRaw() => _pos < _tokens.Count ? _tokens[_pos] : new Token(TokenType.EndOfFile, string.Empty);

    /// <summary>
    /// If a line comment follows on the SAME line (only whitespace between here and it,
    /// no newline), consumes and returns it. Otherwise returns null and consumes nothing.
    /// Used so a comment on its own line is treated as standalone, not as a trailing comment.
    /// </summary>
    private string? TryTakeSameLineComment()
    {
        int i = _pos;
        while (i < _tokens.Count && _tokens[i].Type == TokenType.Whitespace) i++;
        if (i < _tokens.Count && _tokens[i].Type == TokenType.LineComment)
        {
            var val = _tokens[i].Value;
            _pos = i + 1;
            return val;
        }
        return null;
    }

    /// <summary>
    /// Like TryTakeSameLineComment but also catches inline block comments (/* ... */) that
    /// sit on the same line, e.g. "expr /* note */ and ...". Returns null if none.
    /// </summary>
    private string? TryTakeSameLineInlineComment()
    {
        int i = _pos;
        while (i < _tokens.Count && _tokens[i].Type == TokenType.Whitespace) i++;
        if (i < _tokens.Count &&
            (_tokens[i].Type == TokenType.LineComment || _tokens[i].Type == TokenType.BlockComment))
        {
            var val = _tokens[i].Value;
            _pos = i + 1;
            return val;
        }
        return null;
    }
    private bool PeekIs(TokenType type) => Peek().Type == type;

    private Token Advance()
    {
        while (_pos < _tokens.Count && Skippable.Contains(_tokens[_pos].Type)) _pos++;
        return _pos < _tokens.Count ? _tokens[_pos++] : new Token(TokenType.EndOfFile, string.Empty);
    }

    private Token AdvanceRaw() => _pos < _tokens.Count ? _tokens[_pos++] : new Token(TokenType.EndOfFile, string.Empty);

    private Token Expect(TokenType type, string? value = null)
    {
        var t = Peek();
        if (t.Type != type || (value != null && !t.Value.Equals(value, StringComparison.OrdinalIgnoreCase))) return t;
        return Advance();
    }

    private bool TryConsume(TokenType type) { if (Peek().Type != type) return false; Advance(); return true; }
    private bool IsAtEnd() => Peek().Type == TokenType.EndOfFile;
}

internal static class NodeExtensions
{
    public static T Tap<T>(this T node, Action<T> action) where T : AstNode { action(node); return node; }
}

}
