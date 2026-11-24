import { useLocalStorage } from '@mantine/hooks'

export function useFullWidthSeriesPage() {
  return useLocalStorage({
    key: 'maki-series-page-full-width',
    defaultValue: false,
    getInitialValueInEffect: false,
    deserialize: (value) => value === 'true',
  })
}
