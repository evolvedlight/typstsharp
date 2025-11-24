using System;
using System.Collections.Generic;
using System.Diagnostics;
using typstsharp;

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
using var client = new TypstCompiler("input.typ");

var sw = Stopwatch.StartNew();

var sysInputs = new Dictionary<string, object>
{
    { "title", "This is file 1." },
    { "data", new DataObj { item = 17 } }
};
client.SetSysInputs(sysInputs);
var output = client.Compile();

File.WriteAllBytes("output.pdf", output.Buffers[0]);

// open output.pdf via windows
if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
{
    Process.Start(new ProcessStartInfo("output.pdf") { UseShellExecute = true });
}

internal class DataObj
{
    public int item { get; set; }
    public string things { get; set; }
}