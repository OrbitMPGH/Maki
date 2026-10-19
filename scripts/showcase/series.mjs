// MangaBaka ids of well known series, taken from the top of the catalogue's popularity ranking.
export const SERIES = [
  3397, 377, 725, 4024, 2813, 1677, 6199, 6029, 3403, 2060, 7423, 1824, 8312, 1357, 1288, 3248,
  6367, 677, 5072, 1451, 8718, 83, 2996, 7346, 10748, 4627, 4323, 2409, 3733, 990, 12864, 604,
  1995, 5391, 2579, 14302, 145, 2207, 242, 3762, 593, 247, 6766, 6089, 2052,
]

// How far through each kind of series the account has got. Weights add up to the library mix.
export const PLAN = {
  finished: 0.2,
  reading: 0.3,
  started: 0.18,
  unread: 0.32,
}
