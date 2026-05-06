using System.Reflection;

var asmPath = @"C:\Program Files (x86)\ATAS Platform\ATAS.Indicators.dll";
var asm = Assembly.LoadFrom(asmPath);

var chartObject = asm.GetType("ATAS.Indicators.ChartObject");
if (chartObject is null)
{
    Console.WriteLine("ChartObject not found.");
    return;
}

var methods = chartObject.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
    .Where(m => m.Name is "ProcessMouseClick" or "ProcessMouseDown" or "ProcessMouseUp" or "ProcessMouseMove" or "ProcessMouseDoubleClick" or "GetCursor")
    .OrderBy(m => m.Name);

foreach (var m in methods)
{
    var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"));
    Console.WriteLine($"{m.ReturnType.FullName} {m.Name}({ps})");
}
