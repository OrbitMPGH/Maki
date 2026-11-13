# Third-party notices

Maki's own code is GPL-3.0 (see [LICENSE](LICENSE)). Page Enhance and the reader's "Hide credit
pages" setting use the third-party works below. Each keeps its own licence, and none of them is relicensed by being used here.

## Server upscaling models

These are neural network weights, not code. Maki does not contain them: an admin switches on server
upscaling and the server downloads the files on first use from the `upscale-models-v1` release at
https://github.com/OrbitMPGH/Maki/releases/tag/upscale-models-v1. Each model stays a separate file, and
the release carries a `LICENSES.txt` with the full licence texts. The same texts are kept in
`distribution/upscale-models/licenses/`.

### IllustrationJaNai V3 denoise, SPAN-S 2x

- File: `span-s-2x.onnx`
- Author: the-database (MangaJaNai)
- Licence: Creative Commons Attribution-NonCommercial 4.0 International (CC BY-NC 4.0)
- Source: https://github.com/the-database/MangaJaNai/releases/tag/3.0.0
- Maki is distributed free of charge and without commercial use. **This model is not licensed for
  commercial use, and that term binds anyone who receives the file, including anyone who redistributes
  Maki, a container image that downloads it, or the file itself.** If you use Maki in a commercial
  setting, leave this model switched off and do not download it.

### Real-ESRGAN realesr-animevideov3

- File: `realesrgan-av3-4x.onnx`
- Author: Xintao Wang and the Real-ESRGAN authors
- Licence: BSD 3-Clause
- Source: https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.2.5.0
- Converted from the original `.pth` file to ONNX (opset 17, fp32, dynamic height and width) by
  `distribution/upscale-models/prepare.py`. The weights are unchanged.

## Text recognition for credit pages

The reader reads the text on repeated pages at a chapter's edges to tell scanlator credit pages from
publisher pages. Maki ships these two models beside the server, in `OcrModels/`, with the licence
text in `OcrModels/LICENSE-Apache-2.0.txt`. They are neural network weights, not code, and are
unchanged.

### PaddleOCR PP-OCRv3 text detection and recognition

- Files: `ch_PP-OCRv3_det_infer.onnx`, `ch_PP-OCRv3_rec_infer.onnx`
- Authors: the PaddlePaddle Authors (PaddleOCR)
- Licence: Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0)
- Source: https://github.com/PaddlePaddle/PaddleOCR
- The ONNX files are the ones RapidOCR 1.2.3 ships (Apache License 2.0,
  https://github.com/RapidAI/RapidOCR).

## JPEG XL encoder for upscaled pages

Upscaled colour pages are stored as JPEG XL with libjxl, which Maki ships as separate shared libraries
beside the server (`jxl`, `jxl_cms` and the three `brotli` libraries). Linux builds compile them from
the sources below (`distribution/libjxl/build.sh`), with Highway and skcms linked into libjxl; Windows
builds use the libjxl project's own release DLLs.

### libjxl 0.12.0

- Authors: the JPEG XL Project Authors
- Licence: BSD 3-Clause
- Source: https://github.com/libjxl/libjxl/tree/v0.12.0 (commit `a7a9c787341cf703dede03c2009fa460cae5e5df`)

```
Copyright (c) the JPEG XL Project Authors.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### Brotli

- Authors: the Brotli Authors
- Licence: MIT
- Source: https://github.com/google/brotli, commit `028fb5a23661f123017c060daa546b55cf4bde29` (libjxl's submodule)

```
Copyright (c) 2009, 2010, 2013-2016 by the Brotli Authors.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

### Highway

- Authors: the Highway Project Authors
- Licence: Apache 2.0 or BSD 3-Clause, used here under BSD 3-Clause
- Source: https://github.com/google/highway, commit `457c891775a7397bdb0376bb1031e6e027af1c48` (libjxl's submodule)

```
Copyright (c) The Highway Project Authors. All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

1.  Redistributions of source code must retain the above copyright notice, this
    list of conditions and the following disclaimer.

2.  Redistributions in binary form must reproduce the above copyright notice,
    this list of conditions and the following disclaimer in the documentation
    and/or other materials provided with the distribution.

3.  Neither the name of the copyright holder nor the names of its
    contributors may be used to endorse or promote products derived from
    this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### skcms

- Authors: Google Inc.
- Licence: BSD 3-Clause
- Source: https://skia.googlesource.com/skcms, commit `96d9171c94b937a1b5f0293de7309ac16311b722` (libjxl's submodule)

```
// Copyright (c) 2018 Google Inc. All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
//
//    * Redistributions of source code must retain the above copyright
// notice, this list of conditions and the following disclaimer.
//    * Redistributions in binary form must reproduce the above
// copyright notice, this list of conditions and the following disclaimer
// in the documentation and/or other materials provided with the
// distribution.
//    * Neither the name of Google Inc. nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

--------------------------------------------------------------------------------
```

## Browser shaders

### Anime4K

- Files: `frontend/src/lib/enhance/shaders/Anime4K_Upscale_CNN_x2_S.glsl`,
  `Anime4K_Upscale_CNN_x2_M.glsl` and `Anime4K_Restore_CNN_M.glsl`, vendored unmodified
- Author: bloc97
- Licence: MIT. The copyright and permission notice is kept in the header of each file.
- Source: https://github.com/bloc97/Anime4K, commit `7684e9586f8dcc738af08a1cdceb024cc184f426`
