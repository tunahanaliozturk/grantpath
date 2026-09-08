using BenchmarkDotNet.Running;

// dotnet run -c Release --project benchmarks/GrantPath.Benchmarks
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Entry point marker, so the switcher has an assembly to scan.</summary>
public partial class Program;
