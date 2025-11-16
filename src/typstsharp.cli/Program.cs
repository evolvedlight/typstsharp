using typstsharp;

var total = 0;
using var client = new TypstClient("= Hello");

const int iterations = 1000;
var sw = System.Diagnostics.Stopwatch.StartNew();
for (int i = 0; i < iterations; i++)
{
    var output = client.Compile();
    total++;
}

// check unmanaged memory
Console.WriteLine($"Total: {total} in {sw.ElapsedMilliseconds} ms");

