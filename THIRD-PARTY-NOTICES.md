# Third-party notices

Interview Coach's own code is under the [MIT License](LICENSE). It uses the packages below, each under its own license. The list is of the
packages the projects reference directly; the packages they pull in are almost all MIT licensed as well (the license of every package in the build
was checked in the NuGet metadata when this file was written).

| Package | License |
|---|---|
| Anthropic (Anthropic SDK for .NET) | MIT |
| CommunityToolkit.Mvvm | MIT |
| DocumentFormat.OpenXml | MIT |
| Microsoft.EntityFrameworkCore.Sqlite and .Design | MIT |
| Microsoft.Extensions.AI, Microsoft.Extensions.AI.OpenAI, Microsoft.Extensions.Hosting | MIT |
| NAudio | MIT |
| PdfPig | Apache-2.0 |
| SQLitePCLRaw (the SQLite native library for .NET) | Apache-2.0 |
| System.Speech, System.Security.Cryptography.ProtectedData | MIT |
| **Microsoft.CognitiveServices.Speech** (Azure AI Speech SDK) | **Microsoft Software License Terms** (not MIT) |

## Azure AI Speech SDK

Dictation with Azure uses Microsoft's Speech SDK, which is licensed under Microsoft's own terms, reproduced in
[licenses/Microsoft-Cognitive-Services-Speech-SDK-LICENSE.txt](licenses/Microsoft-Cognitive-Services-Speech-SDK-LICENSE.txt) and installed with the
program. Those terms apply to that component, not the MIT license. Among other things they say the SDK may collect usage information and send it to
Microsoft, and they require anyone who receives the SDK with an application to agree to terms that protect Microsoft at least as much. By using the
Azure speech feature you accept them; you can avoid the SDK's use entirely by choosing the OpenAI or Windows options in Settings, or by typing.
(This is a summary, not legal advice: read the license itself.)
