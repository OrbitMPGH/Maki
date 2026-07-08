import {
  AspectRatio,
  Badge,
  Card,
  Center,
  Group,
  Image,
  Loader,
  SimpleGrid,
  Text,
  Title,
} from '@mantine/core'
import { Link } from 'react-router-dom'
import { useSeries } from '../api/hooks'
import type { SeriesDto } from '../api/types'

const statusColor: Record<string, string> = {
  Ongoing: 'blue',
  Completed: 'green',
  Hiatus: 'yellow',
  Cancelled: 'red',
  Unknown: 'gray',
}

function SeriesCard({ series }: { series: SeriesDto }) {
  return (
    <Card
      component={Link}
      to={`/series/${series.id}`}
      shadow="sm"
      padding="xs"
      radius="md"
      withBorder
    >
      <Card.Section>
        <AspectRatio ratio={2 / 3}>
          {series.coverUrl ? (
            <Image src={series.coverUrl} alt={series.title} fit="cover" />
          ) : (
            <Center bg="dark.6">
              <Text size="sm" c="dimmed" ta="center" px="xs">
                {series.title}
              </Text>
            </Center>
          )}
        </AspectRatio>
      </Card.Section>
      <Text fw={600} size="sm" mt="xs" lineClamp={1} title={series.title}>
        {series.title}
      </Text>
      <Group justify="space-between" mt={4}>
        <Badge size="xs" color={statusColor[series.status] ?? 'gray'} variant="light">
          {series.status}
        </Badge>
        <Text size="xs" c="dimmed">
          {series.chapterFileCount}/{series.chapterCount || '?'}
        </Text>
      </Group>
    </Card>
  )
}

export default function LibraryPage() {
  const { data: series, isLoading, error } = useSeries()

  return (
    <>
      <Title order={2} mb="md">
        Library
      </Title>
      {isLoading && (
        <Center py="xl">
          <Loader />
        </Center>
      )}
      {error && <Text c="red">Failed to load library: {String(error)}</Text>}
      {series && series.length === 0 && (
        <Text c="dimmed">No series yet. Add one from the Add Series page.</Text>
      )}
      {series && series.length > 0 && (
        <SimpleGrid cols={{ base: 2, xs: 3, sm: 4, md: 5, lg: 6, xl: 8 }}>
          {series.map((s) => (
            <SeriesCard key={s.id} series={s} />
          ))}
        </SimpleGrid>
      )}
    </>
  )
}
