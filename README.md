# XML Mapping Service

[![CI](https://github.com/hamzamehmood46/xml-mapping-service-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/hamzamehmood46/xml-mapping-service-dotnet/actions/workflows/ci.yml)

**Live demo: [xml-mapping-service-dotnet.onrender.com/swagger](https://xml-mapping-service-dotnet.onrender.com/swagger)** (Swagger UI). Try GET /api/mappings, then POST /api/mappings/acme-po/transform with a partner XML. Hosted on Render's free tier, so the first load after idle can take about a minute; demo data resets on restart.

A **.NET 8** service that converts a trading partner's XML into your internal format: **validate the input against the partner's XSD, transform with XSLT, then validate the output against your XSD.** Adding a new partner is a folder of three files, with no code changes.

Partner integrations fail in two ways: the partner sends something malformed, or someone edits a mapping and quietly breaks the output. This service catches both *before* bad data reaches downstream systems.

```
partner XML ──► [1] validate vs source.xsd ──► [2] XSLT (map.xslt) ──► [3] validate vs target.xsd ──► internal XML
                    │ fail: 422, stage=input                              │ fail: 500, stage=output
                    ▼                                                     ▼
          line/position for every issue                       "the mapping is broken", not "the partner is"
```

## Add a partner

```
src/XmlMapping.Api/Mappings/<partner>/
  source.xsd   what the partner sends
  target.xsd   what downstream systems expect
  map.xslt     the transformation (XSLT 1.0)
```

The registry loads every folder at startup. A sample `acme-po` mapping (purchase order -> internal order, with computed line totals) is included.

## API

```bash
dotnet run --project src/XmlMapping.Api
```

```bash
curl -X POST http://localhost:5000/api/mappings/acme-po/transform \
  -H "Content-Type: application/xml" --data-binary @sample.xml
```

| Response | Meaning |
|---|---|
| `200` + XML | Mapped and validated |
| `422` + problem details | Input is malformed or violates the source schema. `issues[]` lists each message with line and position. |
| `500` + problem details (`stage: output`) | The mapping produced an invalid document: the *mapping* is at fault |
| `404` | Unknown partner |

`GET /api/mappings` lists configured partners; `/swagger` documents the API.

## Security

Partner XML is untrusted input, so:

- **XXE and entity-expansion attacks are blocked**: DTD processing is prohibited and no `XmlResolver` is set (covered by a test that attempts to read a local file through an external entity).
- **XSLT is sandboxed**: `document()` and embedded scripts are disabled, so a mapping cannot read files or execute code.
- Request bodies are capped at 1 MB and issue lists at 50.

## Project layout

```
src/XmlMapping.Core   Validation + transformation engine, mapping registry (no web dependencies)
src/XmlMapping.Api    Minimal API host and the sample mapping
tests/                xUnit: engine tests and end-to-end API tests
```

## Tests

```bash
dotnet test
```

11 tests: correct totals, schema violations with positions, missing elements, malformed XML, **XXE attempts**, **a deliberately broken mapping caught by output validation**, and the HTTP contract.

## License

MIT
