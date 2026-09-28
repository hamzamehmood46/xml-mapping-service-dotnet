using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using XmlMapping.Core;

namespace XmlMapping.Tests;

public static class Samples
{
    public const string ValidPo = """
        <PurchaseOrder xmlns="urn:acme:po" orderNo="PO-1001" date="2026-03-04">
          <Buyer><Name>Dana Lee</Name><Email>dana@example.com</Email></Buyer>
          <Items>
            <Item sku="A-1" qty="2" price="9.99"/>
            <Item sku="B-7" qty="3" price="4.50"/>
          </Items>
        </PurchaseOrder>
        """;

    public static string MappingsDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "XmlMapping.Api", "Mappings")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "src", "XmlMapping.Api", "Mappings");
        }
    }
}

public class MappingEngineTests
{
    private static readonly MappingRegistry Registry = MappingRegistry.LoadFrom(Samples.MappingsDir);

    private static MappingResult Run(string xml) =>
        MappingEngine.Execute(Registry.TryGet("acme-po", out var m) ? m : throw new InvalidOperationException(), xml);

    [Fact]
    public void Registry_loads_the_sample_partner()
    {
        Assert.Contains("acme-po", Registry.Names);
    }

    [Fact]
    public void Valid_document_is_mapped_with_correct_totals()
    {
        var result = Run(Samples.ValidPo);

        Assert.True(result.Success, string.Join("; ", result.Issues.Select(i => i.Message)));
        Assert.Contains("id=\"PO-1001\"", result.Xml);
        Assert.Contains("source=\"ACME\"", result.Xml);
        Assert.Contains("lineTotal=\"19.98\"", result.Xml);
        Assert.Contains("lineTotal=\"13.50\"", result.Xml);
        Assert.Contains(">33.48<", result.Xml);
    }

    [Fact]
    public void Schema_violations_are_reported_with_positions()
    {
        var bad = Samples.ValidPo.Replace("qty=\"2\"", "qty=\"-2\"").Replace("PO-1001", "1001");

        var result = Run(bad);

        Assert.False(result.Success);
        Assert.Equal(MappingStage.Input, result.FailedStage);
        Assert.True(result.Issues.Count >= 2);
        Assert.All(result.Issues, i => Assert.True(i.Line > 0));
    }

    [Fact]
    public void Missing_required_element_is_rejected()
    {
        var bad = Samples.ValidPo.Replace("<Email>dana@example.com</Email>", "");

        var result = Run(bad);

        Assert.False(result.Success);
        Assert.Equal(MappingStage.Input, result.FailedStage);
    }

    [Fact]
    public void Malformed_xml_is_reported_not_thrown()
    {
        var result = Run("<PurchaseOrder><oops></PurchaseOrder>");

        Assert.False(result.Success);
        Assert.Equal(MappingStage.Input, result.FailedStage);
    }

    [Fact]
    public void Doctype_and_external_entities_are_blocked()
    {
        var xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE PurchaseOrder [ <!ENTITY secret SYSTEM "file:///C:/Windows/win.ini"> ]>
            <PurchaseOrder xmlns="urn:acme:po" orderNo="PO-1" date="2026-03-04">
              <Buyer><Name>&secret;</Name><Email>a@b.c</Email></Buyer>
              <Items><Item sku="A" qty="1" price="1.00"/></Items>
            </PurchaseOrder>
            """;

        var result = Run(xxe);

        Assert.False(result.Success);
        Assert.Equal(MappingStage.Input, result.FailedStage);
        Assert.DoesNotContain("[fonts]", result.Xml ?? "");
    }

    [Fact]
    public void A_broken_mapping_is_caught_by_output_validation()
    {
        // Same schemas, but an XSLT that forgets the required <Total> element.
        var dir = Path.Combine(Samples.MappingsDir, "acme-po");
        var brokenXslt = File.ReadAllText(Path.Combine(dir, "map.xslt"))
            .Replace("<Total>", "<!--").Replace("</Total>", "-->");
        var broken = new MappingDefinition("broken",
            File.ReadAllText(Path.Combine(dir, "source.xsd")),
            File.ReadAllText(Path.Combine(dir, "target.xsd")),
            brokenXslt);

        var result = MappingEngine.Execute(broken, Samples.ValidPo);

        Assert.False(result.Success);
        Assert.Equal(MappingStage.Output, result.FailedStage);
    }
}

public class MappingApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> PostAsync(string partner, string xml) =>
        _client.PostAsync($"/api/mappings/{partner}/transform", new StringContent(xml, Encoding.UTF8, "application/xml"));

    [Fact]
    public async Task Lists_configured_partners()
    {
        var names = await _client.GetStringAsync("/api/mappings");

        Assert.Contains("acme-po", names);
    }

    [Fact]
    public async Task Valid_document_returns_transformed_xml()
    {
        var response = await PostAsync("acme-po", Samples.ValidPo);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("<Total>33.48</Total>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Invalid_document_returns_422_with_issue_details()
    {
        var response = await PostAsync("acme-po", Samples.ValidPo.Replace("PO-1001", "bad"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("input", doc.RootElement.GetProperty("stage").GetString());
        Assert.True(doc.RootElement.GetProperty("issues").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Unknown_partner_returns_404()
    {
        var response = await PostAsync("nobody", Samples.ValidPo);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
