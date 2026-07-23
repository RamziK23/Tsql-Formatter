# TsqlFormatter — справочник реализованных правил

Полный перечень того, что форматтер делает **сейчас**, по категориям, с примерами
«вход → результат». Отражает фактическое поведение кода (Lexer → Parser →
FormatterEngine + Rules), а не план. Бейдж-`id` каждого правила совпадает со значением
поля `Rule` в тестах — по нему запускается `run-tests.bat --rule <id>`.

- Правил: ~70 · Тестов: 99/99 · Движок: .NET 5
- Источник истины — код: `Core/Lexer.cs`, `Core/Parser.cs`, `Formatting/FormatterEngine.cs`, `Rules/*.cs`
- Индентация везде — символы табуляции (`\t`)

## Содержание

- [A. Регистр и литералы](#a-регистр-и-литералы)
- [B. Уровень скрипта: границы, пустые строки, GO](#b-уровень-скрипта-границы-пустые-строки-go)
- [C. Комментарии](#c-комментарии)
- [D. SELECT и список колонок](#d-select-и-список-колонок)
- [E. FROM / JOIN](#e-from--join)
- [F. WHERE и условия](#f-where-и-условия)
- [G. GROUP BY / HAVING / ORDER BY](#g-group-by--having--order-by)
- [H. Выражения и функции](#h-выражения-и-функции)
- [I. DECLARE](#i-declare)
- [J. INSERT / UPDATE / DELETE](#j-insert--update--delete)
- [K. CREATE TABLE / DROP TABLE](#k-create-table--drop-table)
- [L. CTE · BEGIN/END · UNION](#l-cte--beginend--union)
- [M. Фрагменты](#m-фрагменты)
- [N. Как писать тесты](#n-как-писать-тесты)

---

## A. Регистр и литералы

### `1.1` — Ключевые слова в нижний регистр, идентификаторы сохраняют регистр
Служебные слова (`select`, `from`, `where`, `inner join`…) → нижний регистр. Имена таблиц/колонок остаются как есть.

```sql
-- вход
SELECT Id, NAME from dbo.Users WHERE Status = 1
-- результат
select
	Id,
	NAME
from dbo.Users
where
	Status = 1
```

### `nstring` — Строковые литералы сохраняются дословно
Юникод-префикс `N'…'`, экранированные кавычки `''`, содержимое строк не изменяются.

```sql
-- вход
select @city=N'***', @x=N'it''s ok'
-- результат
select
	@city = N'***',
	@x = N'it''s ok'
```

### `dquote` — Идентификаторы в `[…]` и `"…"` дословно
Квадратные и двойные кавычки сохраняются, включая экранирование `""`.

```sql
-- вход
select "core_contract"."id" from "core_contract"
-- результат
select
	"core_contract"."id"
from "core_contract"
```

### `declare` — Числа сохраняются (экспонента, знак)
`1e-5`, отрицательные литералы `-1` не ломаются при лексинге.

```sql
-- вход
select 1e-5 as x
-- результат
select
	1e-5 as x
```

---

## B. Уровень скрипта: границы, пустые строки, GO

### `blanklines` — Пустые строки между операторами сохраняются из оригинала
Пустая строка между двумя операторами ставится **только если она была** в исходнике. Лишние не добавляются, существующие не удаляются.

```sql
-- вход (без пустой строки)
select 1 from t
select 2 from t
-- результат
select
	1
from t
select
	2
from t
```

### `go` — GO не схлопывается и всегда окружён пустыми строками
Каждый `GO` сохраняется отдельно (подряд идущие не объединяются), вокруг каждого — пустая строка. Ведущий `GO` без операторов до него отбрасывается.

```sql
-- вход
select 1
GO
GO
select 2 from t
-- результат
select
	1

GO

GO

select
	2
from t
```

### `stmtbound` — SELECT без FROM завершается на новом операторе
Присваивающий `select @x = 1` без FROM корректно завершается перед следующим оператором (`declare`, `exec`, `insert`, `if`, `while` и др.), а не поглощает его как «колонки».

```sql
-- вход
select @yy=2026,@mm=6
declare @z int
-- результат
select
	@yy = 2026,
	@mm = 6
declare @z int
```

### `meta` — Идемпотентность + ведущая `;`
Повторное форматирование даёт тот же результат (проверяется автоматически). Ведущая `;` перед первым оператором (`;with …`) сохраняется.

---

## C. Комментарии

> Ключевой принцип: комментарий остаётся привязанным к той строке, рядом с которой был.

### `comments` — Отдельный комментарий привязывается к следующему оператору
Комментарий на своей строке приклеивается к оператору снизу без пустой строки (если её не было); пустая строка сохраняется, если была.

```sql
-- вход
-- header
select a from t
-- результат
-- header
select
	a
from t
```

### `4.1.2` — Хвостовой `--` комментарий остаётся на своей строке
Комментарий в конце строки колонки, условия или таблицы FROM остаётся на этой строке, отделённый двумя табами. Запятая — перед комментарием.

```sql
-- вход
select a, --первая
b --вторая
from t
-- результат
select
	a,		--первая
	b		--вторая
from t
```

### `comments` — Хвостовой комментарий у таблицы FROM не «уезжает»
Комментарий на строке `from t1` остаётся там, а не мигрирует к следующему оператору.

```sql
-- вход
select a from t1  -- про t1
select b from t2
-- результат
select
	a
from t1		-- про t1
select
	b
from t2
```

### `blockcmt` — Блочные `/* */` комментарии прозрачны
Блочные комментарии **игнорируются** при разборе структуры: остальные правила применяются
так, будто их нет, и перед ними **не добавляется табуляция**. Комментарий остаётся вклеенным
к тому месту, где стоял (к предыдущему значению/колонке), не переносится на свою строку и не
разбивает конструкцию.

В списке колонок блочный комментарий вклеивается к предыдущей колонке (сразу после запятой,
без пробела/таба); следующая колонка идёт на своей строке:

```sql
-- вход
select '...' as [sql_text], /* [Alias] = expr, INNER JOIN */ c.[id] from [dbo].[C] c
-- результат
select
	'...' as [sql_text],/* [Alias] = expr, INNER JOIN */
	c.[id]
from [dbo].[C] as c
```

В IN-списке блочный комментарий вклеивается к значению и список остаётся в одну строку
(комментарий не разбивает список на строки):

```sql
-- вход
select * from a where id in (1, 2, 3, 4, 5, 6/*7,8,9,0*/, 1, 2, 3, 4)
-- результат
select
	*
from a
where
	id in (1, 2, 3, 4, 5, 6/*7,8,9,0*/, 1, 2, 3, 4)
```

---

## D. SELECT и список колонок

> Принцип: **каждая** колонка `select` (а также каждый элемент `group by` и `order by`)
> всегда переносится на свою строку с табуляцией — независимо от количества, даже если
> колонка одна.

### `2.1` — Колонки, каждая на своей строке (+1 таб)

```sql
-- вход
select a, b, c from t
-- результат
select
	a,
	b,
	c
from t
```

### `2.1.1` — Одна колонка тоже на своей строке

```sql
-- вход
select a from t
-- результат
select
	a
from t
```

### `5.1` — Алиас через `=` переписывается в `expr as alias`

```sql
-- вход
select [Name] = t.col1, cnt = count(*) from t
-- результат
select
	t.col1 as [Name],
	count(*) as cnt
from t
```

### `top` — TOP / DISTINCT / PERCENT / WITH TIES
`top 5 percent`, `top (10) with ties`, `distinct top 10` сохраняются в нормализованном виде.

```sql
-- вход
select top (10) with ties a from t order by a
-- результат
select top (10) with ties
	a
from t
order by
	a
```

---

## E. FROM / JOIN

### `2.2` — JOIN на +1 таб, ON на +2 таба; нормализация типа JOIN
Первое условие — на строке `on`, остальные с новых строк. `left outer join` → `left join`, голый `join` → `inner join`. Алиас `t a` → `t as a`.

```sql
-- вход
select t.a, u.b from t
inner join u on u.id=t.id and u.x=t.y
-- результат
select
	t.a,
	u.b
from t
	inner join u
		on u.id = t.id
		and u.x = t.y
```

### `hint` — Табличный хинт `with (nolock)`

```sql
-- вход
select a from t with (nolock) where x = 1
-- результат
select
	a
from t with (nolock)
where
	x = 1
```

### `derived` — Подзапрос как источник (derived table)

```sql
-- вход
select x.a from (select a from t where b=1) as x
-- результат
select
	x.a
from (
	select
		a
	from t
	where
		b = 1
) as x
```

### `7` — OPENQUERY: сервер и удалённый SQL на отдельных строках

```sql
-- вход
select w.* from openquery(srv, 'select id from t') as w
-- результат
select
	w.*
from openquery(
	srv,
	'select id from t'
) as w
```

### `apply` — CROSS APPLY / OUTER APPLY с подзапросом
Разворачивается аналогично JOIN: `cross apply ( … ) as l` с телом на +1 таб.

---

## F. WHERE и условия

> Принцип: после `where` **каждое** условие — на своей строке с одной табуляцией,
> даже если условие всего одно (`and`/`or` — в начале строки).

### `2.6` — Каждое условие на своей строке (даже одно)

```sql
-- вход
select * from t where x = 1
-- результат
select
	*
from t
where
	x = 1
```

```sql
-- вход
select * from t where a=1 or b=2
-- результат
select
	*
from t
where
	a = 1
	or b = 2
```

### `where-or` — Скобочные группы условий сохраняются, внешние скобки не добавляются

```sql
-- вход
... where x=1 and (b=1 or b=2) or y>0
-- результат
where
	x = 1
	and (
		b = 1
		or b = 2
	)
	or y > 0
```

### `isnull` / `2.4` — IS [NOT] NULL, IN, NOT IN, LIKE, BETWEEN
`in (subquery)` разворачивает подзапрос на новую строку с отступом.

```sql
-- вход
select * from t where id in (select id from u where u.x=1)
-- результат
select
	*
from t
where
	id in (
		select
			id
		from u
		where
			u.x = 1
	)
```

---

## G. GROUP BY / HAVING / ORDER BY

### `fragment` — GROUP BY: каждая колонка на своей строке (даже одна)

```sql
-- вход
group by s.[a], p.[Title], p.[FirstName]
-- результат
group by
	s.[a],
	p.[Title],
	p.[FirstName]
```

### `orderby` — ORDER BY: каждый элемент на своей строке (даже один); ASC/DESC сохраняются; HAVING поддержан

```sql
-- вход
select a from t order by a desc, b asc
-- результат
select
	a
from t
order by
	a desc,
	b asc
```

---

## H. Выражения и функции

### `2.15` / `fnbreak` — Функции: короткие inline; с многострочным аргументом — по строкам
Нет пробела перед `(` у функции. Если аргумент многострочный (CASE, подзапрос) — аргументы переносятся по строкам.

```sql
-- вход
select count(*), substring(name,1,3) from emp
-- результат
select
	count(*),
	substring(name, 1, 3)
from emp
```

### `convert` — CAST / CONVERT сохраняют скобки типа
`varchar(10)`, `decimal(18, 2)` не теряют скобки (ранее превращалось в `varchar10`).

```sql
-- вход
select convert(varchar(10), id) as c, cast(x as varchar(20)) as d from t
-- результат
select
	convert(varchar(10), id) as c,
	cast(x as varchar(20)) as d
from t
```

### `window` — Оконные функции OVER (…): нижний регистр и умные пробелы
`partition by` / `order by` внутри окна лоуэркейзятся; точки/скобки без лишних пробелов.

```sql
-- вход
select row_number() OVER (ORDER BY a.[Id]) as [Rn] from t
-- результат
select
	row_number() over (order by a.[Id]) as [Rn]
from t
```

### `dottedfn` — Функции с составным именем `schema.fn(args)`

```sql
-- вход
select webcar.dbo.datafirst(@yy, @mm) as d from t
-- результат
select
	webcar.dbo.datafirst(@yy, @mm) as d
from t
```

### `2.5` — CASE / WHEN / THEN / ELSE / END (простой и вложенный)

```sql
-- вход
select case when a=1 then 'one' else 'other' end as l from t
-- результат
select
	case
		when a = 1
		then 'one'
		else 'other'
	end as l
from t
```

### `aggdistinct` / `collate` / `exists` / `paren` — Прочие выражения
`count(distinct x)` / `sum(all y)`; `collate Latin1_General_Bin2`; `exists (subquery)`; вложенные скобки и арифметика `((a+b)*(c-d))` сохраняют семантику.

### `2.13` — Многострочный динамический SQL (строка) переиндентируется
Тело строкового литерала с переносами сдвигается на +1 таб, закрывающая кавычка — на отступе объявляющей строки. Содержимое строки не «переформатируется» как SQL.

---

## I. DECLARE

### `2.15` / `declare` — Каждая переменная на своей строке (даже одна)
Типы: `varchar(50)` без внутренних пробелов, `decimal(18, 2)` — пробел после запятой. Хвостовой комментарий переменной сохраняется (`--` через два таба, `/* */` вклеивается).

```sql
-- вход
DECLARE @label nvarchar(50) = 'a, b, c' -- @x int, @y int
-- результат
declare
	@label nvarchar(50) = 'a, b, c'		-- @x int, @y int
```

```sql
-- вход
declare @a int = 1, --note
@b varchar(50)
-- результат
declare
	@a int = 1,		--note
	@b varchar(50)
```

---

## J. INSERT / UPDATE / DELETE

### `2.12` — INSERT … VALUES / INSERT … SELECT

```sql
-- вход
insert into dbo.t (a,b,c) values (1,2,3)
-- результат
insert into dbo.t(a, b, c)
values
	(1, 2, 3)
```

### `2.7` — UPDATE … SET (по строкам) … FROM … WHERE

```sql
-- вход
update w set w.a=t.a, w.b=t.b from t1 as w inner join t2 as t on t.id=w.id
-- результат
update w
set
	w.a = t.a,
	w.b = t.b
from t1 as w
	inner join t2 as t
		on t.id = w.id
```

### `delete` — DELETE FROM … / DELETE alias FROM …
Обе формы поддержаны, с FROM/JOIN и WHERE по тем же правилам, что и SELECT.

---

## K. CREATE TABLE / DROP TABLE

### `2.14` — CREATE TABLE: `(` на строке имени, типы в нижнем регистре
Колонки — каждая на своей строке (+1 таб). `drop table [if exists] name` поддержан.

```sql
-- вход
CREATE TABLE #x(
	process INT,
	process_status_id INT
)
-- результат
create table #x (
	process int,
	process_status_id int
)
```

---

## L. CTE · BEGIN/END · UNION

### `cte` — WITH … AS ( … )

```sql
-- вход
WITH cte AS (SELECT s.[Id] FROM [dbo].[Src] s WHERE s.[Val]>0)
SELECT c.[Id] FROM cte c
-- результат
with cte as (
	select
		s.[Id]
	from [dbo].[Src] as s
	where
		s.[Val] > 0
)
select
	c.[Id]
from cte as c
```

### `2.8` — BEGIN / END: пустая строка после begin и перед end, тело +1 таб

```sql
-- вход
BEGIN
DECLARE @a INT=1, @b INT=2
SELECT @a, @b
END
-- результат
begin

	declare
		@a int = 1,
		@b int = 2

	select
		@a,
		@b

end
```

### `2.11` — UNION / UNION ALL / EXCEPT / INTERSECT: пустая строка до и после

```sql
-- вход
select a from t1 union all select a from t2
-- результат
select
	a
from t1

union all

select
	a
from t2
```

---

## M. Фрагменты (частичные выделения)

### `fragment` — Форматирование неполных конструкций
Если выделение — не целый оператор, форматтер распознаёт: голый `WHERE` (или список условий без `where`), список колонок, цепочку `JOIN`, `GROUP BY`, `ORDER BY`. Раскладка совпадает с тем, как та же конструкция выглядит внутри полного SELECT — фрагмент можно вставить обратно без переиндентации.

```sql
-- вход (голый WHERE)
WHERE a.id = 1 AND b.x > 0
-- результат
where
	a.id = 1
	and b.x > 0
```

---

## N. Как писать тесты

**Формат кейса.** Добавьте запись в список `All()` в `TsqlFormatter.Tests/TestCases.cs`.
Табы в `Expected` — реальные символы `\t`. Сравнение игнорирует хвостовые пробелы строк
и переводы строк по краям, но учитывает отступы.

```csharp
new TestCase {
    Rule = "2.14", Name = "краткое описание",
    Input    = "CREATE TABLE #x(process INT)",
    Expected = "create table #x (\n\tprocess int\n)",
},
```

**Запуск.**

- `run-tests.bat` — все тесты
- `run-tests.bat --rule 2.6` — только правило `2.6` (значение поля `Rule`)
- `run-tests.bat -v` — печатать вывод каждого кейса

Каждый кейс дополнительно проверяется на **идемпотентность** — форматирование результата
ещё раз должно давать тот же текст. При падении печатается построчный diff (зелёным —
ожидаемое, красным — фактическое; табы показаны как `→`).

**Поле `Rule`** — это метка id из заголовков выше (`2.1`, `window`, `convert`, `stmtbound`…).
Она группирует вывод и позволяет фильтровать запуск. Можно вводить свои метки для новых правил.

---

_Справочник описывает поведение на текущем состоянии ветки `main`. При изменении правил обновляйте и его._
