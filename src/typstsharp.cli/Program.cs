using System;
using System.Collections.Generic;
using System.Diagnostics;
using typstsharp;

Console.WriteLine("--- Test 1: Compile from File ---");
var input = """
    #let title = sys.inputs.title
    #let data = json(bytes(sys.inputs.data))

    = This is a sample typst document

    Time to import 
    

    = Title is #title

    Data item is #data.item and this is a number

    A things is #data.things

    All inputs: #sys.inputs
    """;

File.WriteAllText("input.typ", input);
// Use the constructor or FromFile
using var clientFile = new TypstCompiler("input.typ");

var sysInputs = new Dictionary<string, string>
{
    { "title", "This is file 1." },
    { "data", System.Text.Json.JsonSerializer.Serialize(new DataObj { item = 17 }) }
};
clientFile.SetSysInputs(sysInputs);
var pdfResult = clientFile.CompilePdf("output_file.pdf");
Console.WriteLine($"Compiled output_file.pdf from input.typ ({pdfResult.Length} bytes)");


Console.WriteLine("\n--- Test 2: Compile from Source String ---");
var sourceString = """
    = Hello from Memory!
    
    This document was compiled directly from a string in memory.
    """;

// One-liner static helper
TypstCompiler.CompilePdf(sourceString).Save("output_source.pdf");
Console.WriteLine("Compiled output_source.pdf directly from string source");


Console.WriteLine("\n--- Test 3: Compile Single SVG ---");
var formula = """
    #set page(width: auto, height: auto, margin: 5pt)
    $ integral_0^infinity e^(-x^2) dif x = sqrt(pi)/2 $
    """;

string svg = TypstCompiler.CompileSvg(formula);
TypstCompiler.CompileSvg(formula).Save("output_formula.svg");
Console.WriteLine($"Compiled output_formula.svg ({svg.Length} characters)");


// open output.pdf via windows
if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
{
    Process.Start(new ProcessStartInfo("output_file.pdf") { UseShellExecute = true });
}

internal class DataObj
{
    public int item { get; set; }
    public string? things { get; set; }
}