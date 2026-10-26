import type { PageConversionSample } from '../../api/pageConversion'

/** Which page is on screen, kept by the wizard so going back and forth between steps keeps it. */
export interface CompareChoice {
  custom: PageConversionSample | null
  active: string | null
}

export const emptyCompareChoice: CompareChoice = { custom: null, active: null }

export const sampleKey = (s: PageConversionSample) => `${s.fileId}:${s.page}`
