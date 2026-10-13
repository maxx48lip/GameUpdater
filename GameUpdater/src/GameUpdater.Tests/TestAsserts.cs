// No namespace: matches Program.cs in this project (global namespace).

/// <summary>Minimal assert helpers for the NuGet-free test runner.</summary>
internal static class TestAsserts
{
    public static int Passed { get; private set; }
    public static readonly List<string> Failures = new();

    public static void True(bool condition, string name)
    {
        if (condition)
        {
            Passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            Failures.Add(name);
            Console.WriteLine($"  FAIL  {name}");
        }
    }

    public static void Equal<T>(T expected, T actual, string name)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            Failures.Add($"{name} — expected [{expected}], got [{actual}]");
            Console.WriteLine($"  FAIL  {name} — expected [{expected}], got [{actual}]");
        }
    }

    public static void SequenceEqual(IEnumerable<string> expected, IEnumerable<string> actual, string name) =>
        Equal(string.Join(" | ", expected), string.Join(" | ", actual), name);
}
