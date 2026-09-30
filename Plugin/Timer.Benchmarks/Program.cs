using BenchmarkDotNet.Running;

namespace Source2Surf.Timer.Benchmarks;

internal static class Program
{
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
