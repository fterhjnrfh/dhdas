using System.Reflection;
var asm = Assembly.LoadFrom(@"C:\Users\26453\.nuget\packages\zstdsharp.port\0.8.1\lib\net8.0\ZstdSharp.dll");
foreach (var t in asm.GetTypes().Where(t => t.IsEnum && t.Name.Contains("Parameter", StringComparison.OrdinalIgnoreCase)).OrderBy(t=>t.FullName))
{
    Console.WriteLine(t.FullName);
    foreach (var n in Enum.GetNames(t)) Console.WriteLine("  " + n);
}
