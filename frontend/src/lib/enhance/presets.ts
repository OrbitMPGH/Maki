import restoreM from './shaders/Anime4K_Restore_CNN_M.glsl?raw'
import upscaleM from './shaders/Anime4K_Upscale_CNN_x2_M.glsl?raw'
import upscaleS from './shaders/Anime4K_Upscale_CNN_x2_S.glsl?raw'

export type EnhancePreset = 'a4k-s' | 'a4k-m' | 'a4k-a'

export const PRESET_FILES: Record<EnhancePreset, readonly string[]> = {
  'a4k-s': [upscaleS],
  'a4k-m': [upscaleM],
  'a4k-a': [restoreM, upscaleM],
}
