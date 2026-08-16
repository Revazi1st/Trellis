using System.Linq.Expressions;
using System.Reflection;

namespace Trellis.SharedKernel.Specifications;

/// <summary>
/// Minimal hand-rolled specification (ADR-0017): reusable, composable query intent declared in the domain.
/// Supports Where (AND-composed), OrderBy/ThenBy, and paging. Includes are deliberately absent —
/// aggregates always load whole (EF AutoInclude on child navigations). Needs beyond this shape are the
/// signal to switch to Ardalis.Specification, which shares the same usage pattern.
/// </summary>
public abstract class Specification<T>
{
    private readonly List<Expression<Func<T, bool>>> _criteria = [];
    private readonly List<(LambdaExpression KeySelector, bool Descending)> _orderings = [];

    public int? Skip { get; private set; }

    public int? Take { get; private set; }

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

    protected void Paginate(int skip, int take)
    {
        Skip = skip;
        Take = take;
    }
}
