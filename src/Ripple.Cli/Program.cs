using Ripple.Core.Extraction;

var repo = args.Length > 0 ? args[0] : "../work/eshop";
string[] services =
[
    "src/Ordering.API/Ordering.API.csproj",
    "src/Catalog.API/Catalog.API.csproj",
    "src/Basket.API/Basket.API.csproj",
    "src/Webhooks.API/Webhooks.API.csproj",
    "src/WebApp/WebApp.csproj",
    "src/PaymentProcessor/PaymentProcessor.csproj",
    "src/OrderProcessor/OrderProcessor.csproj",
];
var loaded = await SolutionLoader.LoadAsync(repo, services, Console.WriteLine);
foreach (var (name, project) in loaded.Services)
{
    var comp = await project.GetCompilationAsync();
    var errors = comp!.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
    Console.WriteLine($"{name}: {errors.Count} compile errors");
    foreach (var e in errors.Take(3)) Console.WriteLine($"  {e}");
}
