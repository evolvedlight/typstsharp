using typstsharp;
using System;

Console.WriteLine("Starting test...");
try
{
    Console.WriteLine("Creating compiler...");
    using var compiler = TypstCompiler.FromSource("#set page(width: 100mm, height: 100mm)\nHello World!");
    Console.WriteLine("Compiling...");
    var result = compiler.Compile();
    Console.WriteLine($"Compiled {result.Buffers.Count} buffers.");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine(ex.ToString());
}
Console.WriteLine("Test finished.");
