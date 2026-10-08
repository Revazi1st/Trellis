using System.Linq.Expressions;
using System.Reflection;

namespace Trellis.SharedKernel.Specifications;

/// <summary>
/// Minimal hand-rolled specification (ADR-0017): reusable, composable query intent declared in the domain.
/// Supports Where (AND-composed), OrderBy/ThenBy, and paging. Includes are deliberately absent —
/// aggregates always load whole (EF AutoInclude on child navigations). Needs beyond this shape are the
/// signal to switch to Ardalis.Specification, which shares the same usage pattern.
/// <para>
/// <b>Always end an ordering with <c>Id</c></b> (<c>OrderBy(x =&gt; x.DueDate); OrderBy(x =&gt; x.Id);</c>).
/// Without a unique last key the order of rows that tie on the earlier keys is up to the database and
/// may differ between two queries, so consecutive pages can repeat one row and skip another. A
/// repository that pages a specification calls <see cref="EnsureCanBePaged"/>, which enforces it.
/// </para>
/// </summary>
public abstract class Specification<T>
{
    private readonly List<Expression<Func<T, bool>>> _criteria = [];
    private readonly List<(LambdaExpression KeySelector, bool Descending)> _orderings = [];

    public int? Skip { get; private set; }

    public int? Take { get; private set; }

    /// <summary>True when the specification pages itself (<see cref="Paginate"/>).</summary>
    public bool IsPaged => Skip is not null || Take is not null;

    /// <summary>
    /// True when the last ordering is on the entity's <c>Id</c> — the unique tiebreaker that makes the
    /// order total, so page boundaries are stable.
    /// </summary>
    public bool EndsWithIdTiebreaker => _orderings.Count > 0 && IsIdOfTheEntity(_orderings[^1].KeySelector);

    /// <summary>
    /// Throws unless a caller may page this specification with its own Skip/Take: the specification must
    /// not page itself (the two would apply on top of each other, silently), and its ordering must end
    /// with <c>Id</c>. Both are programming errors in the specification, not expected failures — the
    /// first test that pages it reports them.
    /// </summary>
    public void EnsureCanBePaged()
    {
        if (IsPaged)
        {
            throw new InvalidOperationException(
                $"{GetType().Name} already pages itself (Paginate) and cannot be paged again; page in the specification or in the repository, not both.");
        }

        if (!EndsWithIdTiebreaker)
        {
            throw new InvalidOperationException(
                $"{GetType().Name} must end its ordering with OrderBy(x => x.Id) to be paged: rows that tie on the other keys could otherwise repeat or vanish across pages.");
        }
    }

    /// <summary>Applies only the filter criteria (no ordering or paging) — used to count the full matching set.</summary>
    public IQueryable<T> ApplyCriteria(IQueryable<T> query)
    {
        foreach (Expression<Func<T, bool>> criterion in _criteria)
        {
            query = query.Where(criterion);
        }

        return query;
    }

    /// <summary>Applies the specification to a queryable; usable by EF (expression-tree preserving) and, via AsQueryable, in-memory.</summary>
    public IQueryable<T> ApplyTo(IQueryable<T> query)
    {
        query = ApplyCriteria(query);

        bool isFirstOrdering = true;
        foreach ((LambdaExpression keySelector, bool descending) in _orderings)
        {
            string methodName = (isFirstOrdering, descending) switch
            {
                (true, false) => nameof(Queryable.OrderBy),
                (true, true) => nameof(Queryable.OrderByDescending),
                (false, false) => nameof(Queryable.ThenBy),
                (false, true) => nameof(Queryable.ThenByDescending),
            };

            MethodInfo method = typeof(Queryable)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == methodName && m.GetParameters().Length == 2)
                .MakeGenericMethod(typeof(T), keySelector.ReturnType);

            query = (IQueryable<T>)method.Invoke(null, [query, keySelector])!;
            isFirstOrdering = false;
        }

        if (Skip is { } skip)
        {
            query = query.Skip(skip);
        }

        if (Take is { } take)
        {
            query = query.Take(take);
        }

        return query;
    }

    /// <summary>In-memory evaluation for unit tests and collections.</summary>
    public IReadOnlyList<T> Evaluate(IEnumerable<T> source)
        => [.. ApplyTo(source.AsQueryable())];

    protected void Where(Expression<Func<T, bool>> predicate) => _criteria.Add(predicate);

    protected void OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => _orderings.Add((keySelector, false));

    protected void OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => _orderings.Add((keySelector, true));

    /// <summary>
    /// A specification that is always a fixed window ("the first ten"). A caller that pages — such as a
    /// repository's <c>ListPagedAsync</c> — refuses a specification that already pages itself.
    /// </summary>
    protected void Paginate(int skip, int take)
    {
        Skip = skip;
        Take = take;
    }

    private static bool IsIdOfTheEntity(LambdaExpression keySelector)
    {
        Expression body = keySelector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
        {
            body = conversion.Operand;
        }

        return body is MemberExpression { Member.Name: "Id", Expression: ParameterExpression };
    }
}
