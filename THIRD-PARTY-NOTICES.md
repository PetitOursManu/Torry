# Composants tiers inclus dans PetitOurs.Torry.dll

`PetitOurs.Torry.dll` contient, fusionnés par ILRepack (dotnet-ilrepack 2.0.44), les composants suivants,
tous distribués sous licence MIT. Les mentions de copyright et le texte de la licence MIT doivent
accompagner toute redistribution de la DLL.

| Composant | Version | Projet | Copyright |
|---|---|---|---|
| MonoTorrent | 3.0.2 | https://github.com/alanmcgovern/monotorrent | Copyright (C) 2006 Alan McGovern, Olivier Dufour |
| ReusableTasks | 4.0.0 | https://github.com/alanmcgovern/ReusableTasks | Copyright (C) 2019 Alan McGovern |
| Mono.Nat | 3.0.0 | https://github.com/mono/Mono.Nat | Copyright (C) 2006 Alan McGovern, (C) 2007 Ben Motmans, (C) 2013 Nicholas Terry |

Les références de compilation `MediaBrowser.Server.Core` / `MediaBrowser.Common` (Emby) ne sont **pas**
incluses dans la DLL : elles sont fournies par le serveur Emby à l'exécution.

## Texte de la licence MIT

```
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
