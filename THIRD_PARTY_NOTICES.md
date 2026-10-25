# Third-party notices

Maki's own code is GPL-3.0 (see [LICENSE](LICENSE)). The Page Enhance feature uses the third-party
works below. Each keeps its own licence, and none of them is relicensed by being used here.

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

### waifu2x swin_unet art (noise 1, 2x)

- File: `waifu2x-art-2x.onnx`
- Author: nagadomi
- Licence: MIT (the waifu2x and nunif repository)
- Source: https://github.com/nagadomi/nunif, model published at https://unlimited.waifu2x.net/
- The repository's MIT licence covers its code. The model weights carry no licence beyond that, and are
  redistributed here as published.

## Browser shaders

### Anime4K

- Files: `frontend/src/lib/enhance/shaders/Anime4K_Upscale_CNN_x2_S.glsl`,
  `Anime4K_Upscale_CNN_x2_M.glsl` and `Anime4K_Restore_CNN_M.glsl`, vendored unmodified
- Author: bloc97
- Licence: MIT. The copyright and permission notice is kept in the header of each file.
- Source: https://github.com/bloc97/Anime4K, commit `7684e9586f8dcc738af08a1cdceb024cc184f426`
