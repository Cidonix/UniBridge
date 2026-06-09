UniBridge Third Party Notices

This document lists third-party software components bundled with, embedded in,
or required by UniBridge as of the current package build. These components are
licensed by their respective owners. UniBridge itself is licensed separately by
Cidonix; see LICENSE.md.

Component Name: Microsoft Roslyn
Bundled Files:
- Plugins/CodeAnalysis/Microsoft.CodeAnalysis.dll
- Plugins/CodeAnalysis/Microsoft.CodeAnalysis.CSharp.dll

License Type: MIT

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to use,
copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Project: https://github.com/dotnet/roslyn

---

Component Name: Microsoft .NET Libraries
Bundled Files:
- Plugins/Shared/System.Collections.Immutable.dll
- Plugins/Shared/System.Reflection.Metadata.dll

Embedded In:
- RelayApp~/unibridge_relay_win.exe (self-contained .NET runtime build)
- RelayApp~/unibridge_relay_linux (self-contained .NET runtime build)
- RelayApp~/unibridge_relay_mac_x64 (self-contained .NET runtime build)
- RelayApp~/unibridge_relay_mac_arm64 (self-contained .NET runtime build)

License Type: MIT

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to use,
copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Project: https://github.com/dotnet/runtime

---

Component Name: Newtonsoft.Json
Required Package:
- com.unity.nuget.newtonsoft-json

License Type: MIT

Copyright (c) 2007 James Newton-King

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to use,
copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Project: https://github.com/JamesNK/Newtonsoft.Json

---

Unity Dependencies

UniBridge is a Unity Editor package and depends on Unity packages/modules
declared in package.json, including Unity UIElements, UnityWebRequest, 2D Sprite,
and Unity's Newtonsoft.Json package wrapper. These are provided by Unity
Technologies under Unity's applicable terms and are not sublicensed by Cidonix.

Unity is a trademark of Unity Technologies.
