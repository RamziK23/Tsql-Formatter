using System.Collections.Generic;

namespace TsqlFormatter.Tests
{

/// <summary>
/// Golden test cases. Expected outputs reflect the agreed Code Style.
/// Use raw string-ish verbatim literals; tabs are real \t characters.
/// </summary>
public static class TestCases
{
    public static List<TestCase> All() => new List<TestCase>
    {
        // ── 1.1 keyword casing ────────────────────────────────────────────────
        new TestCase {
            Rule = "1.1", Name = "keywords lowercased, identifiers preserved",
            Input    = "SELECT Id, NAME from dbo.Users WHERE Status = 1",
            Expected = "select\n\tId,\n\tNAME\nfrom dbo.Users\nwhere\n\tStatus = 1",
        },

        // ── 2.1 select column layout ──────────────────────────────────────────
        new TestCase {
            Rule = "2.1", Name = "multiple columns, one per line",
            Input    = "select a, b, c from t",
            Expected = "select\n\ta,\n\tb,\n\tc\nfrom t",
        },
        new TestCase {
            Rule = "2.1.1", Name = "single column on its own indented line",
            Input    = "select a from t",
            Expected = "select\n\ta\nfrom t",
        },
        new TestCase {
            Rule = "2.1.1", Name = "single order by / group by item on its own line",
            Input    = "select count(*) from t group by a order by a",
            Expected = "select\n\tcount(*)\nfrom t\ngroup by\n\ta\norder by\n\ta",
        },

        new TestCase {
            Rule = "stmtbound", Name = "comment then DECLARE after assignment select is not swallowed as columns",
            Input    = "select @x = dateadd(ss, -1, @x)\n--select @x\n\ndeclare @dt varchar(255)",
            Expected = "select @x = dateadd(ss, -1, @x)\n--select @x\n\ndeclare @dt varchar(255)",
        },

        // ── programmable objects / functions ──────────────────────────────────
        new TestCase {
            Rule = "function", Name = "create function: paren on name line, params/if/return formatted, no hang",
            Input    = "create function dbo.f(@a int) returns int as begin if @a is null return 0; return @a; end go",
            Expected = "create function dbo.f (\n\t@a int\n)\nreturns int\nas\nbegin\n\n\tif @a is null\n\treturn 0\n\n\treturn @a;\n\nend\n\nGO",
        },
        new TestCase {
            Rule = "function", Name = "compound assignment += stays a single operator",
            Input    = "create procedure dbo.p as begin set @n += 1 end go",
            Expected = "create procedure dbo.p\nas\nbegin\n\n\tset @n += 1\n\nend\n\nGO",
        },
        new TestCase {
            Rule = "declare", Name = "initializer-less last variable stops at ';' (no over-consumption)",
            Input    = "declare @x int;\nselect 1 from t",
            Expected = "declare @x int;\nselect\n\t1\nfrom t",
        },

        // ── DROP TABLE trailing comment (bug 7) ──────────────────────────────
        new TestCase {
            Rule = "droptable", Name = "trailing -- comment not glued into the table name",
            Input    = "drop table if exists #aa_part\t\t--attempt to split\nselect 1 from t",
            Expected = "drop table if exists #aa_part\t\t--attempt to split\nselect\n\t1\nfrom t",
        },

        // ── SELECT ... INTO #tbl (bug 1) ──────────────────────────────────────
        new TestCase {
            Rule = "into", Name = "select ... into #tmp on its own line between columns and from",
            Input    = "select a, b into #tmp from t where x = 1",
            Expected = "select\n\ta,\n\tb\ninto #tmp\nfrom t\nwhere\n\tx = 1",
        },

        // ── 2.2 join ───────────────────────────────────────────────────────────
        new TestCase {
            Rule = "2.2", Name = "inner join, on first condition on same line",
            Input    = "select t.a, u.b from t inner join u on u.id = t.id and u.x = t.y",
            Expected = "select\n\tt.a,\n\tu.b\nfrom t\n\tinner join u\n\t\ton u.id = t.id\n\t\tand u.x = t.y",
        },

        // ── 2.5 case ───────────────────────────────────────────────────────────
        new TestCase {
            Rule = "2.5", Name = "case/when/then/else/end",
            Input    = "select case when a = 1 then 'one' when a = 2 then 'two' else 'other' end as label from t",
            Expected = "select\n\tcase\n\t\twhen a = 1\n\t\tthen 'one'\n\t\twhen a = 2\n\t\tthen 'two'\n\t\telse 'other'\n\tend as label\nfrom t",
        },

        // ── 2.11 set operations ───────────────────────────────────────────────
        new TestCase {
            Rule = "2.11", Name = "union all: blank line before and after operator",
            Input    = "select a from t1 union all select a from t2",
            Expected = "select\n\ta\nfrom t1\n\nunion all\n\nselect\n\ta\nfrom t2",
        },
        new TestCase {
            Rule = "2.11", Name = "intersect / except chain: blank line around each operator",
            Input    = "select a from t1 intersect select a from t2 except select a from t3",
            Expected = "select\n\ta\nfrom t1\n\nintersect\n\nselect\n\ta\nfrom t2\n\nexcept\n\nselect\n\ta\nfrom t3",
        },

        // ── 2.12 insert ───────────────────────────────────────────────────────
        new TestCase {
            Rule = "2.12", Name = "insert into ... values",
            Input    = "insert into dbo.t (a, b, c) values (1, 2, 3)",
            Expected = "insert into dbo.t (\n\ta,\n\tb,\n\tc\n)\nvalues\n\t(1, 2, 3)",
        },
        new TestCase {
            Rule = "2.12", Name = "insert column list only (no source), each col own line",
            Input    = "insert into #process(object_type, object_id, object_title)",
            Expected = "insert into #process (\n\tobject_type,\n\tobject_id,\n\tobject_title\n)",
        },
        new TestCase {
            Rule = "2.12", Name = "insert column list + select source",
            Input    = "insert into dbo.t (a, b) select x, y from src",
            Expected = "insert into dbo.t (\n\ta,\n\tb\n)\nselect\n\tx,\n\ty\nfrom src",
        },
        // ── bitwise operators stay inline (same precedence as + -) ─────────────
        new TestCase {
            Rule = "expr", Name = "bitwise & stays inline in where",
            Input    = "select 1 from t main where main.gr&power(2,0)=0",
            Expected = "select\n\t1\nfrom t as main\nwhere\n\tmain.gr & power(2, 0) = 0",
        },

        // ── 5.1 alias via = → as ──────────────────────────────────────────────
        new TestCase {
            Rule = "5.1", Name = "alias = rewritten to as",
            Input    = "select [Name] = t.col1, cnt = count(*) from t",
            Expected = "select\n\tt.col1 as [Name],\n\tcount(*) as cnt\nfrom t",
        },

        // ── DECLARE ────────────────────────────────────────────────────────────
        // NOTE: expected uses varchar(50) WITHOUT spaces — rule 2.15 (function/type
        // parens). Currently the formatter emits "varchar ( 50 )"; this test should
        // FAIL until 2.15 is implemented, then pass.
        new TestCase {
            Rule = "2.15", Name = "declare: type parens have no inner spaces",
            Input    = "declare @a int, @b varchar(50)",
            Expected = "declare @a int,\n\t@b varchar(50)",
        },
        new TestCase {
            Rule = "2.15", Name = "declare: decimal(18, 2) spacing",
            Input    = "declare @m decimal(18,2)",
            Expected = "declare @m decimal(18, 2)",
        },
        new TestCase {
            Rule = "2.15", Name = "window function: over on its own line, items one per line",
            Input    = "select row_number() over (partition by dep order by sal desc) as rn from emp",
            Expected = "select\n\trow_number()\n\t\tover (\n\t\t\tpartition by\n\t\t\t\tdep\n\t\t\torder by\n\t\t\t\tsal desc\n\t\t) as rn\nfrom emp",
        },
        new TestCase {
            Rule = "window", Name = "over: uppercase ORDER BY and dotted column lowercased/tightened",
            Input    = "select row_number() OVER (ORDER BY a.[Id]) as [Rn] from [dbo].[A] a",
            Expected = "select\n\trow_number()\n\t\tover (\n\t\t\torder by\n\t\t\t\ta.[Id]\n\t\t) as [Rn]\nfrom [dbo].[A] as a",
        },
        new TestCase {
            Rule = "window", Name = "over: partition by + order by desc, uppercase and dotted",
            Input    = "select sum(x) OVER (PARTITION BY a.[G] ORDER BY a.[D] DESC) as s from t",
            Expected = "select\n\tsum(x)\n\t\tover (\n\t\t\tpartition by\n\t\t\t\ta.[G]\n\t\t\torder by\n\t\t\t\ta.[D] desc\n\t\t) as s\nfrom t",
        },
        new TestCase {
            Rule = "window", Name = "over: multiple partition/order columns keep comma spacing",
            Input    = "select count(*) OVER (PARTITION BY a.[G1], a.[G2] ORDER BY a.[D1], a.[D2]) as c from t",
            Expected = "select\n\tcount(*)\n\t\tover (\n\t\t\tpartition by\n\t\t\t\ta.[G1],\n\t\t\t\ta.[G2]\n\t\t\torder by\n\t\t\t\ta.[D1],\n\t\t\t\ta.[D2]\n\t\t) as c\nfrom t",
        },

        // ── convert/cast keep type parens; dotted function calls; stmt boundaries ─
        new TestCase {
            Rule = "convert", Name = "convert keeps varchar(n) parens",
            Input    = "select convert(varchar(10), id) as c from t",
            Expected = "select\n\tconvert(varchar(10), id) as c\nfrom t",
        },
        new TestCase {
            Rule = "convert", Name = "cast keeps type parens",
            Input    = "select cast(x as varchar(20)) as c from t",
            Expected = "select\n\tcast(x as varchar(20)) as c\nfrom t",
        },
        new TestCase {
            Rule = "dottedfn", Name = "multi-part function name with args",
            Input    = "select webcar.dbo.datafirst(@yy, @mm) as d from t",
            Expected = "select\n\twebcar.dbo.datafirst(@yy, @mm) as d\nfrom t",
        },
        new TestCase {
            Rule = "stmtbound", Name = "assignment select column list ends at DECLARE",
            Input    = "select @yy=2026,@mm=6\ndeclare @z int",
            Expected = "select @yy = 2026,\n\t@mm = 6\ndeclare @z int",
        },
        new TestCase {
            Rule = "stmtbound", Name = "assignment select column list ends at EXEC",
            Input    = "select @x = 1\nexec('do_something')",
            Expected = "select @x = 1\nexec('do_something')",
        },
        new TestCase {
            Rule = "2.15", Name = "function call: no space before (",
            Input    = "select count(*), max(sal), substring(name, 1, 3) from emp",
            Expected = "select\n\tcount(*),\n\tmax(sal),\n\tsubstring(name, 1, 3)\nfrom emp",
        },

        // ── CROSS APPLY with subquery ─────────────────────────────────────────
        new TestCase {
            Rule = "apply", Name = "cross apply subquery",
            Input    = "select p.maker, l.* from Product as p cross apply (select * from Laptop as L where p.model = L.model) as l",
            Expected = "select\n\tp.maker,\n\tl.*\nfrom Product as p\n\tcross apply (\n\t\tselect\n\t\t\t*\n\t\tfrom Laptop as L\n\t\twhere\n\t\t\tp.model = L.model\n\t) as l",
        },

        // ── EXISTS subquery (regression for the recent bug) ───────────────────
        new TestCase {
            Rule = "exists", Name = "exists subquery inside iif",
            Input    = "select max(iif(exists (select * from t as x where x.id = w.id), 1, 0)) as has_it from w",
            Expected = "select\n\tmax(\n\t\tiif(\n\t\t\texists (\n\t\t\t\tselect\n\t\t\t\t\t*\n\t\t\t\tfrom t as x\n\t\t\t\twhere\n\t\t\t\t\tx.id = w.id\n\t\t\t),\n\t\t\t1,\n\t\t\t0\n\t\t)\n\t) as has_it\nfrom w",
        },

        // ── 4.1.2 trailing comments ───────────────────────────────────────────
        new TestCase {
            Rule = "4.1.2", Name = "column trailing comments, comma before comment",
            Input    = "select a, --первая колонка\nb --вторая\nfrom t",
            Expected = "select\n\ta,\t\t--первая колонка\n\tb\t\t--вторая\nfrom t",
        },
        new TestCase {
            Rule = "4.1.2", Name = "comma before trailing -- comment on a non-last column",
            Input    = "select a, b.c\t\t--note about c\n, d\nfrom t",
            Expected = "select\n\ta,\n\tb.c,\t\t--note about c\n\td\nfrom t",
        },
        new TestCase {
            Rule = "4.1.2", Name = "trailing comment on where",
            Input    = "select a from t where x = 1 --фильтр",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx = 1\t\t--фильтр",
        },

        // ── bug 2: line comments in the column list stay on their own line ────
        new TestCase {
            Rule = "parse-safety", Name = "commented-out top stays on the select line, real columns kept",
            Input    = "select --top (@top)\n w.cid as objectId,\n--  '' as comment,\n ac.title as city_title\nfrom t",
            Expected = "select\t\t--top (@top)\n\tw.cid as objectId,\n\t--  '' as comment,\n\tac.title as city_title\nfrom t",
        },

        // ── bug 0/3: comment before a subquery SELECT must not desync the parser ─
        new TestCase {
            Rule = "parse-safety", Name = "same-line comment after ( stays on the exists ( line",
            Input    = "select a from t where exists (\t--note\n select 1 from u where u.x = 1)",
            Expected = "select\n\ta\nfrom t\nwhere\n\texists (\t\t--note\n\t\tselect\n\t\t\t1\n\t\tfrom u\n\t\twhere\n\t\t\tu.x = 1\n\t)",
        },

        new TestCase {
            Rule = "parse-safety", Name = "comment before subquery select inside IN (...)",
            Input    = "select a from t where id in (\n\t--only active\n\tselect id from u where u.x = 1\n)",
            Expected = "select\n\ta\nfrom t\nwhere\n\tid in (\n\t\t--only active\n\t\tselect\n\t\t\tid\n\t\tfrom u\n\t\twhere\n\t\t\tu.x = 1\n\t)",
        },

        new TestCase {
            Rule = "option", Name = "OPTION (recompile) is a trailing clause, not a WHERE condition",
            Input    = "select a from t where x = 1 option (recompile)\n--next statement comment\nselect 2 from u",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx = 1\noption(recompile)\n--next statement comment\nselect\n\t2\nfrom u",
        },

        new TestCase {
            Rule = "not-exists", Name = "NOT EXISTS is a single condition, not split",
            Input    = "select a from t where w.fc = 0 and not exists (\n\tselect 1 from u where u.id = t.id\n) and x = 1",
            Expected = "select\n\ta\nfrom t\nwhere\n\tw.fc = 0\n\tand not exists (\n\t\tselect\n\t\t\t1\n\t\tfrom u\n\t\twhere\n\t\t\tu.id = t.id\n\t)\n\tand x = 1",
        },
        new TestCase {
            Rule = "not-exists", Name = "prefix NOT (group) is a single condition",
            Input    = "select a from t where not (x = 1 or y = 2)",
            Expected = "select\n\ta\nfrom t\nwhere\n\tnot (\n\t\tx = 1\n\t\tor y = 2\n\t)",
        },

        // ── 2.6 OR grouping ───────────────────────────────────────────────────
        new TestCase {
            Rule = "2.6", Name = "atomic OR (2.6.4)",
            Input    = "select * from t where a = 1 or b = 2",
            Expected = "select\n\t*\nfrom t\nwhere\n\ta = 1\n\tor b = 2",
        },

        // ── 7. OPENQUERY (linked server) ──────────────────────────────────────
        new TestCase {
            Rule = "7", Name = "openquery basic: server + remote sql on own lines",
            Input    = "select w.* from openquery(api_ufaner_utf, 'select id, title from cabinet_user') as w",
            Expected = "select\n\tw.*\nfrom openquery(\n\tapi_ufaner_utf,\n\t'select id, title from cabinet_user'\n) as w",
        },
        new TestCase {
            Rule = "7", Name = "openquery inside join keeps indentation",
            Input    = "select a.id, w.title from local_table as a inner join openquery(srv, 'select id, title from remote') as w on w.id = a.id",
            Expected = "select\n\ta.id,\n\tw.title\nfrom local_table as a\n\tinner join openquery(\n\t\tsrv,\n\t\t'select id, title from remote'\n\t) as w\n\t\ton w.id = a.id",
        },

        // ── 2.13 dynamic SQL string literal ───────────────────────────────────
        new TestCase {
            Rule = "blockcmt", Name = "block comment glued to last group by item stays put",
            Input    = "select a from t group by a, b/*, c*/",
            Expected = "select\n\ta\nfrom t\ngroup by\n\ta,\n\tb/*, c*/",
        },
        new TestCase {
            Rule = "2.13", Name = "concatenated dynamic sql fragments emitted verbatim",
            Input    = "declare @x varchar(max) = '\n\tline1\n' + @p + '\n\tline2\n'",
            Expected = "declare @x varchar(max) = '\n\tline1\n' + @p + '\n\tline2\n'",
        },
        new TestCase {
            Rule = "2.13", Name = "multiline dynamic sql string emitted verbatim (no reindent)",
            Input    = "declare @s varchar(max) = ''\nselect @s = @s + '\n    select\n        id,\n        title\n    from '+b.dbName+'.dbo.contract'+b.suffix+'\n' from webcar.dbo.billing as b",
            Expected = "declare @s varchar(max) = ''\nselect @s = @s + '\n    select\n        id,\n        title\n    from ' + b.dbName + '.dbo.contract' + b.suffix + '\n'\nfrom webcar.dbo.billing as b",
        },

        // ── 2.4 subquery in IN ────────────────────────────────────────────────
        new TestCase {
            Rule = "2.4", Name = "in (subquery)",
            Input    = "select * from t where id in (select id from u where u.x = 1)",
            Expected = "select\n\t*\nfrom t\nwhere\n\tid in (\n\t\tselect\n\t\t\tid\n\t\tfrom u\n\t\twhere\n\t\t\tu.x = 1\n\t)",
        },
        new TestCase {
            Rule = "2.4", Name = "not in (subquery)",
            Input    = "select * from t where id not in (select id from u)",
            Expected = "select\n\t*\nfrom t\nwhere\n\tid not in (\n\t\tselect\n\t\t\tid\n\t\tfrom u\n\t)",
        },

        // ── 2.2 join normalization (detailed) ─────────────────────────────────
        new TestCase {
            Rule = "2.2", Name = "left outer join -> left join",
            Input    = "select a.id from t1 as a left outer join t2 as b on b.id = a.id",
            Expected = "select\n\ta.id\nfrom t1 as a\n\tleft join t2 as b\n\t\ton b.id = a.id",
        },
        new TestCase {
            Rule = "2.2", Name = "bare join -> inner join",
            Input    = "select a.id from t1 as a join t2 as b on b.id = a.id",
            Expected = "select\n\ta.id\nfrom t1 as a\n\tinner join t2 as b\n\t\ton b.id = a.id",
        },

        // ── 2.5 nested case ───────────────────────────────────────────────────
        new TestCase {
            Rule = "2.5", Name = "nested case in then",
            Input    = "select case when a = 1 then case when b = 2 then 'x' else 'y' end else 'z' end from t",
            Expected = "select\n\tcase\n\t\twhen a = 1\n\t\tthen case\n\t\t\twhen b = 2\n\t\t\tthen 'x'\n\t\t\telse 'y'\n\t\tend\n\t\telse 'z'\n\tend\nfrom t",
        },
        new TestCase {
            Rule = "2.5", Name = "case when with AND: lowercase 'and' starts continuation line",
            Input    = "select case when a is null and b is null then 'x' else 'y' end from t",
            Expected = "select\n\tcase\n\t\twhen a is null\n\t\t\tand b is null\n\t\tthen 'x'\n\t\telse 'y'\n\tend\nfrom t",
        },
        // ── having: keyword on own line, condition indented like where ─────────
        new TestCase {
            Rule = "having", Name = "having condition on its own indented line",
            Input    = "select a, count(*) from t group by a having count(*) between 1 and 10",
            Expected = "select\n\ta,\n\tcount(*)\nfrom t\ngroup by\n\ta\nhaving\n\tcount(*) between 1 and 10",
        },

        // ── 2.7 update ... set ... from (detailed) ────────────────────────────
        new TestCase {
            Rule = "2.7", Name = "update set from join, set lowercased",
            Input    = "update w set w.a = t.a, w.b = t.b from t1 as w inner join t2 as t on t.id = w.id",
            Expected = "update w\nset\n\tw.a = t.a,\n\tw.b = t.b\nfrom t1 as w\n\tinner join t2 as t\n\t\ton t.id = w.id",
        },

        // ── CTE (with ... as) ─────────────────────────────────────────────────
        new TestCase {
            Rule = "cte", Name = "single cte: opening paren on as line",
            Input    = "WITH cte AS (SELECT s.[Id], s.[Val] FROM [dbo].[Src] s WHERE s.[Val] > 0)\nSELECT c.[Id] FROM cte c",
            Expected = "with cte as (\n\tselect\n\t\ts.[Id],\n\t\ts.[Val]\n\tfrom [dbo].[Src] as s\n\twhere\n\t\ts.[Val] > 0\n)\nselect\n\tc.[Id]\nfrom cte as c",
        },
        // ── leading block comment on columns ──────────────────────────────────
        new TestCase {
            Rule = "blockcmt", Name = "block comment in IN list is transparent, list stays inline",
            Input    = "select * from a where id in (1, 2, 3, 4, 5, 6/*7,8,9,0*/, 1, 2, 3, 4)",
            Expected = "select\n\t*\nfrom a\nwhere\n\tid in (1, 2, 3, 4, 5, 6/*7,8,9,0*/, 1, 2, 3, 4)",
        },
        new TestCase {
            Rule = "blockcmt", Name = "block comment between columns trails the preceding column, no tab",
            Input    = "SELECT 'SELECT a.[x] FROM t WHERE a.[y] = 1' AS [sql_text], /* [Alias] = expr, INNER JOIN, declare @a int */ c.[id]\nFROM [dbo].[C] c",
            Expected = "select\n\t'SELECT a.[x] FROM t WHERE a.[y] = 1' as [sql_text],/* [Alias] = expr, INNER JOIN, declare @a int */\n\tc.[id]\nfrom [dbo].[C] as c",
        },
        new TestCase {
            Rule = "2.8", Name = "begin/end with declare and select, no extra end column",
            Input    = "BEGIN\nDECLARE @a INT = 1, @b INT = 2\nSELECT @a, @b\nEND",
            Expected = "begin\n\n\tdeclare @a int = 1,\n\t\t@b int = 2\n\n\tselect\n\t\t@a,\n\t\t@b\n\nend",
        },

        // ── fragments (partial selections) ────────────────────────────────────
        new TestCase {
            Rule = "fragment", Name = "bare WHERE clause",
            Input    = "WHERE a.id = 1 AND b.x > 0",
            Expected = "where\n\ta.id = 1\n\tand b.x > 0",
        },
        new TestCase {
            Rule = "fragment", Name = "bare WHERE single condition on its own line",
            Input    = "where x = 1",
            Expected = "where\n\tx = 1",
        },
        new TestCase {
            Rule = "fragment", Name = "bare WHERE with OR grouping",
            Input    = "where a = 1 or b = 2",
            Expected = "where\n\ta = 1\n\tor b = 2",
        },
        new TestCase {
            Rule = "fragment", Name = "bare conditions without where keyword",
            Input    = "a = 1 and b = 2 and c = 3",
            Expected = "where\n\ta = 1\n\tand b = 2\n\tand c = 3",
        },
        new TestCase {
            Rule = "fragment", Name = "bare column list",
            Input    = "a.id, b.name, count(*) as cnt",
            Expected = "a.id,\nb.name,\ncount(*) as cnt",
        },
        new TestCase {
            Rule = "fragment", Name = "bare JOIN chain",
            Input    = "inner join t2 as b on b.id = a.id and b.x = 1",
            Expected = "\tinner join t2 as b\n\t\ton b.id = a.id\n\t\tand b.x = 1",
        },
        // ── newly covered scenarios ───────────────────────────────────────────
        new TestCase {
            Rule = "top", Name = "top (n) with ties",
            Input    = "select top (10) with ties a from t order by a",
            Expected = "select top (10) with ties\n\ta\nfrom t\norder by\n\ta",
        },
        new TestCase {
            Rule = "top", Name = "top n percent",
            Input    = "select top 5 percent a from t",
            Expected = "select top 5 percent\n\ta\nfrom t",
        },
        new TestCase {
            Rule = "top", Name = "distinct top n",
            Input    = "select distinct top 10 a from t",
            Expected = "select distinct top 10\n\ta\nfrom t",
        },
        new TestCase {
            Rule = "top", Name = "comment between select and top does not derail the statement",
            Input    = "select /*top 10*/ top (@top) a as x, b as y from t",
            Expected = "/*top 10*/\nselect top (@top)\n\ta as x,\n\tb as y\nfrom t",
        },
        new TestCase {
            Rule = "hint", Name = "with (nolock) on table",
            Input    = "select a from t with (nolock) where x = 1",
            Expected = "select\n\ta\nfrom t with (nolock)\nwhere\n\tx = 1",
        },
        new TestCase {
            Rule = "hint", Name = "with (nolock) in join",
            Input    = "select a.id from t1 as a inner join t2 as b with (nolock) on b.id = a.id",
            Expected = "select\n\ta.id\nfrom t1 as a\n\tinner join t2 as b with (nolock)\n\t\ton b.id = a.id",
        },
        new TestCase {
            Rule = "paren", Name = "nested parentheses preserved (semantics)",
            Input    = "select ((a + b) * (c - d)) as x from t",
            Expected = "select\n\t((a + b) * (c - d)) as x\nfrom t",
        },
        new TestCase {
            Rule = "orderby", Name = "order by asc/desc preserved",
            Input    = "select a from t order by a desc, b asc",
            Expected = "select\n\ta\nfrom t\norder by\n\ta desc,\n\tb asc",
        },
        new TestCase {
            Rule = "isnull", Name = "is null / is not null",
            Input    = "select a from t where b is null and c is not null",
            Expected = "select\n\ta\nfrom t\nwhere\n\tb is null\n\tand c is not null",
        },
        new TestCase {
            Rule = "derived", Name = "derived table subquery in from",
            Input    = "select x.a from (select a from t where b = 1) as x",
            Expected = "select\n\tx.a\nfrom (\n\tselect\n\t\ta\n\tfrom t\n\twhere\n\t\tb = 1\n) as x",
        },
        new TestCase {
            Rule = "simplecase", Name = "simple case (case x when)",
            Input    = "select case status when 1 then 'a' when 2 then 'b' else 'c' end from t",
            Expected = "select\n\tcase status\n\t\twhen 1\n\t\tthen 'a'\n\t\twhen 2\n\t\tthen 'b'\n\t\telse 'c'\n\tend\nfrom t",
        },

        // ── standalone comments between clauses (smoke regressions) ────────────
        new TestCase {
            Rule = "comments", Name = "standalone comment between from and join",
            Input    = "select a\nfrom t1 s\n-- note\ninner join t2 e on e.id = s.id",
            Expected = "select\n\ta\nfrom t1 as s\n\t-- note\n\tinner join t2 as e\n\t\ton e.id = s.id",
        },
        new TestCase {
            Rule = "comments", Name = "declare trailing comment after comma",
            Input    = "declare @a int = 1, --note\n@b int = 2",
            Expected = "declare @a int = 1,\t\t--note\n\t@b int = 2",
        },
        new TestCase {
            Rule = "condgroup", Name = "parenthesized condition group with and/or",
            Input    = "select a from t where x = 1 and (y = 2 or z = 3) or w = 4",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx = 1\n\tand (\n\t\ty = 2\n\t\tor z = 3\n\t)\n\tor w = 4",
        },
        new TestCase {
            Rule = "casewt", Name = "when and then on separate lines",
            Input    = "select case when a > 1 then 'high' else 'low' end from t",
            Expected = "select\n\tcase\n\t\twhen a > 1\n\t\tthen 'high'\n\t\telse 'low'\n\tend\nfrom t",
        },
        new TestCase {
            Rule = "comments", Name = "no blank line between standalone comment and statement",
            Input    = "-- header\nselect a from t",
            Expected = "-- header\nselect\n\ta\nfrom t",
        },
        new TestCase {
            Rule = "comments", Name = "trailing -- comment stays on its original line, not moved to next statement",
            Input    = "select a from t1  -- comment about t1\nselect b from t2",
            Expected = "select\n\ta\nfrom t1\t\t-- comment about t1\nselect\n\tb\nfrom t2",
        },
        new TestCase {
            Rule = "comments", Name = "trailing -- comment on last statement kept on its line",
            Input    = "select a from t1  -- trailing note",
            Expected = "select\n\ta\nfrom t1\t\t-- trailing note",
        },
        new TestCase {
            Rule = "comments", Name = "comment after GO attaches to next statement",
            Input    = "select 1\nGO\n-- note\nselect 2 from t",
            Expected = "select\n\t1\n\nGO\n\n-- note\nselect\n\t2\nfrom t",
        },
        new TestCase {
            Rule = "go", Name = "consecutive GO separators are preserved (not collapsed)",
            Input    = "select 1\nGO\nGO\nselect 2 from t",
            Expected = "select\n\t1\n\nGO\n\nGO\n\nselect\n\t2\nfrom t",
        },
        new TestCase {
            Rule = "go", Name = "consecutive trailing GO preserved",
            Input    = "select 1 from t\nGO\nGO",
            Expected = "select\n\t1\nfrom t\n\nGO\n\nGO",
        },
        new TestCase {
            Rule = "where-or", Name = "WHERE with OR does not add outer parens",
            Input    = "select a from t where x = 1 or y = 2",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx = 1\n\tor y = 2",
        },
        new TestCase {
            Rule = "where-or", Name = "WHERE keeps original inner group, no outer parens",
            Input    = "select a from t where x = 1 and (b = 1 or b = 2) or y > 0",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx = 1\n\tand (\n\t\tb = 1\n\t\tor b = 2\n\t)\n\tor y > 0",
        },
        new TestCase {
            Rule = "blanklines", Name = "blank line after comment preserved",
            Input    = "-- comment\n\nselect 2 from t",
            Expected = "-- comment\n\nselect\n\t2\nfrom t",
        },
        new TestCase {
            Rule = "blanklines", Name = "no blank line after comment when absent",
            Input    = "-- comment\nselect 2 from t",
            Expected = "-- comment\nselect\n\t2\nfrom t",
        },

        // ── inline comments inside conditions ─────────────────────────────────
        new TestCase {
            Rule = "inline-cmt", Name = "inline block comment between condition and and",
            Input    = "select a from t where name = 'a and b' /* and here */ and z = 1",
            Expected = "select\n\ta\nfrom t\nwhere\n\tname = 'a and b' /* and here */\n\tand z = 1",
        },
        new TestCase {
            Rule = "inline-cmt", Name = "inline comment on single where condition",
            Input    = "select a from t where x = 1 /* only */",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx = 1 /* only */",
        },
        new TestCase {
            Rule = "inline-cmt", Name = "inline comment in join on",
            Input    = "select a from t1 s inner join t2 e on e.id = s.id /* join note */ and e.x = 1",
            Expected = "select\n\ta\nfrom t1 as s\n\tinner join t2 as e\n\t\ton e.id = s.id /* join note */\n\t\tand e.x = 1",
        },

        // ── GROUP BY / ORDER BY fragments ─────────────────────────────────────
        new TestCase {
            Rule = "fragment", Name = "bare group by list",
            Input    = "group by s.[BusinessEntityID], p.[Title], p.[FirstName]",
            Expected = "group by\n\ts.[BusinessEntityID],\n\tp.[Title],\n\tp.[FirstName]",
        },
        new TestCase {
            Rule = "fragment", Name = "bare group by single column",
            Input    = "group by s.[a]",
            Expected = "group by\n\ts.[a]",
        },
        new TestCase {
            Rule = "fragment", Name = "bare order by with directions",
            Input    = "order by a desc, b asc, c",
            Expected = "order by\n\ta desc,\n\tb asc,\n\tc",
        },

        // ── declare + comment edge cases (тест2/тест3) ────────────────────────
        new TestCase {
            Rule = "declare", Name = "next-line comment after declare stays standalone (bug 6)",
            Input    = "declare @a date = getdate()\n--next line comment\nselect 1 from t",
            Expected = "declare @a date = getdate()\n--next line comment\nselect\n\t1\nfrom t",
        },
        new TestCase {
            Rule = "declare", Name = "single variable stays on the declare line",
            Input    = "declare @be int = 1",
            Expected = "declare @be int = 1",
        },
        new TestCase {
            Rule = "declare", Name = "comment glued to value (1--test2)",
            Input    = "declare @be int = 1--test2",
            Expected = "declare @be int = 1\t\t--test2",
        },
        new TestCase {
            Rule = "declare", Name = "standalone comment before declare",
            Input    = "--test\ndeclare @be int = 1",
            Expected = "--test\ndeclare @be int = 1",
        },
        new TestCase {
            Rule = "declare", Name = "exponent literal not broken by number lexing",
            Input    = "select 1e-5 as x",
            Expected = "select\n\t1e-5 as x",
        },
        new TestCase {
            Rule = "begincomment", Name = "comment attaches to declare inside begin/end",
            Input    = "begin\n\n--test\ndeclare @be int = 1--test2\n\ndeclare @b2e int = 1,\n@dt varchar(255) = ''\n\nend",
            Expected = "begin\n\n\t--test\n\tdeclare @be int = 1\t\t--test2\n\n\tdeclare @b2e int = 1,\n\t\t@dt varchar(255) = ''\n\nend",
        },
        new TestCase {
            Rule = "declarecomment", Name = "leading comment + declare with glued trailing comment (top level)",
            Input    = "--test\ndeclare @be int = 1--test2\n\ndeclare @b2e int = 1,\n@dt varchar(255) = ''",
            Expected = "--test\ndeclare @be int = 1\t\t--test2\n\ndeclare @b2e int = 1,\n\t@dt varchar(255) = ''",
        },
        new TestCase {
            Rule = "nstring", Name = "N-prefixed unicode string literals kept intact",
            Input    = "select @city=N'***',@utm=N'***',@report_sale_user=0,@process_status_id=-1,@source_title=N'***'",
            Expected = "select @city = N'***',\n\t@utm = N'***',\n\t@report_sale_user = 0,\n\t@process_status_id = -1,\n\t@source_title = N'***'",
        },
        new TestCase {
            Rule = "nstring", Name = "N identifier (not a string) untouched",
            Input    = "select N.id from Nodes N",
            Expected = "select\n\tN.id\nfrom Nodes as N",
        },
        new TestCase {
            Rule = "nstring", Name = "N-string with escaped quote",
            Input    = "select N'it''s ok' as x",
            Expected = "select\n\tN'it''s ok' as x",
        },

        // ── ANSI double-quoted identifiers ────────────────────────────────────
        new TestCase {
            Rule = "dquote", Name = "double-quoted identifiers kept intact",
            Input    = "select \"core_contract\".\"id\", \"auth_user\".\"username\" from \"core_contract\"",
            Expected = "select\n\t\"core_contract\".\"id\",\n\t\"auth_user\".\"username\"\nfrom \"core_contract\"",
        },
        new TestCase {
            Rule = "dquote", Name = "double-quoted with join and where",
            Input    = "select \"a\".\"id\" from \"t1\" a inner join \"t2\" b on (\"a\".\"id\" = \"b\".\"aid\") where \"a\".\"x\" = 1",
            Expected = "select\n\t\"a\".\"id\"\nfrom \"t1\" as a\n\tinner join \"t2\" as b\n\t\ton (\"a\".\"id\" = \"b\".\"aid\")\nwhere\n\t\"a\".\"x\" = 1",
        },
        new TestCase {
            Rule = "dquote", Name = "escaped double-quote inside identifier",
            Input    = "select \"a\"\"b\" from t",
            Expected = "select\n\t\"a\"\"b\"\nfrom t",
        },
        new TestCase {
            Rule = "dquote", Name = "single-quote string with double-quotes inside stays a string",
            Input    = "select 'text with \"quotes\" inside' as x from t",
            Expected = "select\n\t'text with \"quotes\" inside' as x\nfrom t",
        },

        // ── COLLATE and CASE with arithmetic in THEN/ELSE ─────────────────────
        new TestCase {
            Rule = "collate", Name = "collate clause on expression",
            Input    = "select name collate Latin1_General_Bin2 as x from t",
            Expected = "select\n\tname collate Latin1_General_Bin2 as x\nfrom t",
        },
        new TestCase {
            Rule = "case-arith", Name = "case else with arithmetic after parens",
            Input    = "select case when @e = -1 then 2147483647 else ((@a - @b)/2) + 1 end as x from t",
            Expected = "select\n\tcase\n\t\twhen @e = -1\n\t\tthen 2147483647\n\t\telse ((@a - @b) / 2) + 1\n\tend as x\nfrom t",
        },
        new TestCase {
            Rule = "case-arith", Name = "case then with arithmetic",
            Input    = "select case when @a = 1 then @x + @y * 2 else 0 end as z from t",
            Expected = "select\n\tcase\n\t\twhen @a = 1\n\t\tthen @x + @y * 2\n\t\telse 0\n\tend as z\nfrom t",
        },

        // ── function argument line-breaking (multiline args) ──────────────────
        new TestCase {
            Rule = "fnbreak", Name = "short function call stays inline",
            Input    = "select substring(est.text, 1, 5), nchar(31) from t",
            Expected = "select\n\tsubstring(est.text, 1, 5),\n\tnchar(31)\nfrom t",
        },
        new TestCase {
            Rule = "fnbreak", Name = "function with case arg breaks onto lines",
            Input    = "select replace(case when a=1 then 'x' else 'y' end, 'p', 'q') as z from t",
            Expected = "select\n\treplace(\n\t\tcase\n\t\t\twhen a = 1\n\t\t\tthen 'x'\n\t\t\telse 'y'\n\t\tend,\n\t\t'p',\n\t\t'q'\n\t) as z\nfrom t",
        },
        new TestCase {
            Rule = "fnbreak", Name = "function with subquery arg breaks onto lines",
            Input    = "select coalesce((select max(x) from u), 0) as m from t",
            Expected = "select\n\tcoalesce(\n\t\t(\n\t\t\tselect\n\t\t\t\tmax(x)\n\t\t\tfrom u\n\t\t),\n\t\t0\n\t) as m\nfrom t",
        },

        // ── DISTINCT / ALL inside aggregate functions ─────────────────────────
        new TestCase {
            Rule = "aggdistinct", Name = "count distinct simple column inline",
            Input    = "select count(distinct call_id) as c from t",
            Expected = "select\n\tcount(distinct call_id) as c\nfrom t",
        },
        new TestCase {
            Rule = "aggdistinct", Name = "count distinct case breaks onto lines",
            Input    = "select count(distinct case when x is not null then id end) as c from t",
            Expected = "select\n\tcount(\n\t\tdistinct case\n\t\t\twhen x is not null\n\t\t\tthen id\n\t\tend\n\t) as c\nfrom t",
        },

        // ── blank-line preservation between statements ────────────────────────
        new TestCase {
            Rule = "blanklines", Name = "no blank line added between statements when source has none",
            Input    = "select 1 from t\nselect 2 from t",
            Expected = "select\n\t1\nfrom t\nselect\n\t2\nfrom t",
        },
        new TestCase {
            Rule = "blanklines", Name = "blank line between statements preserved when present in source",
            Input    = "select 1 from t\n\nselect 2 from t",
            Expected = "select\n\t1\nfrom t\n\nselect\n\t2\nfrom t",
        },

        // ── CREATE TABLE: same-line paren, lowercased types ───────────────────
        new TestCase {
            Rule = "2.14", Name = "create table: opening paren on name line, types lowercased",
            Input    = "CREATE TABLE #x(\n\tprocess INT,\n\tprocess_status_id INT\n)",
            Expected = "create table #x (\n\tprocess int,\n\tprocess_status_id int\n)",
        },
        new TestCase {
            Rule = "2.14", Name = "if object_id drop/create: condition on the if line, lowercased",
            Input    = "IF OBJECT_ID('TempDb..#x') is not null DROP TABLE #x\nCREATE TABLE #x(\n\tprocess INT,\n\tprocess_status_id INT\n)",
            Expected = "if object_id('TempDb..#x') is not null\ndrop table #x\ncreate table #x (\n\tprocess int,\n\tprocess_status_id int\n)",
        },

        // ── transactions: BEGIN TRAN is a statement, not a BEGIN...END block ──
        new TestCase {
            Rule = "tran", Name = "begin tran / commit are statements, no fabricated end",
            Input    = "begin tran\nupdate t set x = 1 where id = 5\ncommit",
            Expected = "begin tran\nupdate t\nset\n\tx = 1\nwhere\n\tid = 5\ncommit",
        },
        new TestCase {
            Rule = "tran", Name = "begin transaction with name, rollback lowercased",
            Input    = "BEGIN TRANSACTION MyTx\ndelete from t where id = 1\nROLLBACK",
            Expected = "begin transaction MyTx\ndelete from t\nwhere\n\tid = 1\nrollback",
        },
        new TestCase {
            Rule = "tran", Name = "commit is not swallowed as a select column",
            Input    = "begin tran\nselect 1 as x\ncommit",
            Expected = "begin tran\nselect\n\t1 as x\ncommit",
        },
        new TestCase {
            Rule = "tran", Name = "commit / rollback keep transaction on their own line",
            Input    = "if @@TRANCOUNT > 0\nrollback \ntransaction\nif @@TRANCOUNT > 0\ncommit\n\ntransaction",
            Expected = "if @@TRANCOUNT > 0\nrollback transaction\nif @@TRANCOUNT > 0\ncommit transaction",
        },
        new TestCase {
            Rule = "tran", Name = "begin transaction split across lines is joined",
            Input    = "begin\ntransaction\nselect 1",
            Expected = "begin transaction\nselect\n\t1",
        },
        new TestCase {
            Rule = "tran", Name = "unbalanced begin (no end) is returned unchanged",
            Input    = "begin\nselect 1 as a",
            Expected = "begin\nselect 1 as a",
        },

        // ── statement boundaries after a column/condition list ────────────────
        new TestCase {
            Rule = "stmtbound", Name = "set statement after assignment select is not a column",
            Input    = "select @a = 1\nset @b = 2",
            Expected = "select @a = 1\nset @b = 2",
        },
        new TestCase {
            Rule = "stmtbound", Name = "identifier statement after select is not swallowed with an invented comma",
            Input    = "select 1 as x\nuse mydb",
            Expected = "select\n\t1 as x\nuse mydb",
        },
        new TestCase {
            Rule = "stmtbound", Name = "identifier statement after where is not swallowed as a condition",
            Input    = "select 1 from t where x = 1\nopen cur",
            Expected = "select\n\t1\nfrom t\nwhere\n\tx = 1\nopen cur",
        },

        // ── fragments: unrecognized statements must not become bogus columns ──
        new TestCase {
            Rule = "fragment", Name = "two-word statement is not rewritten into 'x as y'",
            Input    = "use mydb",
            Expected = "use mydb",
        },
        new TestCase {
            Rule = "fragment", Name = "waitfor is not rewritten into a column list",
            Input    = "waitfor delay '00:00:05'",
            Expected = "waitfor delay '00:00:05'",
        },

        // ── literals ──────────────────────────────────────────────────────────
        new TestCase {
            Rule = "hex", Name = "hex literal 0x1F stays one token",
            Input    = "select 0x1F as mask from t where flags & 0x0F = 0x01",
            Expected = "select\n\t0x1F as mask\nfrom t\nwhere\n\tflags & 0x0F = 0x01",
        },

        // ── non-reserved keyword as bare alias ────────────────────────────────
        new TestCase {
            Rule = "2.1", Name = "non-reserved keyword (day) as bare alias is kept as alias",
            Input    = "select getdate() day, 1 x from t",
            Expected = "select\n\tgetdate() as day,\n\t1 as x\nfrom t",
        },

        // ── ; before WITH is preserved ────────────────────────────────────────
        new TestCase {
            Rule = "meta", Name = "semicolon before a non-first WITH (CTE) is preserved",
            Input    = "select 1 as a\n;with cte as (select 2 as b) select * from cte",
            Expected = "select\n\t1 as a\n;with cte as (\n\tselect\n\t\t2 as b\n)\nselect\n\t*\nfrom cte",
        },

        // ── GO with a repeat count ────────────────────────────────────────────
        new TestCase {
            Rule = "go", Name = "GO 5 keeps its repeat count",
            Input    = "select 1\nGO 5",
            Expected = "select\n\t1\n\nGO 5",
        },

        // ── CASE: OR in a when-condition ──────────────────────────────────────
        new TestCase {
            Rule = "2.5", Name = "when with or: operator starts the continuation line",
            Input    = "select case when a=1 or b=2 then 'x' else 'y' end as r from t",
            Expected = "select\n\tcase\n\t\twhen a = 1\n\t\t\tor b = 2\n\t\tthen 'x'\n\t\telse 'y'\n\tend as r\nfrom t",
        },

        // ── HAVING with several conditions ────────────────────────────────────
        new TestCase {
            Rule = "having", Name = "having with and: each condition on its own line",
            Input    = "select a from t group by a having count(*) > 1 and sum(b) < 5",
            Expected = "select\n\ta\nfrom t\ngroup by\n\ta\nhaving\n\tcount(*) > 1\n\tand sum(b) < 5",
        },

        // ── nested block comments ─────────────────────────────────────────────
        new TestCase {
            Rule = "blockcmt", Name = "nested /* /* */ */ comment is one comment",
            Input    = "select a /* outer /* inner */ still comment */ from t",
            Expected = "select\n\ta/* outer /* inner */ still comment */\nfrom t",
        },

        // ── UPDATE/DELETE WHERE: flat like SELECT, no outer parens added ──────
        new TestCase {
            Rule = "where-or", Name = "update with or: flat conditions, no added parens",
            Input    = "update t set a = 1 where x = 1 or y = 2",
            Expected = "update t\nset\n\ta = 1\nwhere\n\tx = 1\n\tor y = 2",
        },
        new TestCase {
            Rule = "where-or", Name = "delete with and/or: flat conditions, no added parens",
            Input    = "delete from t where a = 1 and b = 2 or c = 3",
            Expected = "delete from t\nwhere\n\ta = 1\n\tand b = 2\n\tor c = 3",
        },

        // ── DECLARE @t table (...) is laid out like create table ──────────────
        new TestCase {
            Rule = "declare", Name = "table variable: one column per line, like create table",
            Input    = "declare @t table (id int, name varchar(50))",
            Expected = "declare @t table (\n\tid int,\n\tname varchar(50)\n)",
        },
        new TestCase {
            Rule = "declare", Name = "table variable keeps its columns' comments",
            Input    = "declare @tbl table\n(\nid  int,          -- ключ\nval nvarchar(100) -- значение\n);",
            Expected = "declare @tbl table (\n\tid int,\t\t-- ключ\n\tval nvarchar(100)\t\t-- значение\n);",
        },

        // ── First argument on the keyword line: declare / assignment select / if ──
        new TestCase {
            Rule = "declare", Name = "first variable on the declare line, the rest one tab in",
            Input    = "declare @i int, @a varchar(200), @b float",
            Expected = "declare @i int,\n\t@a varchar(200),\n\t@b float",
        },
        new TestCase {
            Rule = "assign", Name = "assignment select: first assignment on the select line",
            Input    = "select @a = 'делай так', @i = 67, @b = 12.3",
            Expected = "select @a = 'делай так',\n\t@i = 67,\n\t@b = 12.3",
        },
        new TestCase {
            Rule = "assign", Name = "assignment select with from: first assignment on the select line",
            Input    = "select @a = t.name, @b = t.cnt from dbo.t as t where t.id = 1",
            Expected = "select @a = t.name,\n\t@b = t.cnt\nfrom dbo.t as t\nwhere\n\tt.id = 1",
        },
        new TestCase {
            Rule = "assign", Name = "result-set select keeps every column on its own line",
            Input    = "select @a, @b",
            Expected = "select\n\t@a,\n\t@b",
        },
        new TestCase {
            Rule = "assign", Name = "mixed assignment/plain column list is not an assignment select",
            Input    = "select @a = 1, b",
            Expected = "select\n\t@a = 1,\n\tb",
        },
        new TestCase {
            Rule = "assign", Name = "-- comment on the select line keeps the broken-out layout",
            Input    = "select --note\n@a = 1, @b = 2",
            Expected = "select\t\t--note\n\t@a = 1,\n\t@b = 2",
        },
        new TestCase {
            Rule = "if", Name = "first condition on the if line, the rest one tab in",
            Input    = "if @i < 1 and @b >= 1 print('1')",
            Expected = "if @i < 1\n\tand @b >= 1\nprint('1')",
        },
        new TestCase {
            Rule = "if", Name = "if/else without begin end: body on the next line, no blank line",
            Input    = "if @i < 1 and @b >= 1\nprint('1')\nelse\nprint('0')",
            Expected = "if @i < 1\n\tand @b >= 1\nprint('1')\nelse\nprint('0')",
        },
        new TestCase {
            Rule = "if", Name = "if/else with begin end: block at the if indent, no extra blank line",
            Input    = "if @i < 1 and @b >= 1\nbegin\nprint('1')\nend\nelse\nbegin\nprint('0')\nend",
            Expected = "if @i < 1\n\tand @b >= 1\nbegin\n\n\tprint('1')\n\nend\nelse\nbegin\n\n\tprint('0')\n\nend",
        },
        new TestCase {
            Rule = "droptable", Name = "table name ends at the next statement",
            Input    = "drop table #t\nupdate t set a = 1 where id = 5",
            Expected = "drop table #t\nupdate t\nset\n\ta = 1\nwhere\n\tid = 5",
        },
        new TestCase {
            Rule = "droptable", Name = "table name does not swallow the end of the enclosing block",
            Input    = "if object_id('tempdb..#flag') is not null\nand @t < 2\nbegin\ndrop table #flag\nend",
            Expected = "if object_id('tempdb..#flag') is not null\n\tand @t < 2\nbegin\n\n\tdrop table #flag\n\nend",
        },
        new TestCase {
            Rule = "if", Name = "bare body sits at the if's own indent",
            Input    = "if object_id('tempdb..#flag') is not null\nand @t < 2\ndrop table #flag",
            Expected = "if object_id('tempdb..#flag') is not null\n\tand @t < 2\ndrop table #flag",
        },
        new TestCase {
            Rule = "while", Name = "while is laid out like if: first condition on its line, body below",
            Input    = "while 1 < 2 and 20>5\nbegin\nselect\n1\nset @r += 1\nend",
            Expected = "while 1 < 2\n\tand 20 > 5\nbegin\n\n\tselect\n\t\t1\n\n\tset @r += 1\n\nend",
        },
        new TestCase {
            Rule = "while", Name = "while with a bare body statement",
            Input    = "while @i < 10\nset @i = @i + 1",
            Expected = "while @i < 10\nset @i = @i + 1",
        },
        new TestCase {
            Rule = "if", Name = "else branch with a select body",
            Input    = "if @i < 1 select 1 else select 0",
            Expected = "if @i < 1\nselect\n\t1\nelse\nselect\n\t0",
        },
        // ── a multi-line block comment keeps its own lines ───────────────────
        new TestCase {
            Rule = "blockcmt", Name = "multi-line block comment does not drag the next column onto its last line",
            Input    = "select\na,\n/*old1,\nold2*/\nb\nfrom t",
            Expected = "select\n\ta,\n\t/*old1,\nold2*/\n\tb\nfrom t",
        },
        new TestCase {
            Rule = "blockcmt", Name = "one-line comment between columns: the break lands after it",
            Input    = "select a, /*x*/ b from t",
            Expected = "select\n\ta,/*x*/\n\tb\nfrom t",
        },

        // ── OVER (…) is laid out as a list ───────────────────────────────────
        new TestCase {
            Rule = "window", Name = "over with partition and order lists, alias after the paren",
            Input    = "select first_value(w.decision_group)\nover (\npartition by\nw.organization_id,new_column\norder by\ndecision_group,new_column\n) as updated_decision_group\nfrom #res as w",
            Expected = "select\n\tfirst_value(w.decision_group)\n\t\tover (\n\t\t\tpartition by\n\t\t\t\tw.organization_id,\n\t\t\t\tnew_column\n\t\t\torder by\n\t\t\t\tdecision_group,\n\t\t\t\tnew_column\n\t\t) as updated_decision_group\nfrom #res as w",
        },
        new TestCase {
            Rule = "window", Name = "frame clause keeps its own line",
            Input    = "select first_value(g) over (partition by p order by p asc rows unbounded preceding) as f from t",
            Expected = "select\n\tfirst_value(g)\n\t\tover (\n\t\t\tpartition by\n\t\t\t\tp\n\t\t\torder by\n\t\t\t\tp asc\n\t\t\trows unbounded preceding\n\t\t) as f\nfrom t",
        },

        // ── a comment between the column list and INTO/FROM ─────────────────
        new TestCase {
            Rule = "linecmt", Name = "commented-out into between the columns and from",
            Input    = "select d.group_id\n--  into #group_by_org\nfrom t as d\nwhere d.id = 1",
            Expected = "select\n\td.group_id\n--  into #group_by_org\nfrom t as d\nwhere\n\td.id = 1",
        },
        new TestCase {
            Rule = "linecmt", Name = "comment before into keeps the into clause",
            Input    = "select a\n--note\ninto #x\nfrom t",
            Expected = "select\n\ta\n--note\ninto #x\nfrom t",
        },
        new TestCase {
            Rule = "linecmt", Name = "commented-out into inside a subquery",
            Input    = "select a from t where g in (select d.id\n--  into #tmp\nfrom u as d\nwhere d.x = 1)",
            Expected = "select\n\ta\nfrom t\nwhere\n\tg in (\n\t\tselect\n\t\t\td.id\n\t\t--  into #tmp\n\t\tfrom u as d\n\t\twhere\n\t\t\td.x = 1\n\t)",
        },

        // ── NOT belongs to the operator: losing it inverts the condition ─────
        new TestCase {
            Rule = "isnull", Name = "not like keeps its negation",
            Input    = "select u.Sid from Users as u where u.UserName not like '%CRP_%'\nand u.UserName not like '%Domain%'",
            Expected = "select\n\tu.Sid\nfrom Users as u\nwhere\n\tu.UserName not like '%CRP_%'\n\tand u.UserName not like '%Domain%'",
        },
        new TestCase {
            Rule = "isnull", Name = "not between keeps its negation",
            Input    = "select a from t where x not between 1 and 5 and y between 2 and 3",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx not between 1 and 5\n\tand y between 2 and 3",
        },

        // ── the line break around a /* */ comment follows the source ─────────
        new TestCase {
            Rule = "blockcmt", Name = "break after a single-line comment is kept",
            Input    = "select\nc.title,\n/*note*/\n12 as a\nfrom a as c",
            Expected = "select\n\tc.title,\n\t/*note*/\n\t12 as a\nfrom a as c",
        },
        new TestCase {
            Rule = "blockcmt", Name = "no break after a multi-line comment is kept",
            Input    = "select\nc.title,\n/*note\nnote*/ 12 as a\nfrom a as c",
            Expected = "select\n\tc.title,\n\t/*note\nnote*/ 12 as a\nfrom a as c",
        },
        new TestCase {
            Rule = "blockcmt", Name = "multi-line comment from the column line: next column on its closing line",
            Input    = "select\nc.title, /*note\nnote*/ 12 as a\nfrom a as c",
            Expected = "select\n\tc.title,/*note\nnote*/ 12 as a\nfrom a as c",
        },
        new TestCase {
            Rule = "blockcmt", Name = "comment starting on the column line keeps its break",
            Input    = "select\nc.title, /*note\nnote*/\n12 as a\nfrom a as c",
            Expected = "select\n\tc.title,/*note\nnote*/\n\t12 as a\nfrom a as c",
        },

        // ── a comment on the join line must not hide the ON that follows ─────
        new TestCase {
            Rule = "comments", Name = "comment after the join's alias stays on the join line",
            Input    = "select a\nfrom t\nleft join u as ug --создал процесс\non ug.id = t.id\nand ug.x = 1",
            Expected = "select\n\ta\nfrom t\n\tleft join u as ug\t\t--создал процесс\n\t\ton ug.id = t.id\n\t\tand ug.x = 1",
        },
        new TestCase {
            Rule = "comments", Name = "comment on its own line between the join and ON",
            Input    = "select a\nfrom t\nleft join u as ug\n--почему так\non ug.id = t.id",
            Expected = "select\n\ta\nfrom t\n\tleft join u as ug\n\t\t--почему так\n\t\ton ug.id = t.id",
        },

        // ── a function argument may be a whole condition ─────────────────────
        new TestCase {
            Rule = "fnbreak", Name = "iif with an and/or condition as its first argument",
            Input    = "select iif(a = 1 and b > 2, 'x', 'y') as f from t",
            Expected = "select\n\tiif(a = 1 and b > 2, 'x', 'y') as f\nfrom t",
        },
        new TestCase {
            Rule = "fnbreak", Name = "condition group plus and inside a function argument",
            Input    = "select iif(\n(\nw.close_dt is null\nor w.close_dt > @dto\n)\nand datediff(month, w.create_dt, w.close_dt) >= 1,\n1,\n0) as x\nfrom t as w",
            Expected = "select\n\tiif(\n\t\t(\n\t\t\tw.close_dt is null\n\t\t\tor w.close_dt > @dto\n\t\t) and datediff(month, w.create_dt, w.close_dt) >= 1,\n\t\t1,\n\t\t0\n\t) as x\nfrom t as w",
        },

        // ── leading-comma lists and comments at the bottom of a subquery ─────
        new TestCase {
            Rule = "linecmt", Name = "comment before the separating comma keeps the column list going",
            Input    = "select\na\n--note\n,b\n,c\nfrom t",
            Expected = "select\n\ta,\n\t--note\n\tb,\n\tc\nfrom t",
        },
        new TestCase {
            Rule = "linecmt", Name = "commented-out column between leading-comma columns",
            Input    = "select a\n-- ,old_col\n,b\nfrom t\nwhere x = 1",
            Expected = "select\n\ta,\n\t-- ,old_col\n\tb\nfrom t\nwhere\n\tx = 1",
        },
        new TestCase {
            Rule = "linecmt", Name = "comment before a subquery's closing paren stays inside it",
            Input    = "select q.a\nfrom (\nselect a from #t\n--where a = 1\n) as q\nleft join #u as u\non u.a = q.a",
            Expected = "select\n\tq.a\nfrom (\n\tselect\n\t\ta\n\tfrom #t\n\t--where a = 1\n) as q\n\tleft join #u as u\n\t\ton u.a = q.a",
        },

        // ── a comment in a place no rule can lay out is hoisted, never lost ──
        new TestCase {
            Rule = "cmt-safety", Name = "comment inside a dotted name does not mangle it",
            Input    = "select Id from dbo.--note\nUsers where Status = 1",
            Expected = "--note\nselect\n\tId\nfrom dbo.Users\nwhere\n\tStatus = 1",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "comment where an operand is expected",
            Input    = "select a from t where x = --note\n1",
            Expected = "--note\nselect\n\ta\nfrom t\nwhere\n\tx = 1",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "comment that would end up in front of the rest of a condition",
            Input    = "select a from t where Status --note\n= 1",
            Expected = "--note\nselect\n\ta\nfrom t\nwhere\n\tStatus = 1",
        },

        // ── comments inside a parenthesised condition group ──────────────────
        new TestCase {
            Rule = "where-or", Name = "comment on the not ( line stays there",
            Input    = "select a from t where e.cid is null\nand not (  --почему\nw.mm_dz = 1\nand w.status = 2\n)",
            Expected = "select\n\ta\nfrom t\nwhere\n\te.cid is null\n\tand not (\t\t--почему\n\t\tw.mm_dz = 1\n\t\tand w.status = 2\n\t)",
        },
        new TestCase {
            Rule = "where-or", Name = "a single condition in parens with a comment gets the group layout",
            Input    = "select a from t where ( --note\na = 1 )",
            Expected = "select\n\ta\nfrom t\nwhere\n\t(\t\t--note\n\t\ta = 1\n\t)",
        },
        new TestCase {
            Rule = "where-or", Name = "comments above and after conditions inside a group",
            Input    = "select a from t where not (\n--why\na = 1\nand b = 2 --tail\n)",
            Expected = "select\n\ta\nfrom t\nwhere\n\tnot (\n\t\t--why\n\t\ta = 1\n\t\tand b = 2\t\t--tail\n\t)",
        },
        new TestCase {
            Rule = "where-or", Name = "arithmetic parens stay inline",
            Input    = "select (1 + 2) * 3 as x from t",
            Expected = "select\n\t(1 + 2) * 3 as x\nfrom t",
        },

        // ── BEGIN TRY / BEGIN CATCH and @@globals ────────────────────────────
        new TestCase {
            Rule = "atat", Name = "@@TRANCOUNT is one token, not @ plus @TRANCOUNT",
            Input    = "if @@TRANCOUNT > 0\ncommit transaction",
            Expected = "if @@TRANCOUNT > 0\ncommit transaction",
        },
        new TestCase {
            Rule = "atat", Name = "@@ROWCOUNT as a select column keeps its case",
            Input    = "select @@ROWCOUNT as x, @a from t",
            Expected = "select\n\t@@ROWCOUNT as x,\n\t@a\nfrom t",
        },
        new TestCase {
            Rule = "trycatch", Name = "try/catch blocks, blank line around each",
            Input    = "begin transaction\nbegin try\ndelete from p\nwhere id = 980\nend try\nbegin catch\nif @@TRANCOUNT > 0\nrollback transaction\nend catch\nif @@TRANCOUNT > 0\ncommit transaction",
            Expected = "begin transaction\n\nbegin try\n\n\tdelete from p\n\twhere\n\t\tid = 980\n\nend try\n\nbegin catch\n\n\tif @@TRANCOUNT > 0\n\trollback transaction\n\nend catch\n\nif @@TRANCOUNT > 0\ncommit transaction",
        },
        new TestCase {
            Rule = "trycatch", Name = "try/catch nested in a begin/end block",
            Input    = "begin\nbegin try\nselect 1\nend try\nbegin catch\nthrow\nend catch\nend",
            Expected = "begin\n\n\tbegin try\n\n\t\tselect\n\t\t\t1\n\n\tend try\n\n\tbegin catch\n\n\t\tthrow\n\n\tend catch\n\nend",
        },
        new TestCase {
            Rule = "trycatch", Name = "begin try closed by a plain end is left alone",
            Input    = "begin try\nselect 1\nend",
            Expected = "begin try\nselect 1\nend",
        },

        // ── comments inside GROUP BY / ORDER BY lists ────────────────────────
        new TestCase {
            Rule = "linecmt", Name = "-- comment on its own line inside group by",
            Input    = "select a, b from t group by a,\n--note\nb",
            Expected = "select\n\ta,\n\tb\nfrom t\ngroup by\n\ta,\n\t--note\n\tb",
        },
        new TestCase {
            Rule = "linecmt", Name = "-- comment on its own line inside order by",
            Input    = "select a from t order by a,\n--note\nb",
            Expected = "select\n\ta\nfrom t\norder by\n\ta,\n\t--note\n\tb",
        },
        new TestCase {
            Rule = "blockcmt", Name = "block comment before a group by item glues inline",
            Input    = "select a, b from t group by a, /*x*/ b",
            Expected = "select\n\ta,\n\tb\nfrom t\ngroup by\n\ta,\n\t/*x*/ b",
        },
        new TestCase {
            Rule = "2.12", Name = "comment after the insert column list does not split the statement",
            Input    = "insert into t (\na,\nb\n)\t\t-- note\n\nselect x, y from s",
            Expected = "insert into t (\n\ta,\n\tb\n)\t\t-- note\nselect\n\tx,\n\ty\nfrom s",
        },

        // ── /* */ comments inside an IN list keep their position ─────────────
        new TestCase {
            Rule = "blockcmt", Name = "block comment before the first IN value is kept",
            Input    = "select a from t where x in (/*11,*/54,/*56,*/24,26,49)",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx in (/*11,*/54, /*56,*/24, 26, 49)",
        },
        new TestCase {
            Rule = "blockcmt", Name = "block comment after a value stays glued to it",
            Input    = "select a from t where x in (1, 2 /*two*/, 3)",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx in (1, 2/*two*/, 3)",
        },
        new TestCase {
            Rule = "blockcmt", Name = "block comment before the closing paren sticks to the last value",
            Input    = "select a from t where x in (1, 2 /*tail*/)",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx in (1, 2/*tail*/)",
        },
        new TestCase {
            Rule = "blockcmt", Name = "leading block comment survives a -- comment breaking the list",
            Input    = "select a from t where x in (1, --note\n2, /*c*/3)",
            Expected = "select\n\ta\nfrom t\nwhere\n\tx in (\n\t\t1,\t\t--note\n\t\t2,\n\t\t/*c*/3\n\t)",
        },

        // ── Unfinished construct at the end of the selection ──────────────────
        new TestCase {
            Rule = "parse-safety", Name = "everything but the cut-off remainder is formatted",
            Input    = "create table #process (\nobject_type varchar(255),\nobject_id int\n)\n\ninsert into #process(\nobject_type,\nobject_id\n)\nselect\nobject_type,\nobject_id\nfrom openquery(",
            Expected = "create table #process (\n\tobject_type varchar(255),\n\tobject_id int\n)\n\ninsert into #process (\n\tobject_type,\n\tobject_id\n)\nselect\n\tobject_type,\n\tobject_id\nfrom openquery(",
        },
        new TestCase {
            Rule = "parse-safety", Name = "cut-off tail keeps the line it was written on",
            Input    = "select a\nfrom t\nselect b from openquery(",
            Expected = "select\n\ta\nfrom t\nselect\n\tb\nfrom openquery(",
        },
        new TestCase {
            Rule = "parse-safety", Name = "prefix that would swallow text is rejected (dangling where)",
            Input    = "update t set a = 1 where (x = 1",
            Expected = "update t set a = 1 where (x = 1",
        },
        new TestCase {
            Rule = "parse-safety", Name = "cut-off tail after a join keeps the normalized prefix",
            Input    = "select a\nfrom t\n\tleft outer join u on u.id = t.id\nselect b from openquery(",
            Expected = "select\n\ta\nfrom t\n\tleft join u\n\t\ton u.id = t.id\nselect\n\tb\nfrom openquery(",
        },
        new TestCase {
            Rule = "parse-safety", Name = "unfinished first statement: parsable prefix still formatted",
            Input    = "select b from openquery(",
            Expected = "select\n\tb\nfrom openquery(",
        },

        // ── Blank line around a comment after a FROM with no WHERE ───────────
        new TestCase {
            Rule = "comments", Name = "blank line before a comment after from (no where) is kept",
            Input    = "select a\nfrom t\n\n--note\n\nselect b\nfrom t",
            Expected = "select\n\ta\nfrom t\n\n--note\n\nselect\n\tb\nfrom t",
        },
        new TestCase {
            Rule = "comments", Name = "comment after from with no blank line stays glued to the next statement",
            Input    = "select a\nfrom t\n--note\nselect b\nfrom t",
            Expected = "select\n\ta\nfrom t\n--note\nselect\n\tb\nfrom t",
        },
        new TestCase {
            Rule = "comments", Name = "comment between from and where stays inside the select",
            Input    = "select a\nfrom t\n--note\nwhere x = 1",
            Expected = "select\n\ta\nfrom t\n--note\nwhere\n\tx = 1",
        },
        new TestCase {
            Rule = "comments", Name = "blank line around a comment inside begin/end is kept",
            Input    = "begin\n\ninsert into t2 (a)\nselect nc.a\nfrom #new as nc\n\n--следующий шаг\n\ninsert into t3 (a)\nselect nc.a\nfrom #new as nc\n\nend",
            Expected = "begin\n\n\tinsert into t2 (\n\t\ta\n\t)\n\tselect\n\t\tnc.a\n\tfrom #new as nc\n\n\t--следующий шаг\n\n\tinsert into t3 (\n\t\ta\n\t)\n\tselect\n\t\tnc.a\n\tfrom #new as nc\n\nend",
        },
        new TestCase {
            Rule = "if", Name = "comment after a raw if body belongs to the next statement",
            Input    = "if @i < 1\nprint('1')\n\n--next\nprint('2')",
            Expected = "if @i < 1\nprint('1')\n\n--next\nprint('2')",
        },

        // ── cmt-inline: a comment with code behind it becomes a block comment ─
        new TestCase {
            Rule = "cmt-inline", Name = "-- comment after a join keyword becomes a block comment",
            Input    = "select a from t\nleft join -- тип соединения\n     dbo.Orders as o\n     on o.id = t.id",
            Expected = "select\n\ta\nfrom t\n\tleft join /*тип соединения*/ dbo.Orders as o\n\t\ton o.id = t.id",
        },
        new TestCase {
            Rule = "cmt-inline", Name = "block comment between from and its table keeps the table beside it",
            Input    = "SELECT status, total\nFROM /*таблица должна быть рядом с from*/\ndbo.Orders",
            Expected = "select\n\tstatus,\n\ttotal\nfrom /*таблица должна быть рядом с from*/ dbo.Orders",
        },
        new TestCase {
            Rule = "cmt-inline", Name = "-- comment after a table name becomes a block comment before the column list",
            Input    = "create table dbo.T  -- комментарий после имени\n(\nid int not null\n)",
            Expected = "create table dbo.T /*комментарий после имени*/ (\n\tid int not null\n)",
        },
        new TestCase {
            Rule = "blockcmt", Name = "comment alone on its line inside over () keeps that line",
            Input    = "select row_number() over (\n/* без PARTITION BY */\norder by a.total desc -- по убыванию\n) as rn\nfrom t",
            Expected = "select\n\trow_number()\n\t\tover (\n\t\t\t/* без PARTITION BY */\n\t\t\torder by\n\t\t\t\ta.total desc\t\t-- по убыванию\n\t\t) as rn\nfrom t",
        },
        new TestCase {
            Rule = "2.14", Name = "table constraints keep their names and lowercase their keywords",
            Input    = "create table dbo.T (\nid int identity(1, 1) not null,\ntotal AS (price * qty) PERSISTED,\nCONSTRAINT PK_T PRIMARY KEY CLUSTERED (id ASC),\nCONSTRAINT CK_T CHECK (qty > 0)\n-- висячий комментарий\n);",
            Expected = "create table dbo.T (\n\tid int identity(1, 1) not null,\n\ttotal as (price * qty) persisted,\n\tconstraint PK_T primary key clustered (id asc),\n\tconstraint CK_T check (qty > 0)\n\t-- висячий комментарий\n);",
        },
        new TestCase {
            Rule = "window", Name = "frame clause words lowercased",
            Input    = "select sum(a.total) OVER (\nPARTITION BY a.line -- разбиение\nORDER BY a.id\nROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW -- рамка\n) as running\nfrom t as a",
            Expected = "select\n\tsum(a.total)\n\t\tover (\n\t\t\tpartition by\n\t\t\t\ta.line\t\t-- разбиение\n\t\t\torder by\n\t\t\t\ta.id\n\t\t\trows between unbounded preceding and current row -- рамка\n\t\t) as running\nfrom t as a",
        },

        // ── comments that used to derail a procedure body ─────────────────────
        new TestCase {
            Rule = "cmt-safety", Name = "leading-comma SET list: comma before the comment",
            Input    = "update ol\n   SET ol.qty = ol.qty + 1 -- инкремент\n     , ol.note = N'x'\nfrom dbo.OrderLine as ol\nwhere ol.id = 1",
            Expected = "update ol\nset\n\tol.qty = ol.qty + 1,\t\t-- инкремент\n\tol.note = N'x'\nfrom dbo.OrderLine as ol\nwhere\n\tol.id = 1",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "create table keeps its columns' comments",
            Input    = "create table #t (\nid int,          -- ключ\nval nvarchar(100) -- значение\n)",
            Expected = "create table #t (\n\tid int,\t\t-- ключ\n\tval nvarchar(100)\t\t-- значение\n)",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "values rows keep their comments, comma first",
            Input    = "insert into @tbl (id, val)\nvalues\n(1, N'один'),   -- первая\n(2, N'два');    -- вторая",
            Expected = "insert into @tbl (\n\tid,\n\tval\n)\nvalues\n\t(1, N'один'),\t\t-- первая\n\t(2, N'два');\t\t-- вторая",
        },
        new TestCase {
            Rule = "if", Name = "comment on the else line stays on it",
            Input    = "if @mode = 0\nprint 1;\nelse -- режим записи\nprint 2;",
            Expected = "if @mode = 0\nprint 1;\nelse\t\t-- режим записи\nprint 2;",
        },
        new TestCase {
            Rule = "semi", Name = "';' kept inside a block, and continue/break lowercased",
            Input    = "while @i < 10\nbegin\nSET @i += 1; -- шаг\nIF @i = 5 CONTINUE;\nend;",
            Expected = "while @i < 10\nbegin\n\n\tset @i += 1;\t\t-- шаг\n\n\tif @i = 5\n\tcontinue;\n\nend;",
        },
        new TestCase {
            Rule = "semi", Name = "begin/commit transaction keep their ';' and comment",
            Input    = "begin transaction; -- начало\ncommit transaction; -- фиксация",
            Expected = "begin transaction;\t\t-- начало\ncommit transaction;\t\t-- фиксация",
        },

        // ── 2.5 case: comments inside a CASE keep their places ────────────────
        new TestCase {
            Rule = "2.5", Name = "comment on the case line stays on it",
            Input    = "select case -- start\nwhen a = 1 then 'x'\nelse 'y'\nend as n\nfrom t",
            Expected = "select\n\tcase\t\t-- start\n\t\twhen a = 1\n\t\tthen 'x'\n\t\telse 'y'\n\tend as n\nfrom t",
        },
        new TestCase {
            Rule = "2.5", Name = "comment between the condition and then closes the when line",
            Input    = "select case\nwhen a = 1 /* c */ then 'x'\nelse 'y'\nend as n\nfrom t",
            Expected = "select\n\tcase\n\t\twhen a = 1 /* c */\n\t\tthen 'x'\n\t\telse 'y'\n\tend as n\nfrom t",
        },
        new TestCase {
            Rule = "2.5", Name = "block comment after then stays in front of the value",
            Input    = "select case\nwhen a = 3 then /* c */ 'x'\nelse 'y'\nend as n\nfrom t",
            Expected = "select\n\tcase\n\t\twhen a = 3\n\t\tthen /* c */ 'x'\n\t\telse 'y'\n\tend as n\nfrom t",
        },
        new TestCase {
            Rule = "2.5", Name = "standalone comment between branches keeps its own line",
            Input    = "select case\nwhen a = 2 then 'B' -- second\n-- between branches\nwhen a = 3 then 'C'\nelse 'D'\nend as n\nfrom t",
            Expected = "select\n\tcase\n\t\twhen a = 2\n\t\tthen 'B'\t\t-- second\n\t\t-- between branches\n\t\twhen a = 3\n\t\tthen 'C'\n\t\telse 'D'\n\tend as n\nfrom t",
        },

        // ── fnbreak: comments on function arguments ───────────────────────────
        new TestCase {
            Rule = "fnbreak", Name = "arguments with -- comments break onto their own lines",
            Input    = "select coalesce(\na,   -- one\nb,   -- two\n'' -- three\n) as t\nfrom t",
            Expected = "select\n\tcoalesce(\n\t\ta,\t\t-- one\n\t\tb,\t\t-- two\n\t\t''\t\t-- three\n\t) as t\nfrom t",
        },
        new TestCase {
            Rule = "fnbreak", Name = "block comment before the comma stays inline",
            Input    = "select iif(o.total > 1000 /* limit */, 'a', 'b') as n\nfrom t",
            Expected = "select\n\tiif(o.total > 1000 /* limit */, 'a', 'b') as n\nfrom t",
        },
        new TestCase {
            Rule = "convert", Name = "comment after the cast type stays inside the cast",
            Input    = "select cast(o.total as decimal(18, 2) /* round */) as m\nfrom t",
            Expected = "select\n\tcast(o.total as decimal(18, 2) /* round */) as m\nfrom t",
        },

        // ── semi: the ';' the author wrote is kept where it was ───────────────
        new TestCase {
            Rule = "semi", Name = "a run of statements on one line splits at every ';'",
            Input    = "PRINT 'a'; PRINT 'b'; PRINT 'c';",
            Expected = "print 'a';\nprint 'b';\nprint 'c';",
        },
        new TestCase {
            Rule = "semi", Name = "';' glues to the statement, before its trailing comment",
            Input    = "SELECT [c] FROM dbo.Weird;  -- note",
            Expected = "select\n\t[c]\nfrom dbo.Weird;\t\t-- note",
        },
        new TestCase {
            Rule = "semi", Name = "a ';' written on its own line keeps that line",
            Input    = "SELECT 5 --2\n;",
            Expected = "select\n\t5\t\t--2\n;",
        },
        new TestCase {
            Rule = "semi", Name = "';' before a with is still glued to the with",
            Input    = "select 1\n;with c as (select 2 as a from t)\nselect a from c",
            Expected = "select\n\t1\n;with c as (\n\tselect\n\t\t2 as a\n\tfrom t\n)\nselect\n\ta\nfrom c",
        },
        new TestCase {
            Rule = "semi", Name = "set option name lowercased like a keyword",
            Input    = "SET QUOTED_IDENTIFIER ON;",
            Expected = "set quoted_identifier on;",
        },

        // ── weird identifiers and comment-shaped text that is not a comment ───
        new TestCase {
            Rule = "cmt-safety", Name = "escaped ]] inside a bracketed identifier is one token",
            Input    = "SELECT [col]]--umn]      FROM dbo.Weird;  -- note",
            Expected = "select\n\t[col]]--umn]\nfrom dbo.Weird;\t\t-- note",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "nested block comments close at the right level",
            Input    = "/* level 1\n  /* level 2\n    /* level 3 */\n  still level 2 */\nlevel 1 again */\nselect 1",
            Expected = "/* level 1\n  /* level 2\n    /* level 3 */\n  still level 2 */\nlevel 1 again */\nselect\n\t1",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "a slash right after the star does not close a block comment",
            Input    = "/*/ still one comment */\nselect 1",
            Expected = "/*/ still one comment */\nselect\n\t1",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "-- inside a block comment and /* inside a line comment",
            Input    = "/* has a -- marker inside */\n-- has a /* marker inside, opens nothing\nselect 1",
            Expected = "/* has a -- marker inside */\n-- has a /* marker inside, opens nothing\nselect\n\t1",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "unbalanced quote or bracket inside a comment breaks nothing",
            Input    = "/* don't break the parser */\n-- it's fine\n/* [ and ( and \" */\nselect 1",
            Expected = "/* don't break the parser */\n-- it's fine\n/* [ and ( and \" */\nselect\n\t1",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "-- and /* inside a string are not comments",
            Input    = "PRINT 'a -- b /* c */ d';",
            Expected = "print 'a -- b /* c */ d';",
        },
        new TestCase {
            Rule = "blockcmt", Name = "block comment written in place of a space keeps the expression whole",
            Input    = "SELECT 10 /* note */ / 2;",
            Expected = "select\n\t10 /* note */ / 2;",
        },

        // ── cte: a CTE list can head INSERT / UPDATE / DELETE too ─────────────
        new TestCase {
            Rule = "cte", Name = "cte in front of an insert ... select",
            Input    = ";with corr as (select * from #dr as dr where dr.groupID=4)\ninsert into #dr (dt, summa)\nselect corr.dt, sum(corr.summa) as summa\nfrom corr as corr\ngroup by corr.dt",
            Expected = ";with corr as (\n\tselect\n\t\t*\n\tfrom #dr as dr\n\twhere\n\t\tdr.groupID = 4\n)\ninsert into #dr (\n\tdt,\n\tsumma\n)\nselect\n\tcorr.dt,\n\tsum(corr.summa) as summa\nfrom corr as corr\ngroup by\n\tcorr.dt",
        },
        new TestCase {
            Rule = "cte", Name = "cte in front of an update",
            Input    = "with c as (select id from t)\nupdate u\nset a = 1\nfrom u\ninner join c on c.id = u.id",
            Expected = "with c as (\n\tselect\n\t\tid\n\tfrom t\n)\nupdate u\nset\n\ta = 1\nfrom u\n\tinner join c\n\t\ton c.id = u.id",
        },
        new TestCase {
            Rule = "cte", Name = "cte in front of a delete",
            Input    = "with c as (select id from t)\ndelete d\nfrom u as d\ninner join c on c.id = d.id",
            Expected = "with c as (\n\tselect\n\t\tid\n\tfrom t\n)\ndelete d\nfrom u as d\n\tinner join c\n\t\ton c.id = d.id",
        },

        // ── merge: MERGE … USING … WHEN … THEN ────────────────────────────────
        new TestCase {
            Rule = "merge", Name = "merge with update / insert branches",
            Input    = "merge into dbo.t as tgt\nusing dbo.s as src on tgt.id = src.id and tgt.k = src.k\nwhen matched then update set tgt.v = src.v\nwhen not matched then insert (id, v) values (src.id, src.v);",
            Expected = "merge into dbo.t as tgt\nusing dbo.s as src\n\ton tgt.id = src.id\n\t\tand tgt.k = src.k\nwhen matched\nthen\n\tupdate set\n\t\ttgt.v = src.v\nwhen not matched\nthen\n\tinsert (\n\t\tid,\n\t\tv\n\t)\n\tvalues\n\t\t(src.id, src.v);",
        },
        new TestCase {
            Rule = "merge", Name = "merge with by target / by source and default values",
            Input    = "merge dbo.t tgt using dbo.s src\non tgt.id = src.id\nwhen not matched by target then insert default values\nwhen not matched by source then delete;",
            Expected = "merge dbo.t as tgt\nusing dbo.s as src\n\ton tgt.id = src.id\nwhen not matched by target\nthen\n\tinsert default values\nwhen not matched by source\nthen\n\tdelete;",
        },
        new TestCase {
            Rule = "merge", Name = "merge keeps its comments, condition on the when line, output/into on their own",
            Input    = "MERGE dbo.OrderLine AS tgt -- приёмник\nUSING dbo.Src AS src\n   ON tgt.order_id = src.order_id -- условие\nWHEN MATCHED AND tgt.qty <> src.qty /* если изменилось */ THEN\n    UPDATE SET tgt.qty = src.qty -- количество\nOUTPUT $action, inserted.id -- что произошло\nINTO #log; -- временная",
            Expected = "merge dbo.OrderLine as tgt\t\t-- приёмник\nusing dbo.Src as src\n\ton tgt.order_id = src.order_id\t\t-- условие\nwhen matched and tgt.qty <> src.qty /* если изменилось */\nthen\n\tupdate set\n\t\ttgt.qty = src.qty\t\t-- количество\noutput $action, inserted.id\t\t-- что произошло\ninto #log;\t\t-- временная",
        },

        // ── pivot: PIVOT / UNPIVOT laid out as a block ────────────────────────
        new TestCase {
            Rule = "pivot", Name = "pivot over a derived table, one IN value per line",
            Input    = "select pvt.VendorID, pvt.[250] as Emp1\nfrom (select PurchaseOrderID, EmployeeID, VendorID from Purchasing.PurchaseOrderHeader) as p\npivot (count(PurchaseOrderID) for EmployeeID in ([250], [251])) as pvt\norder by pvt.VendorID",
            Expected = "select\n\tpvt.VendorID,\n\tpvt.[250] as Emp1\nfrom (\n\tselect\n\t\tPurchaseOrderID,\n\t\tEmployeeID,\n\t\tVendorID\n\tfrom Purchasing.PurchaseOrderHeader\n) as p\npivot (\n\tcount(PurchaseOrderID)\n\tfor EmployeeID in (\n\t\t[250],\n\t\t[251]\n\t)\n) as pvt\norder by\n\tpvt.VendorID",
        },
        new TestCase {
            Rule = "pivot", Name = "unpivot over a plain table",
            Input    = "select cid, col, val\nfrom t\nunpivot (val for col in (a, b, c)) as u\nwhere val > 0",
            Expected = "select\n\tcid,\n\tcol,\n\tval\nfrom t\nunpivot (\n\tval\n\tfor col in (\n\t\ta,\n\t\tb,\n\t\tc\n\t)\n) as u\nwhere\n\tval > 0",
        },
        new TestCase {
            Rule = "pivot", Name = "comments inside the pivot block stay in it",
            Input    = "select * from (select status, total from dbo.Orders) as s\nPIVOT (\nSUM(total) /* агрегат */\nFOR status IN ([1], [2]) -- список значений\n) AS p;",
            Expected = "select\n\t*\nfrom (\n\tselect\n\t\tstatus,\n\t\ttotal\n\tfrom dbo.Orders\n) as s\npivot (\n\tsum(total) /* агрегат */\n\tfor status in (\n\t\t[1],\n\t\t[2]\n\t)\t\t-- список значений\n) as p;",
        },
        new TestCase {
            Rule = "pivot", Name = "a join after the pivot keeps its own layout",
            Input    = "select *\nfrom #src\npivot (sum(amount) for m in ([1],[2])) as p\ninner join d on d.id = p.id",
            Expected = "select\n\t*\nfrom #src\npivot (\n\tsum(amount)\n\tfor m in (\n\t\t[1],\n\t\t[2]\n\t)\n) as p\n\tinner join d\n\t\ton d.id = p.id",
        },

        // ── sign: a + or - written in front of an operand ─────────────────────
        new TestCase {
            Rule = "sign", Name = "unary plus in a function argument",
            Input    = "select dateadd(day, +14, t.close_dt) as d\nfrom t",
            Expected = "select\n\tdateadd(day, +14, t.close_dt) as d\nfrom t",
        },
        new TestCase {
            Rule = "sign", Name = "signed columns keep their sign",
            Input    = "select -5 as a, +5 as b\nfrom t",
            Expected = "select\n\t-5 as a,\n\t+5 as b\nfrom t",
        },
        new TestCase {
            Rule = "sign", Name = "sign in front of a parenthesised expression invents no zero",
            Input    = "select -(a + b) as x\nfrom t\nwhere x between -1 and +2",
            Expected = "select\n\t-(a + b) as x\nfrom t\nwhere\n\tx between -1 and +2",
        },

        // ── linecmt: a -- comment on the select line stays on it ──────────────
        new TestCase {
            Rule = "linecmt", Name = "comment on the select line stays on the select line",
            Input    = "select  -- делитель для коэф.\n  d.process_id, count(1) as [n]\n into #enter\n from #DATA as d\n group by d.process_id",
            Expected = "select\t\t-- делитель для коэф.\n\td.process_id,\n\tcount(1) as [n]\ninto #enter\nfrom #DATA as d\ngroup by\n\td.process_id",
        },
        new TestCase {
            Rule = "linecmt", Name = "comment written below select keeps its own line",
            Input    = "select\n--note\na, b\nfrom t",
            Expected = "select\n\t--note\n\ta,\n\tb\nfrom t",
        },
        new TestCase {
            Rule = "blockcmt", Name = "block comment on the select line stays on it, a space out",
            Input    = "select /*note*/\na, b\nfrom t",
            Expected = "select /*note*/\n\ta,\n\tb\nfrom t",
        },
        new TestCase {
            Rule = "blockcmt", Name = "block comment on the select line stays even with a column behind it",
            Input    = "select /*note*/ a, b\nfrom t",
            Expected = "select /*note*/\n\ta,\n\tb\nfrom t",
        },
        new TestCase {
            Rule = "blockcmt", Name = "comments around AS stay on the column line",
            Input    = "SELECT 1 /* before */ AS /* after */ one;",
            Expected = "select\n\t1 /* before */ as /* after */ one;",
        },
        new TestCase {
            Rule = "blockcmt", Name = "block comment after a comma stays glued to the column before it",
            Input    = "select\na, /*note*/ b\nfrom t",
            Expected = "select\n\ta,/*note*/\n\tb\nfrom t",
        },
        new TestCase {
            Rule = "linecmt", Name = "comment after distinct/top stays on the select line",
            Input    = "select distinct top 10 --note\na\nfrom t",
            Expected = "select distinct top 10\t\t--note\n\ta\nfrom t",
        },

        // ── cmt-safety: a comment must not hide a DELETE/UPDATE clause ────────
        new TestCase {
            Rule = "cmt-safety", Name = "block comment on the delete line does not hide the from",
            Input    = "delete d  /* !!! */\nfrom webcar.dbo.t d\nwhere d.mm = @mm\n and d.yy = @yy",
            Expected = "delete d/* !!! */\nfrom webcar.dbo.t as d\nwhere\n\td.mm = @mm\n\tand d.yy = @yy",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "line comment on the delete line does not hide the from",
            Input    = "delete d --note\nfrom t d\nwhere d.mm = @mm",
            Expected = "delete d\t\t--note\nfrom t as d\nwhere\n\td.mm = @mm",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "comment on the delete from line does not hide the where",
            Input    = "delete from t  /* zzz */\nwhere x = 1",
            Expected = "delete from t/* zzz */\nwhere\n\tx = 1",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "commented-out clauses inside a delete keep their lines",
            Input    = "delete d\n--from t2 d\nfrom t d\ninner join u on u.id = d.id\n--note\nwhere d.a = 1",
            Expected = "delete d\n--from t2 d\nfrom t as d\n\tinner join u\n\t\ton u.id = d.id\n--note\nwhere\n\td.a = 1",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "comment on the update line does not hide the set",
            Input    = "update t  /* zzz */\nset a = 1\nwhere x = 1",
            Expected = "update t/* zzz */\nset\n\ta = 1\nwhere\n\tx = 1",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "commented-out clauses inside an update keep their lines",
            Input    = "update t\n--set b = 2\nset a = 1\nfrom t\ninner join u on u.id = t.id\n--note\nwhere x = 1",
            Expected = "update t\n--set b = 2\nset\n\ta = 1\nfrom t\n\tinner join u\n\t\ton u.id = t.id\n--note\nwhere\n\tx = 1",
        },
        new TestCase {
            Rule = "cmt-safety", Name = "comment after a delete belongs to the next statement",
            Input    = "delete d\nfrom t d\n\n--next statement\n\nselect 1",
            Expected = "delete d\nfrom t as d\n\n--next statement\n\nselect\n\t1",
        },

        // ── fromlist: several comma-separated FROM sources ────────────────────
        new TestCase {
            Rule = "fromlist", Name = "three sources: first on the from line, the rest one tab in",
            Input    = "select *\nfrom webcar.dbo.city as t1, \nwebcar.dbo.city as t2, \t#test as t3",
            Expected = "select\n\t*\nfrom webcar.dbo.city as t1,\n\twebcar.dbo.city as t2,\n\t#test as t3",
        },
        new TestCase {
            Rule = "fromlist", Name = "two sources followed by where",
            Input    = "select a from t1, t2 where t1.id = t2.id",
            Expected = "select\n\ta\nfrom t1,\n\tt2\nwhere\n\tt1.id = t2.id",
        },
        new TestCase {
            Rule = "fromlist", Name = "comma goes before the source's trailing comment",
            Input    = "select a\nfrom t1 as a --note\n, t2 as b",
            Expected = "select\n\ta\nfrom t1 as a,\t\t--note\n\tt2 as b",
        },
        new TestCase {
            Rule = "fromlist", Name = "joins stay with their source, comma closes it after the on line",
            Input    = "select a from t1 as a inner join u on u.id = a.id, t2 as b",
            Expected = "select\n\ta\nfrom t1 as a\n\tinner join u\n\t\ton u.id = a.id,\n\tt2 as b",
        },
    };
}

}
