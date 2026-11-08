namespace Tickbird.Core.Data.Sql;

/// <summary>
/// A text parameter sent to the server without a type, so the server reads it as the type of the column or function
/// argument it is used for (a number for a double precision column, a time for a timestamp or timestamptz column).
/// A plain string parameter is typed as text and fails for such columns.
/// </summary>
public readonly record struct UntypedText(string Value)
{
    public override string ToString() => Value;
}
