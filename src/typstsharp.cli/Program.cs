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
    """;

File.WriteAllText("input.typ", input);
// Use the constructor or FromFile
using var clientFile = new TypstCompiler("input.typ");

var sysInputs = new Dictionary<string, object>
{
    { "title", "This is file 1." },
    { "data", new DataObj { item = 17 } }
};
clientFile.SetSysInputs(sysInputs);
var outputFile = clientFile.Compile();

File.WriteAllBytes("output_file.pdf", outputFile.Buffers[0]);
Console.WriteLine("Compiled output_file.pdf from input.typ");


Console.WriteLine("\n--- Test 2: Compile from Source String ---");
var sourceString = """
    = Hello from Memory!
    
    This document was compiled directly from a string in memory.
    """;

using var clientSource = TypstCompiler.FromSource(sourceString);
var outputSource = clientSource.Compile();
File.WriteAllBytes("output_source.pdf", outputSource.Buffers[0]);
Console.WriteLine("Compiled output_source.pdf from string source");


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