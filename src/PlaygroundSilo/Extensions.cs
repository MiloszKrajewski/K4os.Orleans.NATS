namespace PlaygroundSilo;

public static class Extensions
{
    public static T NotLessThan<T>(this T value, T minimum) => 
        Comparer<T>.Default.Compare(value, minimum) < 0 ? minimum : value;
}
