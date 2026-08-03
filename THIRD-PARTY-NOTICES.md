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
