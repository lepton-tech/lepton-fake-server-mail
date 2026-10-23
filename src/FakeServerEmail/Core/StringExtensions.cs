namespace FakeServerEmail.Core;

public static class StringExtensions
{
    public static string? NullIfEmpty(this string? value) => string.IsNullOrEmpty(value) ? null : value;
}
