# Third-Party Notices

## SVGL Plugin Logos

The Windows plugin logo PNGs in `src/TypeWhisper.Windows/Resources/PluginLogos/` were generated from selected SVG files provided by the SVGL project:

- https://svgl.app/
- https://github.com/pheralb/svgl

SVGL is distributed under the MIT License. The included brand logos remain trademarks or registered trademarks of their respective owners. TypeWhisper uses them only to identify the corresponding plugin provider in the local app UI.

## NAudio WASAPI Capture

The prepare-once WASAPI capture implementation in `src/TypeWhisper.Windows/Services/WasapiAudioInputCapture.cs` adapts capture initialization and packet-reading logic from NAudio 2.2.1:

- https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Wasapi/WasapiCapture.cs
- Copyright (c) 2020 Mark Heath

NAudio is distributed under the MIT License:

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## VarCon English Spelling Variants

The English spelling maps in `src/TypeWhisper.Core/Resources/EnglishSpelling/`
are derived from VarCon 2020.12.07 as distributed by the `crate-ci/typos`
project at commit `5e38bf262baaf477ca9f241b3a7cd3d277fd7df8`:

- https://github.com/crate-ci/typos/tree/5e38bf262baaf477ca9f241b3a7cd3d277fd7df8/crates/varcon/assets
- https://wordlist.aspell.net/

Copyright 2000-2019 by Kevin Atkinson

Permission to use, copy, modify, distribute and sell this array, the associated
software, and its documentation for any purpose is hereby granted without fee,
provided that the above copyright notice appears in all copies and that both
that copyright notice and this permission notice appear in supporting
documentation. Kevin Atkinson makes no representations about the suitability of
this array for any purpose. It is provided "as is" without express or implied
warranty.

Copyright 2016 by Benjamin Titze

Permission to use, copy, modify, distribute and sell this array, the associated
software, and its documentation for any purpose is hereby granted without fee,
provided that the above copyright notice appears in all copies and that both
that copyright notice and this permission notice appear in supporting
documentation. Benjamin Titze makes no representations about the suitability of
this array for any purpose. It is provided "as is" without express or implied
warranty.

Since the original word lists come from the Ispell distribution:

Copyright 1993, Geoff Kuenning, Granada Hills, CA
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. All modifications to the source code must be clearly marked as such. Binary
   redistributions based on modified source code must be clearly marked as
   modified versions in the documentation and/or other materials provided with
   the distribution.
(clause 4 removed with permission from Geoff Kuenning)

5. The name of Geoff Kuenning may not be used to endorse or promote products
   derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY GEOFF KUENNING AND CONTRIBUTORS "AS IS" AND ANY
EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL GEOFF KUENNING OR CONTRIBUTORS BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
