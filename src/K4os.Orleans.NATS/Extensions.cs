// ReSharper disable CheckNamespace

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace K4os.Orleans.NATS;

internal static class Extensions
{
    public static string ToNatsId(this string id) => Url64.EncodeString(id);

    public static string FromNatsId(this string id) => Url64.DecodeString(id);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T NotLessThan<T>(this T value, T minimum) =>
        Comparer<T>.Default.Compare(value, minimum) < 0 ? minimum : value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T NotMoreThan<T>(this T value, T maximum) =>
        Comparer<T>.Default.Compare(value, maximum) > 0 ? maximum : value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ThrowIfNull<T>(
        [NotNull] this T? expression,
        [CallerArgumentExpression(nameof(expression))]
        string? expressionText = null)
        where T: class =>
        expression ?? ThrowNullArgumentException<T>(expressionText);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ThrowIfNull<T>(
        [NotNull] this T? expression,
        [CallerArgumentExpression(nameof(expression))]
        string? expressionText = null)
        where T: struct =>
        expression ?? ThrowNullArgumentException<T>(expressionText);

    public static bool IsEmpty([NotNullWhen(false)] this string? text) =>
        string.IsNullOrEmpty(text);

    public static bool IsBlank([NotNullWhen(false)] this string? text) =>
        string.IsNullOrWhiteSpace(text);

    public static string? NullIfEmpty(this string? text) => 
        string.IsNullOrEmpty(text) ? null : text;

    public static string? NullIfBlank(this string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text;

    [MethodImpl(MethodImplOptions.NoInlining), DoesNotReturn]
    private static T ThrowNullArgumentException<T>(string? expressionText) =>
        throw new ArgumentNullException(expressionText);

    public static async Task ForEach<T>(this IAsyncEnumerable<T> stream, Action<T> apply, CancellationToken token)
    {
        await foreach (var item in stream.WithCancellation(token)) apply(item);
    }

    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> stream, CancellationToken token)
    {
        var list = new List<T>();
        await foreach (var item in stream.WithCancellation(token)) list.Add(item);
        return list;
    }
}
