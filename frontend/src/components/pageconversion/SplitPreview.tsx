import { Box, Group, Skeleton, Stack, Text } from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { usePageSplitPreview, usePreviewPage } from '../../api/pageConversion'
import { formatNumber } from '../../format'

const HEIGHT_DEBOUNCE_MS = 400
const STRIP_WIDTH = 140
const VIEW_HEIGHT = 420

/**
 * The library's tallest page at a narrow width, scrollable, with a line where each cut would go, so
 * the admin can see the cuts land in the gutters between panels.
 */
export function SplitPreview({ height }: { height: number }) {
  const { t } = useLingui()
  const [settled] = useDebouncedValue(height, HEIGHT_DEBOUNCE_MS)
  const preview = usePageSplitPreview(settled)
  const data = preview.data
  const image = usePreviewPage(
    data
      ? { fileId: data.sample.fileId, page: data.sample.page, format: 'webp', stop: 'high', pngLossless: false, original: true }
      : null,
  )

  if (preview.isError) {
    return (
      <Text size="sm" c="var(--ink-3)">
        <Trans>No page to preview yet. The preview uses the tallest page in your library.</Trans>
      </Text>
    )
  }

  if (!data) {
    return <Skeleton height={VIEW_HEIGHT} width={STRIP_WIDTH} radius="sm" />
  }

  const { seriesTitle, chapterLabel } = data.sample
  const pageNumber = data.sample.page + 1
  const pageHeight = formatNumber(data.height)
  const pieces = data.cuts.length + 1
  const scaled = (STRIP_WIDTH * data.height) / data.width

  return (
    <Group align="flex-start" gap="md" wrap="nowrap">
      <Box
        aria-label={t`Tallest page with its cuts`}
        role="group"
        style={{
          width: STRIP_WIDTH + 2,
          maxHeight: VIEW_HEIGHT,
          overflowY: 'auto',
          flexShrink: 0,
          border: '1px solid var(--border)',
          borderRadius: 'var(--mantine-radius-sm)',
          background: 'var(--surface-sunken)',
          opacity: preview.isPlaceholderData ? 0.6 : 1,
        }}
      >
        <Box pos="relative" w={STRIP_WIDTH} h={scaled}>
          {image.data && (
            <img
              src={image.data.url}
              alt={t`Tallest page`}
              width={STRIP_WIDTH}
              height={scaled}
              style={{ display: 'block', width: STRIP_WIDTH, height: scaled }}
            />
          )}
          {data.cuts.map((cut, i) => (
            <Box
              key={cut}
              data-testid="split-cut"
              pos="absolute"
              left={0}
              right={0}
              style={{
                top: `${(cut / data.height) * 100}%`,
                borderTop: '2px dashed var(--brand)',
              }}
            >
              <Text
                size="xs"
                fw={600}
                px={4}
                style={{ position: 'absolute', right: 2, top: 2, background: 'var(--surface)', color: 'var(--brand)', borderRadius: 4 }}
              >
                {i + 2}
              </Text>
            </Box>
          ))}
        </Box>
      </Box>
      <Stack gap={4}>
        <Text size="sm" fw={500}>
          {seriesTitle}
        </Text>
        <Text size="sm" c="var(--ink-3)">
          <Trans>
            {chapterLabel}, page {pageNumber}, {pageHeight} px tall
          </Trans>
        </Text>
        <Text size="sm">
          {data.cuts.length === 0 ? (
            <Trans>Stays one page at this height.</Trans>
          ) : (
            <Plural value={pieces} one="Becomes # page." other="Becomes # pages. Each dashed line is a cut." />
          )}
        </Text>
      </Stack>
    </Group>
  )
}
