using System.Text;
using Microsoft.AspNetCore.Mvc;
using XmlMapping.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var mappingsPath = builder.Configuration["Mappings:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "Mappings");
builder.Services.AddSingleton(MappingRegistry.LoadFrom(mappingsPath));

// Partner payloads are untrusted: cap the request size.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1_000_000);

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapGet("/api/mappings", (MappingRegistry registry) => registry.Names.Order().ToArray())
   .WithSummary("Lists the configured partner mappings.");

app.MapPost("/api/mappings/{partner}/transform", async (string partner, HttpRequest request, MappingRegistry registry) =>
{
    if (!registry.TryGet(partner, out var mapping))
        return Results.Problem($"No mapping is configured for partner '{partner}'.", statusCode: StatusCodes.Status404NotFound);

    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var xml = await reader.ReadToEndAsync();

    var result = MappingEngine.Execute(mapping, xml);
    if (result.Success)
        return Results.Content(result.Xml!, "application/xml");

    var stage = result.FailedStage == MappingStage.Input ? "input" : "output";
    return Results.Problem(
        title: result.FailedStage == MappingStage.Input ? "Input document is not valid" : "Mapping produced an invalid document",
        detail: $"{result.Issues.Count} validation issue(s) at the {stage} stage.",
        statusCode: result.FailedStage == MappingStage.Input ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status500InternalServerError,
        extensions: new Dictionary<string, object?> { ["stage"] = stage, ["issues"] = result.Issues });
})
.Accepts<string>("application/xml")
.WithSummary("Validates a partner XML document, transforms it to the internal format and validates the result.");

app.Run();

public partial class Program;
