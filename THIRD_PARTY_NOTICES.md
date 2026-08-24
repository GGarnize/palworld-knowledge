# Third-Party Notices

This project's own code and license are in [LICENSE](LICENSE). This file
covers third-party code included in this repository.

## palworld-atlas-data

The `.NET` extractor in `extractor/` (data-table reading via `RowReader`,
PAK/package loading via `PakWorkspace`, the `probe`/`normalize-*` command
scaffolding in `Program.cs`, and related project setup) is derived from and
extends:

- Project: palworld-atlas-data
- Repository: https://github.com/Awy64/palworld-atlas-data
- Copyright (c) 2026 Adam Young
- License: MIT

This repository is an independent project built on top of that codebase -
it is not affiliated with, endorsed by, or a fork tracked against
`palworld-atlas-data`'s history. The `validate-data`/`build-metadata`
commands, `DataValidator`, `BuildMetadataGenerator`, the JSON Schemas in
`schemas/`, and the `data/*.json` datasets themselves are new work added in
this repository, not attributed to the upstream project.

The original MIT copyright and permission notice from `palworld-atlas-data`
is reproduced below, as required by the MIT License:

```
MIT License

Copyright (c) 2026 Adam Young

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Third-party NuGet packages

The extractor also depends on the following third-party libraries, each
under its own license (see the respective package for full terms):

- [CUE4Parse](https://github.com/FabianFG/CUE4Parse) - Unreal Engine asset parsing
- [JsonSchema.Net](https://github.com/json-everything/json-everything) - JSON Schema 2020-12 validation
- [Newtonsoft.Json](https://www.newtonsoft.com/json)
- [Serilog.Sinks.Console](https://github.com/serilog/serilog-sinks-console)
