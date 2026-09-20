namespace Vouch.Application.Common;

/// <summary>
/// Step 14: Generic pagination envelope for list responses.
/// Returned by any endpoint that supports page/pageSize query params.
/// </summary>
/// <param name="Items">The page of data.</param>
/// <param name="Page">Current page (1-indexed).</param>
/// <param name="PageSize">Items per page.</param>
/// <param name="TotalCount">Total items matching the query (across all pages).</param>
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount
)
{
    /// <summary>Total number of pages.</summary>
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);

    /// <summary>True when a next page exists.</summary>
    public bool HasNextPage => Page < TotalPages;

    /// <summary>True when a previous page exists.</summary>
    public bool HasPreviousPage => Page > 1;
}
